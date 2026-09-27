// Package relay 子文件 server：relayd 主服务。
//
// 职责（P3）：
//  1. 双监听：QUIC（UDP，ALPN desklink-relay-v1）+ TCP/TLS（同端口号 TCP）。
//  2. 会话建立：读控制帧 RelayHello → 调 registry 验签 → Register。
//  3. 接线：读 RelayDial → 校验配对（registry）→ Wire → Pump。
//  4. 宽限窗：断线保留 30s；同 device_id 重连复用。
//  5. 撤销踢线：订阅 regnotify，收到 pair_revoked / device_key_rotated 即踢。
//  6. 限速：每 IP 的连接尝试与 verify-challenge 转发限速。
//
// relay 对业务字节零可见：接线后只 Pump 密文。
package relay

import (
	"context"
	"crypto/tls"
	"encoding/hex"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"sync"
	"time"

	"github.com/quic-go/quic-go"

	"desklink/vps/internal/proto"
)

// ServerConfig 是 relayd 运行参数。
type ServerConfig struct {
	// ListenAddr 形如 ":9443"。QUIC 绑 UDP、TCP/TLS 绑 TCP 同端口号。
	ListenAddr string
	// RegistryBaseURL 是 registryd loopback 地址（http://127.0.0.1:7860）。
	RegistryBaseURL string
	// RegistryToken 是 regapi Bearer token。
	RegistryToken string
	// TLSCert / TLSKey 是 TCP/TLS 路径的服务端证书（PEM 文件路径）。
	TLSCertPath string
	TLSKeyPath  string
	// AllowInsecureTLS 为 true 时跳过 TLS 证书加载，用自签（仅供测试）。
	AllowInsecureTLS bool
	// MaxConnPerIP 每 IP 并发连接上限。
	MaxConnPerIP int
}

// Server 是 relayd 服务实例。
type Server struct {
	cfg     ServerConfig
	log     *slog.Logger
	wiring  *Wiring
	reg     *RegClient
	limiter *IPLimiter
	// replayGuard 防同一挑战签名在时间窗内重放（见 challenge.go）。
	replayGuard *ChallengeReplayGuard

	quicLn *quic.Listener
	tcpLn  net.Listener
	tlsCfg *tls.Config

	// deviceCache 是 device_id → DeviceInfo 的内存缓存（验签需要 pubkey）。
	// 由 ListDevices 定期/按需刷新；命中则不查 registry。
	devMu       sync.RWMutex
	deviceCache map[string]DeviceInfo

	closeOnce sync.Once
	closed    chan struct{}
	wg        sync.WaitGroup
}

// NewServer 构造 relayd 服务。
func NewServer(cfg ServerConfig, reg *RegClient, log *slog.Logger) *Server {
	if cfg.MaxConnPerIP <= 0 {
		cfg.MaxConnPerIP = 64
	}
	s := &Server{
		cfg:         cfg,
		log:         log,
		wiring:      NewWiring(func(f string, a ...any) { log.Debug(fmt.Sprintf(f, a...)) }),
		reg:         reg,
		limiter:     NewIPLimiter(cfg.MaxConnPerIP),
		replayGuard: NewChallengeReplayGuard(),
		deviceCache: make(map[string]DeviceInfo),
		closed:      make(chan struct{}),
	}
	return s
}

// Wiring 暴露接线表（测试用）。
func (s *Server) Wiring() *Wiring { return s.wiring }

// Run 启动双监听并阻塞直到 ctx 取消。
func (s *Server) Run(ctx context.Context) error {
	if err := s.loadTLS(); err != nil {
		return err
	}
	if err := s.listenQUIC(); err != nil {
		return fmt.Errorf("relay: quic listen: %w", err)
	}
	if err := s.listenTCP(); err != nil {
		return fmt.Errorf("relay: tcp listen: %w", err)
	}
	s.log.Info("relayd: listening",
		"addr", s.cfg.ListenAddr,
		"quic", "udp",
		"tcp_tls", "tcp")

	// 后台：宽限窗清理 + 设备缓存刷新
	s.wg.Add(2)
	go s.graceSweeper(ctx)
	go s.deviceCacheRefresher(ctx)

	// ctx 取消 → 主动关闭监听，让两个 accept 循环退出。
	//
	// 为什么需要这一步（早期实现的坑）：Run 曾把 acceptTCP 放在**当前 goroutine**
	// 里阻塞，而 Close() 又在 <-ctx.Done() 之后才调用。于是 ctx 取消后 Run 会一直
	// 卡在 Accept 上，必须靠"外部再额外调一次 Close()"才能退出——cmd/relayd 恰好
	// 这么做了，所以问题被掩盖；任何只 cancel ctx 的调用方都会挂死。
	s.wg.Add(1)
	go func() {
		defer s.wg.Done()
		select {
		case <-ctx.Done():
			_ = s.Close()
		case <-s.closed:
		}
	}()

	// 两个 accept 循环都放后台，Run 阻塞到监听被关闭。
	s.wg.Add(2)
	go func() {
		defer s.wg.Done()
		s.acceptQUIC(ctx)
	}()
	go func() {
		defer s.wg.Done()
		s.acceptTCP(ctx)
	}()

	<-s.closed
	// 等在途会话收尾：Pump / readCtl / WaitUnwired 都会在 ctx 取消后及时退出
	// （transport.Recv 把 ctx 取消映射为"立刻到期"的读超时，见 transport.go）。
	s.wg.Wait()
	return nil
}

// loadTLS 准备 TCP/TLS 证书。
func (s *Server) loadTLS() error {
	if s.cfg.AllowInsecureTLS {
		cert, err := selfSignedCert()
		if err != nil {
			return fmt.Errorf("relay: self-signed cert: %w", err)
		}
		s.tlsCfg = &tls.Config{
			Certificates: []tls.Certificate{cert},
			MinVersion:   tls.VersionTLS12,
			NextProtos:   []string{alpnRelay},
		}
		return nil
	}
	if s.cfg.TLSCertPath == "" || s.cfg.TLSKeyPath == "" {
		return errors.New("relay: TLS cert/key paths required (or set AllowInsecureTLS for tests)")
	}
	cert, err := tls.LoadX509KeyPair(s.cfg.TLSCertPath, s.cfg.TLSKeyPath)
	if err != nil {
		return fmt.Errorf("relay: load keypair: %w", err)
	}
	s.tlsCfg = &tls.Config{
		Certificates: []tls.Certificate{cert},
		MinVersion:   tls.VersionTLS12,
		NextProtos:   []string{alpnRelay},
	}
	return nil
}

// alpnRelay 与 C# QuicTransport / TcpTlsTransport 协商的 ALPN 一致。
const alpnRelay = "desklink-relay-v1"

func (s *Server) listenQUIC() error {
	tlsCfg := s.tlsCfg.Clone()
	tlsCfg.NextProtos = []string{alpnRelay}
	ln, err := quic.ListenAddr(s.cfg.ListenAddr, tlsCfg, &quic.Config{
		MaxIdleTimeout:        60 * time.Second,
		MaxIncomingStreams:    256,
		MaxIncomingUniStreams: 256,
		KeepAlivePeriod:       20 * time.Second,
	})
	if err != nil {
		return err
	}
	s.quicLn = ln
	return nil
}

func (s *Server) listenTCP() error {
	ln, err := net.Listen("tcp", s.cfg.ListenAddr)
	if err != nil {
		return err
	}
	s.tcpLn = ln
	return nil
}

// ---------- accept 循环 ----------

func (s *Server) acceptQUIC(ctx context.Context) {
	for {
		conn, err := s.quicLn.Accept(ctx)
		if err != nil {
			select {
			case <-s.closed:
				return
			default:
			}
			if errors.Is(err, context.Canceled) {
				return
			}
			s.log.Warn("relay: quic accept", "err", err)
			continue
		}
		s.wg.Add(1)
		go func() {
			defer s.wg.Done()
			s.handleQUICConn(ctx, conn)
		}()
	}
}

func (s *Server) acceptTCP(ctx context.Context) {
	for {
		raw, err := s.tcpLn.Accept()
		if err != nil {
			select {
			case <-s.closed:
				return
			default:
			}
			s.log.Warn("relay: tcp accept", "err", err)
			continue
		}
		s.wg.Add(1)
		go func() {
			defer s.wg.Done()
			s.handleTCPConn(ctx, raw)
		}()
	}
}

// handleQUICConn 处理一条 QUIC 连接：接受其第一条双向流作为会话字节流。
func (s *Server) handleQUICConn(ctx context.Context, conn *quic.Conn) {
	defer conn.CloseWithError(0, "")

	// 按 IP 限并发连接。早期只在 TCP 路径做限速，QUIC 完全没有——
	// 而 QUIC 才是默认优先的传输，等于主要入口没有保护。
	ip := hostOf(conn.RemoteAddr())
	if !s.limiter.Allow(ip) {
		s.log.Warn("relay: quic connection rate limited", "ip", ip)
		return
	}
	defer s.limiter.Release(ip)

	stream, err := conn.AcceptStream(ctx)
	if err != nil {
		s.log.Warn("relay: quic accept stream", "err", err)
		return
	}
	sc := newQUICConn(conn, stream)
	s.serveSession(ctx, sc)
}

// handleTCPConn 处理一条 TCP 连接：跑 TLS 握手。
func (s *Server) handleTCPConn(ctx context.Context, raw net.Conn) {
	// 限速：按远端 IP
	ip := hostOf(raw.RemoteAddr())
	if !s.limiter.Allow(ip) {
		s.log.Warn("relay: tcp connection rate limited", "ip", ip)
		_ = raw.Close()
		return
	}
	// 名额持有到**会话结束**（而不是握手结束）。
	// MaxConnPerIP 的语义是"每 IP 并发连接上限"；早期在 TLS 握手后就 Release，
	// 实际只限制了并发握手数，在线连接数完全不受控。
	defer s.limiter.Release(ip)

	tlsConn := tls.Server(raw, s.tlsCfg)
	hsCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	if err := tlsConn.HandshakeContext(hsCtx); err != nil {
		s.log.Warn("relay: tls handshake", "ip", ip, "err", err)
		_ = raw.Close()
		return
	}
	sc := newStreamConn(tlsConn, "tcp-tls")
	s.serveSession(ctx, sc)
}

// ---------- 会话状态机 ----------

// serveSession 处理一条已建立的传输：Hello → (可选)Dial → Pump。
func (s *Server) serveSession(ctx context.Context, c Conn) {
	defer func() { _ = c.Close() }()
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()

	// 1. 读 Hello
	deviceID, hint, err := s.readHello(ctx, c)
	if err != nil {
		s.log.Warn("relay: hello failed", "addr", c.RemoteAddr(), "err", err)
		return
	}

	// 2. 登记（可能顶替旧连接）
	if replaced := s.wiring.Register(deviceID, hint, c); replaced != nil {
		s.sendCtl(ctx, replaced, proto.KindRelayBye, mustStatus(false, proto.ReasonAlreadyWired))
		_ = replaced.Close()
	}
	// 无论后续如何，退出时注销本会话。
	// 必须带上 c：若期间已被"同 device_id 的新连接"顶替，本次注销不应误删新会话。
	defer s.wiring.Unregister(deviceID, c)

	// 3. 确认 Hello
	if err := s.writeCtl(ctx, c, proto.KindRelayHelloAck, mustStatus(true, proto.ReasonOK)); err != nil {
		return
	}

	// 4. 等待 Dial（或保活/断开）
	for {
		frame, err := s.readCtl(ctx, c)
		if err != nil {
			return
		}
		switch frame.Kind {
		case proto.KindRelayPing:
			_ = s.writeCtl(ctx, c, proto.KindRelayPong, nil)
		case proto.KindRelayBye:
			return
		case proto.KindRelayDial:
			peer, derr := proto.DecodeRelayDial(frame.Payload)
			if derr != nil {
				_ = s.writeCtl(ctx, c, proto.KindRelayDialResult, mustStatus(false, proto.ReasonBadFrame))
				return
			}
			reason, newly := s.tryWire(ctx, c, deviceID, peer)
			if reason != proto.ReasonOK {
				_ = s.writeCtl(ctx, c, proto.KindRelayDialResult, mustStatus(false, reason))
				s.log.Info("relay: dial rejected", "self", short(deviceID), "peer", short(peer), "reason", reason)
				// 不立即断：让客户端自己决定重试；但同一连接不应再 dial。
				continue
			}
			// 接线成功：先发 Ready，再发 DialResult(ok)（同 goroutine 顺序写）。
			// 客户端据此进入业务阶段。
			if err := s.writeReadyThenDialResult(ctx, c, peer); err != nil {
				return
			}
			if !newly {
				// 对端（先到者）已持有该配对唯一的双向泵。
				//
				// 本端只做两件事：
				//   1. 关闭 dialGate，让先到者确认"我也已退出控制读循环"；
				//   2. 保持连接存活但**不再读取任何字节**，直到接线结束。
				//
				// 关键：这里绝不能再起第二个泵——relay 的 Pump 本就是双向的，
				// 第二个泵会让同一条连接被两个 goroutine 并发读取，造成字节乱序，
				// 进而破坏 E2E 层严格递增的 AEAD 计数器（表现为解密失败）。
				s.wiring.SignalDialGate(deviceID)
				s.log.Info("relay: session already wired by peer, waiting",
					"self", short(deviceID), "peer", short(peer))
				s.wiring.WaitUnwired(ctx, deviceID)
				return
			}
			// 先到者：等对端也完成 Dial（退出其控制读循环）后再起泵，
			// 保证"同一连接任一时刻只有一个读者"。
			if !s.wiring.WaitDialGate(ctx, deviceID, dialGateTimeout) {
				s.log.Warn("relay: dial gate timeout, pumping anyway", "self", short(deviceID), "peer", short(peer))
			}
			peerConn := s.wiring.Lookup(peer)
			if peerConn == nil {
				// 竞态：wire 后对端刚断
				return
			}
			s.log.Info("relay: session wired", "a", short(deviceID), "b", short(peer))
			Pump(ctx, c, peerConn)
			return
		default:
			_ = s.writeCtl(ctx, c, proto.KindRelayBye, mustStatus(false, proto.ReasonBadFrame))
			return
		}
	}
}

// dialGateTimeout 是先到者等待"对端也完成 Dial"的最长时间。
// 超时后仍会开始泵送（best-effort），避免对端只 Hello 不 Dial 时永久卡住。
const dialGateTimeout = 5 * time.Second

// tryWire 执行接线校验与接线。
//
// 返回 (reason, newlyWired)：
//   - reason == ReasonOK 且 newlyWired == true  → 本次调用完成了接线，调用方是唯一泵持有者；
//   - reason == ReasonOK 且 newlyWired == false → 配对已由对端接线（双侧 dial 幂等）；
//   - 其他 reason 为失败原因。
func (s *Server) tryWire(ctx context.Context, self Conn, deviceID, peer [proto.DeviceIDSize]byte) (string, bool) {
	if deviceID == peer {
		return proto.ReasonBadFrame, false
	}
	// 对端必须在线
	peerConn := s.wiring.Lookup(peer)
	if peerConn == nil {
		return proto.ReasonPeerOffline, false
	}
	// 配对校验：查 registry
	chk, err := s.reg.GetActivePair(ctx, deviceID, peer)
	if err != nil {
		s.log.Warn("relay: pair check failed", "err", err)
		return proto.ReasonInternal, false
	}
	if !chk.Found {
		return proto.ReasonNotPaired, false
	}
	// 接线（互斥：两端各自 dial 时只有先到者成功）
	newly, werr := s.wiring.Wire(deviceID, peer)
	if werr != nil {
		// 已接线：视为成功（幂等），避免双侧 dial 竞态导致一方报错。
		if s.wiring.PeerOf(deviceID) == peer {
			return proto.ReasonOK, false
		}
		return proto.ReasonInternal, false
	}
	return proto.ReasonOK, newly
}

// writeReadyThenDialResult 向"本会话"依次写 PeerEvent(Ready) 与 DialResult(ok)。
//
// 为什么 Ready 必须由本会话自己的 goroutine 写：
//   - 客户端在 Dial 阶段会跳过 PeerEvent、只等 DialResult；若 Ready 晚于
//     DialResult 到达，就会被当成业务数据，污染密文流。
//   - 若改由"对端会话的 goroutine"代发（早期实现如此），两条写会并发落到
//     同一条连接上，顺序无法保证——这正是 QUIC 路径上偶发的
//     "A 收到 PeerEvent 字节而不是密文"的根因。
//
// 因此这里坚持"一条连接在同一时刻只有一个写者"：Ready 与 DialResult 同源同序。
func (s *Server) writeReadyThenDialResult(ctx context.Context, c Conn, peer [proto.DeviceIDSize]byte) error {
	ready, err := proto.EncodeRelayCtl(proto.KindRelayPeerEvent, proto.EncodeRelayPeerEvent(proto.PeerEventReady, peer))
	if err != nil {
		return err
	}
	if err := s.writeCtlRaw(ctx, c, ready); err != nil {
		return err
	}
	return s.writeCtl(ctx, c, proto.KindRelayDialResult, mustStatus(true, proto.ReasonOK))
}

// ---------- Hello 读取与验签 ----------

func (s *Server) readHello(ctx context.Context, c Conn) ([proto.DeviceIDSize]byte, [16]byte, error) {
	var zero [proto.DeviceIDSize]byte
	var zhint [16]byte
	frame, err := s.readCtl(ctx, c)
	if err != nil {
		return zero, zhint, err
	}
	if frame.Kind != proto.KindRelayHello {
		_ = s.writeCtl(ctx, c, proto.KindRelayHelloAck, mustStatus(false, proto.ReasonBadFrame))
		return zero, zhint, fmt.Errorf("expected hello, got kind 0x%02x", byte(frame.Kind))
	}
	hello, err := proto.DecodeRelayHello(frame.Payload)
	if err != nil {
		return zero, zhint, err
	}
	if hello.ProtoVersion != proto.ProtoVersion {
		_ = s.writeCtl(ctx, c, proto.KindRelayHelloAck, mustStatus(false, proto.ReasonProtoMismatch))
		return zero, zhint, fmt.Errorf("proto version mismatch: %d", hello.ProtoVersion)
	}
	// 按 IP 限速（挑战尝试）
	ip := remoteIP(c.RemoteAddr())
	if !s.limiter.AllowChallenge(ip) {
		_ = s.writeCtl(ctx, c, proto.KindRelayHelloAck, mustStatus(false, proto.ReasonRateLimited))
		return zero, zhint, errors.New("challenge rate limited")
	}
	// 取设备 pubkey（缓存优先）
	info, err := s.deviceInfo(ctx, hello.DeviceID)
	if err != nil {
		_ = s.writeCtl(ctx, c, proto.KindRelayHelloAck, mustStatus(false, proto.ReasonAuthFailed))
		return zero, zhint, fmt.Errorf("device lookup: %w", err)
	}
	if info.Revoked {
		_ = s.writeCtl(ctx, c, proto.KindRelayHelloAck, mustStatus(false, proto.ReasonRevoked))
		return zero, zhint, errors.New("device revoked")
	}
	pk, err := proto.ParsePublicKeyHex(info.PubKeyHex)
	if err != nil {
		return zero, zhint, fmt.Errorf("bad stored pubkey: %w", err)
	}
	// 交叉核对：Hello 声明的 device_id 必须等于 pubkey 推导出的 device_id
	if pk.DeviceID() != hello.DeviceID {
		_ = s.writeCtl(ctx, c, proto.KindRelayHelloAck, mustStatus(false, proto.ReasonAuthFailed))
		return zero, zhint, errors.New("device_id / pubkey mismatch")
	}
	// 验签用 Hello 提供的 nonce；但协议里 nonce 不在 Hello 明文里——
	// 客户端把 nonce 编进签名 transcript。relay 无法从 Hello 恢复 nonce，
	// 因此 nonce 由客户端随 Hello 一起给出：见 helloNonce 字段（proto 扩展）。
	if err := VerifyDeviceChallenge(hello.ChallengeID, hello.ChallengeUnix, hello.Nonce, hello.DeviceID, pk, hello.Signature); err != nil {
		_ = s.writeCtl(ctx, c, proto.KindRelayHelloAck, mustStatus(false, proto.ReasonAuthFailed))
		return zero, zhint, err
	}
	// 验签通过后查重：同一 (challengeID, ts, nonce, device) 只允许出现一次。
	// 防的是"抓包重放一条合法 Hello 冒充设备重连"——签名本身无状态，
	// 不去重的话重放在 ±120s 时间窗内每次都能通过验签。
	if !s.replayGuard.CheckAndRecord(hello.ChallengeID, hello.ChallengeUnix, hello.Nonce, hello.DeviceID) {
		_ = s.writeCtl(ctx, c, proto.KindRelayHelloAck, mustStatus(false, proto.ReasonAuthFailed))
		return zero, zhint, errors.New("challenge replay detected")
	}
	return hello.DeviceID, hello.DeviceIDHint, nil
}

// deviceInfo 取设备信息（内存缓存优先；miss 则查 registry 并回填）。
func (s *Server) deviceInfo(ctx context.Context, id [proto.DeviceIDSize]byte) (DeviceInfo, error) {
	key := hex.EncodeToString(id[:])
	s.devMu.RLock()
	info, ok := s.deviceCache[key]
	s.devMu.RUnlock()
	if ok {
		return info, nil
	}
	info2, err := s.reg.GetDeviceByID(ctx, key)
	if err != nil {
		return DeviceInfo{}, err
	}
	s.devMu.Lock()
	s.deviceCache[key] = *info2
	s.devMu.Unlock()
	return *info2, nil
}

// refreshDeviceCache 全量刷新设备缓存。
func (s *Server) refreshDeviceCache(ctx context.Context) {
	devs, err := s.reg.ListDevices(ctx)
	if err != nil {
		s.log.Warn("relay: refresh device cache failed", "err", err)
		return
	}
	m := make(map[string]DeviceInfo, len(devs))
	for _, d := range devs {
		m[d.DeviceIDHex] = d
	}
	s.devMu.Lock()
	s.deviceCache = m
	s.devMu.Unlock()
}

func (s *Server) deviceCacheRefresher(ctx context.Context) {
	defer s.wg.Done()
	s.refreshDeviceCache(ctx)
	t := time.NewTicker(30 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-s.closed:
			return
		case <-t.C:
			s.refreshDeviceCache(ctx)
		}
	}
}

func (s *Server) graceSweeper(ctx context.Context) {
	defer s.wg.Done()
	t := time.NewTicker(5 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-s.closed:
			return
		case <-t.C:
			if n := s.wiring.SweepGrace(); n > 0 {
				s.log.Debug("relay: swept grace entries", "count", n)
			}
		}
	}
}

// ---------- 撤销 / 密钥轮换处理（由 regnotify 订阅者驱动）----------

// HandleNotifyEvent 处理一条 registry 推送事件（relay 侧）。
//
// 由 cmd/relayd 把 regnotify.Subscriber 的事件转到这里：
//   - pair_revoked        → KickPair（两端一起踢）
//   - device_key_rotated  → KickDevice + 刷新该设备缓存
func (s *Server) HandleNotifyEvent(ev EventLike) {
	switch ev.Kind() {
	case "pair_revoked":
		a, errA := parseDeviceHex(ev.DeviceAHex())
		b, errB := parseDeviceHex(ev.DeviceBHex())
		if errA != nil || errB != nil {
			s.log.Warn("relay: bad pair_revoked event", "a", ev.DeviceAHex(), "b", ev.DeviceBHex())
			return
		}
		s.log.Info("relay: pair revoked, kicking", "a", short(a), "b", short(b), "reason", ev.Reason())
		s.wiring.KickPair(a, b)
	case "device_key_rotated":
		d, err := parseDeviceHex(ev.DeviceIDHex())
		if err != nil {
			s.log.Warn("relay: bad key_rotated event", "id", ev.DeviceIDHex())
			return
		}
		s.log.Info("relay: device key rotated, kicking", "device", short(d))
		s.wiring.KickDevice(d)
		// 失效缓存，下次 Hello 重新拉取新 pubkey
		s.devMu.Lock()
		delete(s.deviceCache, ev.DeviceIDHex())
		s.devMu.Unlock()
	default:
		// pair_added / pairing_code_issued 不影响在线会话
	}
}

// EventLike 抽象 regnotify.Event 的最小字段集，避免 relay 包直接依赖 regnotify
// （便于测试注入假事件）。
type EventLike interface {
	Kind() string
	DeviceAHex() string
	DeviceBHex() string
	DeviceIDHex() string
	Reason() string
}

// ---------- 控制帧读写 ----------

// ctlIdleTimeout 是控制阶段的空闲超时：进入控制读循环后超过该时长仍未读到
// 一帧，就断开（防"只连接不发帧"占坑）。
const ctlIdleTimeout = 60 * time.Second

// readCtl 读一帧控制帧（带 ctlIdleTimeout 空闲超时）。
//
// 超时实现要点：给 Recv 传一个**带 deadline 的 ctx**，由 transport 把它映射成
// 套接字读超时。早期实现是"在循环里检查 time.Now().After(deadline)"，但那个检查
// 位于阻塞的 Recv 之前——Recv 不返回就永远不会被执行，实际只能等底层 45s 读超时
// 先返回才可能被检查到（注释写 30s、代码写 60s、实测约 90s，三者都不一致）。
func (s *Server) readCtl(ctx context.Context, c Conn) (*proto.RelayCtl, error) {
	readCtx, cancel := context.WithTimeout(ctx, ctlIdleTimeout)
	defer cancel()

	// 控制帧最大 RelayCtlMaxPayload(4KB) + 帧头；缓冲超过它说明对端在灌数据。
	const maxCtlBuf = proto.RelayCtlMaxPayload + proto.RelayCtlFrameSize

	buf := make([]byte, 0, 512)
	tmp := make([]byte, 512)
	for {
		frame, consumed, err := proto.TryDecodeRelayCtl(buf)
		if err != nil {
			return nil, err
		}
		if frame != nil {
			// 注意：控制帧阶段不应有剩余字节；遗留字节说明客户端提前发了业务流。
			if consumed < len(buf) {
				return nil, errors.New("relay: unexpected trailing bytes before wiring")
			}
			return frame, nil
		}
		if len(buf) > maxCtlBuf {
			return nil, errors.New("relay: control buffer overflow")
		}
		n, err := c.Recv(readCtx, tmp)
		if n > 0 {
			buf = append(buf, tmp[:n]...)
		}
		if err != nil {
			return nil, err
		}
	}
}

func (s *Server) writeCtl(ctx context.Context, c Conn, kind proto.RelayKind, payload []byte) error {
	frame, err := proto.EncodeRelayCtl(kind, payload)
	if err != nil {
		return err
	}
	return c.Send(ctx, frame)
}

// writeCtlRaw 直接写一个"已编码"的控制帧。
// 供需要精确控制多帧写入顺序的调用点使用（如 Ready + DialResult 必须同源同序）。
func (s *Server) writeCtlRaw(ctx context.Context, c Conn, frame []byte) error {
	return c.Send(ctx, frame)
}

func (s *Server) sendCtl(ctx context.Context, c Conn, kind proto.RelayKind, payload []byte) {
	_ = s.writeCtl(ctx, c, kind, payload)
}

func mustStatus(ok bool, reason string) []byte {
	b, _ := proto.EncodeRelayStatus(ok, reason)
	return b
}

// ---------- 关闭 ----------

// Close 关闭所有监听。
func (s *Server) Close() error {
	s.closeOnce.Do(func() {
		close(s.closed)
		if s.quicLn != nil {
			_ = s.quicLn.Close()
		}
		if s.tcpLn != nil {
			_ = s.tcpLn.Close()
		}
	})
	return nil
}

// ---------- 小工具 ----------

func hostOf(addr net.Addr) string {
	if addr == nil {
		return ""
	}
	return remoteIP(addr.String())
}

// remoteIP 从 "host:port" 提取 host；已是纯 host 时原样返回。
func remoteIP(s string) string {
	if h, _, err := net.SplitHostPort(s); err == nil {
		return h
	}
	return s
}

func parseDeviceHex(h string) ([proto.DeviceIDSize]byte, error) {
	return proto.ParseDeviceIDHex(h)
}
