package relay

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"desklink/vps/internal/proto"
)

// fakeRegistry 是测试用 registry 替身：实现 relay.RegClient 依赖的 4 个 HTTP 端点。
//
// 之所以用 HTTP 替身而不是 mock RegClient：
//   - RegClient 是具体类型（非接口），保证 relay 真的走 HTTP 路径；
//   - 能顺带验证 regapi 的 wire 格式（snake_case 字段名）与 relay 的解析一致。
type fakeRegistry struct {
	mu      sync.Mutex
	devices map[string]DeviceInfo // deviceIDHex → info
	pairs   map[string][]string   // deviceIDHex → peer deviceIDHex 列表
	token   string
	srv     *httptest.Server
}

func newFakeRegistry(t *testing.T, token string) *fakeRegistry {
	t.Helper()
	f := &fakeRegistry{
		devices: make(map[string]DeviceInfo),
		pairs:   make(map[string][]string),
		token:   token,
	}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /v1/devices", f.handleList)
	mux.HandleFunc("GET /v1/devices/{id}/pairs", f.handlePairs)
	mux.HandleFunc("POST /v1/devices/verify-challenge", f.handleVerify)
	f.srv = httptest.NewServer(f.auth(mux))
	t.Cleanup(f.srv.Close)
	return f
}

func (f *fakeRegistry) auth(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer "+f.token {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (f *fakeRegistry) handleList(w http.ResponseWriter, r *http.Request) {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []DeviceInfo
	for _, d := range f.devices {
		out = append(out, d)
	}
	_ = json.NewEncoder(w).Encode(map[string]any{"devices": out})
}

func (f *fakeRegistry) handlePairs(w http.ResponseWriter, r *http.Request) {
	f.mu.Lock()
	defer f.mu.Unlock()
	id := r.PathValue("id")
	var pairs []map[string]string
	for _, peer := range f.pairs[id] {
		pairs = append(pairs, map[string]string{"pair_uuid": "uuid-" + id[:8], "peer_device_id_hex": peer})
	}
	_ = json.NewEncoder(w).Encode(map[string]any{"pairs": pairs})
}

// handleVerify 只做最基础的字段回显校验（真实验签由 registry 做；本测试的重心
// 是 relay 的数据面，不是 registry 的鉴权逻辑）。
func (f *fakeRegistry) handleVerify(w http.ResponseWriter, r *http.Request) {
	var body struct {
		DeviceIDHex string `json:"device_id_hex"`
		PubKey      string `json:"ed25519_pubkey"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		w.WriteHeader(http.StatusBadRequest)
		return
	}
	_ = json.NewEncoder(w).Encode(map[string]any{"ok": true, "device_id_hex": body.DeviceIDHex})
}

// addDevice 登记一台设备（计算 device_id 并写入表）。
func (f *fakeRegistry) addDevice(pub proto.PublicKey, revoked bool) string {
	id := pub.DeviceID()
	idHex := hex.EncodeToString(id[:])
	f.mu.Lock()
	defer f.mu.Unlock()
	f.devices[idHex] = DeviceInfo{
		DeviceIDHex:    idHex,
		PubKeyHex:      pub.Hex(),
		FingerprintHex: pub.FingerprintHex(),
		Revoked:        revoked,
	}
	return idHex
}

// addPair 建立双向配对关系。
func (f *fakeRegistry) addPair(a, b string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.pairs[a] = append(f.pairs[a], b)
	f.pairs[b] = append(f.pairs[b], a)
}

// genKeyPair 生成一对 (Ed25519 pub, 签名函数)，用于构造合法 Hello。
func genKeyPair(t *testing.T) (proto.PublicKey, ed25519.PrivateKey) {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	var pk proto.PublicKey
	copy(pk[:], pub)
	return pk, priv
}

// buildHello 构造一个签名合法的 RelayHello 控制帧。
func buildHello(t *testing.T, pk proto.PublicKey, priv ed25519.PrivateKey, serverUnix *int64) []byte {
	t.Helper()
	id := pk.DeviceID()
	var hint [16]byte
	hint[0] = 0x01
	nonce := make([]byte, 32)
	if _, err := rand.Read(nonce); err != nil {
		t.Fatal(err)
	}
	var chalID uint32 = 7
	ts := time.Now().Unix()
	tr, err := ChallengeTranscript(chalID, ts, nonce, id, pk)
	if err != nil {
		t.Fatal(err)
	}
	sig := ed25519.Sign(priv, tr)
	payload, err := proto.EncodeRelayHello(proto.ProtoVersion, id, hint, chalID, ts, nonce, sig)
	if err != nil {
		t.Fatal(err)
	}
	frame, err := proto.EncodeRelayCtl(proto.KindRelayHello, payload)
	if err != nil {
		t.Fatal(err)
	}
	return frame
}

// startTestServer 启动一个 relay.Server（TCP/TLS 自签 + QUIC），返回其地址与清理函数。
func startTestServer(t *testing.T, reg *fakeRegistry, token string) (*Server, string) {
	t.Helper()
	log := slog.New(slog.NewTextHandler(io.Discard, nil))
	cfg := ServerConfig{
		ListenAddr:       "127.0.0.1:0",
		RegistryBaseURL:  reg.srv.URL,
		RegistryToken:    token,
		AllowInsecureTLS: true,
		MaxConnPerIP:     64,
	}
	// 先探测可用端口，再把该端口同时给 QUIC(UDP) 与 TCP 用。
	// 直接 ListenAddr="127.0.0.1:0" 的问题是 QUIC 与 TCP 各自随机选端口，
	// 而生产语义是"同端口号双协议"；测试显式取一个空闲端口更贴近生产。
	port := freePort(t)
	cfg.ListenAddr = net.JoinHostPort("127.0.0.1", port)

	regCli := NewRegClient(reg.srv.URL, token)
	srv := NewServer(cfg, regCli, log)

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		_ = srv.Run(ctx)
	}()

	// 等两个监听都起来：最多 3s
	addr := cfg.ListenAddr
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		c, err := net.DialTimeout("tcp", addr, 200*time.Millisecond)
		if err == nil {
			_ = c.Close()
			break
		}
		time.Sleep(50 * time.Millisecond)
	}

	t.Cleanup(func() {
		cancel()
		_ = srv.Close()
		select {
		case <-done:
		case <-time.After(2 * time.Second):
		}
	})
	return srv, addr
}

// freePort 取一个空闲 TCP 端口号（同时假定 UDP 同号可用，自用测试环境成立）。
func freePort(t *testing.T) string {
	t.Helper()
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	_, port, _ := net.SplitHostPort(l.Addr().String())
	return port
}

// clientSession 是测试用客户端会话：一条字节流 + 控制帧读写。
type clientSession struct {
	conn net.Conn
	t    *testing.T
	kind string
}

func (c *clientSession) writeCtl(t *testing.T, kind proto.RelayKind, payload []byte) {
	t.Helper()
	frame, err := proto.EncodeRelayCtl(kind, payload)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := c.conn.Write(frame); err != nil {
		t.Fatalf("write ctl: %v", err)
	}
}

func (c *clientSession) readCtl(t *testing.T) (*proto.RelayCtl, error) {
	t.Helper()
	// 精确读帧头再按长度读 payload，避免流式解码把半帧留在下一次调用里。
	_ = c.conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	hdr := make([]byte, proto.RelayCtlFrameSize)
	if _, err := io.ReadFull(c.conn, hdr); err != nil {
		return nil, err
	}
	plen := int(hdr[1])<<8 | int(hdr[2])
	if plen > proto.RelayCtlMaxPayload {
		return nil, fmt.Errorf("ctl payload too large: %d", plen)
	}
	payload := make([]byte, plen)
	if plen > 0 {
		if _, err := io.ReadFull(c.conn, payload); err != nil {
			return nil, err
		}
	}
	return &proto.RelayCtl{Kind: proto.RelayKind(hdr[0]), Payload: payload}, nil
}

// TestRelayTCPEndToEnd 验证 TCP/TLS 路径：两台设备经 relay 接线后密文双向互通。
func TestRelayTCPEndToEnd(t *testing.T) {
	token := "test-token-0123456789abcdef"
	reg := newFakeRegistry(t, token)

	pkA, privA := genKeyPair(t)
	pkB, privB := genKeyPair(t)
	idA := reg.addDevice(pkA, false)
	idB := reg.addDevice(pkB, false)
	reg.addPair(idA, idB)

	_, addr := startTestServer(t, reg, token)

	// A、B 都以 TCP/TLS 拨入
	a := dialTCP(t, addr)
	b := dialTCP(t, addr)

	// 双方 Hello
	a.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkA, privA, nil)))
	b.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkB, privB, nil)))

	// 双方各自读 HelloAck
	assertAckOK(t, a)
	assertAckOK(t, b)

	// A dial B；B 也 dial A（双侧 dial 应幂等成功）
	a.writeCtl(t, proto.KindRelayDial, proto.EncodeRelayDial(pkToDevice(pkB)))
	b.writeCtl(t, proto.KindRelayDial, proto.EncodeRelayDial(pkToDevice(pkA)))

	// 两端都应收到至少一个 DialResult(ok)
	if !anyDialOK(t, a) {
		t.Fatal("A did not get ok dial result")
	}
	if !anyDialOK(t, b) {
		t.Fatal("B did not get ok dial result")
	}

	// 现在两端的流已接线：A 写密文，B 应原样收到
	payload := []byte("hello-from-A-encrypted-blob")
	if _, err := a.conn.Write(payload); err != nil {
		t.Fatalf("A write: %v", err)
	}
	got := readN(t, b, len(payload))
	if string(got) != string(payload) {
		t.Fatalf("B got %q, want %q", got, payload)
	}

	// 反向
	back := []byte("reply-from-B")
	if _, err := b.conn.Write(back); err != nil {
		t.Fatalf("B write: %v", err)
	}
	got2 := readN(t, a, len(back))
	if string(got2) != string(back) {
		t.Fatalf("A got %q, want %q", got2, back)
	}
}

// TestRelayTCPNoReorderingUnderDoubleDial 验证"双侧 dial"场景下字节顺序不被破坏。
//
// 背景：relay 的 Pump 是双向的——一侧起泵即可同时搬运两个方向。早期实现里
// 两端各自 dial 会各自起一个泵，于是同一条连接被两个 goroutine 并发读取：
// 若两个读者分别取到相邻的两块数据、且后取到者先写出去，接收端就会看到乱序。
// E2E 层是严格递增的 AEAD 计数器，一旦乱序就会解密失败，所以这是必须守住的不变量。
//
// 修复后只有"先到者"起泵，后到者只保持连接存活；本测试用大量小块 + 小间隔发送
// 来放大"两个读者交替取块"的概率，并逐字节校验顺序。
func TestRelayTCPNoReorderingUnderDoubleDial(t *testing.T) {
	token := "test-token-0123456789abcdef"
	reg := newFakeRegistry(t, token)

	pkA, privA := genKeyPair(t)
	pkB, privB := genKeyPair(t)
	idA := reg.addDevice(pkA, false)
	idB := reg.addDevice(pkB, false)
	reg.addPair(idA, idB)

	_, addr := startTestServer(t, reg, token)

	a := dialTCP(t, addr)
	b := dialTCP(t, addr)

	a.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkA, privA, nil)))
	b.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkB, privB, nil)))
	assertAckOK(t, a)
	assertAckOK(t, b)

	// 双侧 dial（幂等成功）
	a.writeCtl(t, proto.KindRelayDial, proto.EncodeRelayDial(pkToDevice(pkB)))
	b.writeCtl(t, proto.KindRelayDial, proto.EncodeRelayDial(pkToDevice(pkA)))
	if !anyDialOK(t, a) {
		t.Fatal("A did not get ok dial result")
	}
	if !anyDialOK(t, b) {
		t.Fatal("B did not get ok dial result")
	}

	// 构造 60 个 1..60 字节递增的块，块首字节 = 块序号（便于定位乱序位置）。
	const chunks = 60
	var want []byte
	for i := 1; i <= chunks; i++ {
		chunk := make([]byte, i)
		for j := range chunk {
			chunk[j] = byte(i)
		}
		want = append(want, chunk...)
	}

	// 发送端：逐块写，块间留 1ms 间隔以放大并发读者交替取块的窗口。
	writeDone := make(chan error, 1)
	go func() {
		for i := 1; i <= chunks; i++ {
			chunk := make([]byte, i)
			for j := range chunk {
				chunk[j] = byte(i)
			}
			if _, err := a.conn.Write(chunk); err != nil {
				writeDone <- err
				return
			}
			time.Sleep(time.Millisecond)
		}
		writeDone <- nil
	}()

	got := readN(t, b, len(want))
	if err := <-writeDone; err != nil {
		t.Fatalf("A write: %v", err)
	}
	if !bytes.Equal(got, want) {
		// 定位首个不一致的字节，方便排查乱序。
		for i := range want {
			if got[i] != want[i] {
				t.Fatalf("byte stream reordered at offset %d: got 0x%02x want 0x%02x", i, got[i], want[i])
			}
		}
		t.Fatalf("byte stream mismatch: got %d bytes want %d", len(got), len(want))
	}
}

// TestRelayTCPNotPaired 验证：未配对的两端 dial 被拒绝（ReasonNotPaired）。
// waitListening 轮询等待监听就绪（最多 3s）。
//
// 为什么需要：Run 是在 goroutine 里先 loadTLS 再 listenQUIC/listenTCP，
// 测试若直接 dial 会与监听建立竞态（表现为 "actively refused it"）。
func waitListening(t *testing.T, addr string) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		c, err := net.DialTimeout("tcp", addr, 200*time.Millisecond)
		if err == nil {
			_ = c.Close()
			return
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatalf("server not listening on %s within 3s", addr)
}

// TestServerRun_StopsOnCtxCancel 验证 Run **自己**就能响应 ctx 取消。
//
// 回归的坑：早期 Run 把 acceptTCP 放在当前 goroutine 里阻塞，而 Close() 又在
// <-ctx.Done() 之后才调用——于是"只 cancel ctx、不额外调 Close()"时 Run 会永久
// 卡在 Accept 上。cmd/relayd 恰好 cancel 与 Close 都调了，所以问题被掩盖；
// 任何只依赖 ctx 的调用方（测试、未来的库使用者）都会挂死。
func TestServerRun_StopsOnCtxCancel(t *testing.T) {
	token := "test-token-0123456789abcdef"
	reg := newFakeRegistry(t, token)

	log := slog.New(slog.NewTextHandler(io.Discard, nil))
	port := freePort(t)
	srv := NewServer(ServerConfig{
		ListenAddr:       net.JoinHostPort("127.0.0.1", port),
		RegistryBaseURL:  reg.srv.URL,
		RegistryToken:    token,
		AllowInsecureTLS: true,
		MaxConnPerIP:     64,
	}, NewRegClient(reg.srv.URL, token), log)

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- srv.Run(ctx) }()

	// 等监听就绪
	waitListening(t, srv.cfg.ListenAddr)

	// 关键：只 cancel，**不**调 srv.Close()
	cancel()

	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("Run returned %v, want nil", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("Run did not return after ctx cancel — it still needs an external Close()")
	}
}

// TestServerPerIPLimitHeldForSessionLifetime 验证每 IP 名额持有到会话结束，
// 而不只是握手阶段。
//
// 回归的坑：早期在 TLS 握手后就 Release(ip)，于是 MaxConnPerIP 实际只限制了
// "并发握手数"，在线连接数完全不受控（与 ServerConfig 文档的"并发连接上限"不符）。
func TestServerPerIPLimitHeldForSessionLifetime(t *testing.T) {
	lim := NewIPLimiter(2)
	const ip = "9.9.9.9"

	// 两条"会话"长期占用
	if !lim.Allow(ip) || !lim.Allow(ip) {
		t.Fatal("first two should be allowed")
	}
	if lim.Allow(ip) {
		t.Fatal("third should be rejected while two sessions are alive")
	}
	if lim.ConnCount(ip) != 2 {
		t.Fatalf("ConnCount=%d, want 2", lim.ConnCount(ip))
	}

	// 一条会话结束后名额归还
	lim.Release(ip)
	if !lim.Allow(ip) {
		t.Fatal("after one release, a new connection should be allowed")
	}
}

// TestServerRun_ShutdownIsPrompt 验证关闭时不会被 45s 读超时拖住。
//
// 回归的坑：transport.Recv 早期只设 SetReadDeadline，不监听 ctx 取消，
// 于是已建立的空闲会话要等 45s 读超时才醒，Run 的 wg.Wait() 被拖几十秒。
func TestServerRun_ShutdownIsPrompt(t *testing.T) {
	token := "test-token-0123456789abcdef"
	reg := newFakeRegistry(t, token)
	log := slog.New(slog.NewTextHandler(io.Discard, nil))
	port := freePort(t)
	srv := NewServer(ServerConfig{
		ListenAddr:       net.JoinHostPort("127.0.0.1", port),
		RegistryBaseURL:  reg.srv.URL,
		RegistryToken:    token,
		AllowInsecureTLS: true,
		MaxConnPerIP:     64,
	}, NewRegClient(reg.srv.URL, token), log)

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- srv.Run(ctx) }()

	// 建一条 TCP/TLS 连接并完成 Hello，让它停在控制读循环里（空闲）。
	waitListening(t, srv.cfg.ListenAddr)
	pkA, privA := genKeyPair(t)
	reg.addDevice(pkA, false)

	c := dialTCP(t, srv.cfg.ListenAddr)
	c.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkA, privA, nil)))
	assertAckOK(t, c)

	// 现在这条会话阻塞在 readCtl 的 Recv 上；关闭必须在 5s 内完成。
	start := time.Now()
	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("Run returned %v", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("Run did not return within 5s — an idle session is holding shutdown")
	}
	if elapsed := time.Since(start); elapsed > 5*time.Second {
		t.Fatalf("shutdown took %s", elapsed)
	}
	_ = c.conn.Close()
}

func TestRelayTCPNotPaired(t *testing.T) {
	token := "test-token-0123456789abcdef"
	reg := newFakeRegistry(t, token)
	pkA, privA := genKeyPair(t)
	pkB, privB := genKeyPair(t)
	reg.addDevice(pkA, false)
	reg.addDevice(pkB, false)
	// 故意不加 pair
	_, addr := startTestServer(t, reg, token)

	a := dialTCP(t, addr)
	b := dialTCP(t, addr)
	a.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkA, privA, nil)))
	b.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkB, privB, nil)))
	assertAckOK(t, a)
	assertAckOK(t, b)

	a.writeCtl(t, proto.KindRelayDial, proto.EncodeRelayDial(pkToDevice(pkB)))
	ok, reason := waitDialResult(t, a)
	if ok || reason != proto.ReasonNotPaired {
		t.Fatalf("expected not_paired, got ok=%v reason=%q", ok, reason)
	}
}

// TestRelayTCPPeerOffline 验证：对端不在线时 dial 返回 ReasonPeerOffline。
func TestRelayTCPPeerOffline(t *testing.T) {
	token := "test-token-0123456789abcdef"
	reg := newFakeRegistry(t, token)
	pkA, privA := genKeyPair(t)
	pkB, _ := genKeyPair(t)
	reg.addDevice(pkA, false)
	idB := reg.addDevice(pkB, false)
	reg.addPair(reg.addDeviceLookup(pkA), idB)

	_, addr := startTestServer(t, reg, token)
	a := dialTCP(t, addr)
	a.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkA, privA, nil)))
	assertAckOK(t, a)

	a.writeCtl(t, proto.KindRelayDial, proto.EncodeRelayDial(pkToDevice(pkB)))
	ok, reason := waitDialResult(t, a)
	if ok || reason != proto.ReasonPeerOffline {
		t.Fatalf("expected peer_offline, got ok=%v reason=%q", ok, reason)
	}
}

// TestRelayHelloBadSignature 验证：伪造签名被拒（无 HelloAck ok）。
func TestRelayHelloBadSignature(t *testing.T) {
	token := "test-token-0123456789abcdef"
	reg := newFakeRegistry(t, token)
	pkA, _ := genKeyPair(t)
	_, otherPriv := genKeyPair(t)
	reg.addDevice(pkA, false)

	_, addr := startTestServer(t, reg, token)
	a := dialTCP(t, addr)

	// 用错误的私钥签 A 的 device_id
	badHello := buildHello(t, pkA, otherPriv, nil)
	a.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, badHello))
	frame, err := a.readCtl(t)
	if err != nil {
		t.Fatalf("expected hello ack frame, got err %v", err)
	}
	if frame.Kind != proto.KindRelayHelloAck {
		t.Fatalf("expected HelloAck, got 0x%02x", byte(frame.Kind))
	}
	ok, reason, _ := proto.DecodeRelayStatus(frame.Payload)
	if ok {
		t.Fatalf("bad signature was accepted")
	}
	if reason != proto.ReasonAuthFailed {
		t.Fatalf("expected auth_failed, got %q", reason)
	}
}

// TestRelayRevokedDeviceRejected 验证被撤销设备无法建立会话。
func TestRelayRevokedDeviceRejected(t *testing.T) {
	token := "test-token-0123456789abcdef"
	reg := newFakeRegistry(t, token)
	pkA, privA := genKeyPair(t)
	reg.addDevice(pkA, true) // revoked

	_, addr := startTestServer(t, reg, token)
	a := dialTCP(t, addr)
	a.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkA, privA, nil)))
	frame := mustReadCtl(t, a)
	ok, reason, _ := proto.DecodeRelayStatus(frame.Payload)
	if ok || reason != proto.ReasonRevoked {
		t.Fatalf("expected revoked, got ok=%v reason=%q", ok, reason)
	}
}

// TestRelayKickOnRevoke 验证撤销踢线：KickPair 后两端连接被关闭。
func TestRelayKickOnRevoke(t *testing.T) {
	token := "test-token-0123456789abcdef"
	reg := newFakeRegistry(t, token)
	pkA, privA := genKeyPair(t)
	pkB, privB := genKeyPair(t)
	idAHex := reg.addDevice(pkA, false)
	idBHex := reg.addDevice(pkB, false)
	reg.addPair(idAHex, idBHex)

	srv, addr := startTestServer(t, reg, token)
	a := dialTCP(t, addr)
	b := dialTCP(t, addr)
	a.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkA, privA, nil)))
	b.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkB, privB, nil)))
	assertAckOK(t, a)
	assertAckOK(t, b)
	a.writeCtl(t, proto.KindRelayDial, proto.EncodeRelayDial(pkToDevice(pkB)))
	if !anyDialOK(t, a) {
		t.Fatal("dial failed")
	}

	// 模拟 registry 推 pair_revoked
	srv.HandleNotifyEvent(fakeEvent{kind: "pair_revoked", a: idAHex, b: idBHex})

	// 两端最终都应读到 EOF / 错误（连接被踢）
	if !eventuallyClosed(t, a) {
		t.Fatal("A connection not closed after revoke")
	}
	if !eventuallyClosed(t, b) {
		t.Fatal("B connection not closed after revoke")
	}
}

// TestRelayDeviceKeyRotatedKick 验证密钥轮换踢线。
func TestRelayDeviceKeyRotatedKick(t *testing.T) {
	token := "test-token-0123456789abcdef"
	reg := newFakeRegistry(t, token)
	pkA, privA := genKeyPair(t)
	idAHex := reg.addDevice(pkA, false)

	srv, addr := startTestServer(t, reg, token)
	a := dialTCP(t, addr)
	a.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkA, privA, nil)))
	assertAckOK(t, a)

	srv.HandleNotifyEvent(fakeEvent{kind: "device_key_rotated", id: idAHex})
	if !eventuallyClosed(t, a) {
		t.Fatal("connection not closed after key rotation")
	}
}

// TestRelayIPLimiterConcurrent 验证每 IP 并发连接上限。
func TestRelayIPLimiterConcurrent(t *testing.T) {
	lim := NewIPLimiter(2)
	if !lim.Allow("1.2.3.4") {
		t.Fatal("first should pass")
	}
	if !lim.Allow("1.2.3.4") {
		t.Fatal("second should pass")
	}
	if lim.Allow("1.2.3.4") {
		t.Fatal("third should be rejected")
	}
	lim.Release("1.2.3.4")
	if !lim.Allow("1.2.3.4") {
		t.Fatal("after release should pass")
	}
	// 不同 IP 独立
	if !lim.Allow("5.6.7.8") {
		t.Fatal("other ip should pass")
	}
}

// TestRelayChallengeRateLimit 验证挑战限速窗口。
func TestRelayChallengeRateLimit(t *testing.T) {
	lim := NewIPLimiter(64)
	now := time.Unix(1000, 0)
	lim.SetClock(func() time.Time { return now })
	for i := 0; i < challengePerMinute; i++ {
		if !lim.AllowChallenge("9.9.9.9") {
			t.Fatalf("attempt %d should pass", i)
		}
	}
	if lim.AllowChallenge("9.9.9.9") {
		t.Fatal("should be rate limited after window full")
	}
	// 过窗后恢复
	now = now.Add(61 * time.Second)
	if !lim.AllowChallenge("9.9.9.9") {
		t.Fatal("should pass after window reset")
	}
}

// TestWiringGraceWindow 验证宽限窗语义。
func TestWiringGraceWindow(t *testing.T) {
	now := time.Unix(5000, 0)
	w := NewWiring(nil)
	w.SetClock(func() time.Time { return now })

	var idA, idB [proto.DeviceIDSize]byte
	idA[0], idB[0] = 1, 2
	ca, cb := newFakeConn("a"), newFakeConn("b")
	w.Register(idA, [16]byte{}, ca)
	w.Register(idB, [16]byte{}, cb)
	if _, err := w.Wire(idA, idB); err != nil {
		t.Fatal(err)
	}
	// 断 A → 进宽限窗
	w.Unregister(idA, ca)
	if !w.InGrace(idA) {
		t.Fatal("A should be in grace")
	}
	if w.InGrace(idB) {
		t.Fatal("B should not be in grace")
	}
	// 过窗：SweepGrace 应清掉 A 的宽限记录。
	// 注意：InGrace 自身在发现过窗时也会顺手删除，因此这里直接调 SweepGrace
	// 验证"过期条目可被回收"，不依赖 InGrace 的副作用。
	now = now.Add(GraceWindow + time.Second)
	if n := w.SweepGrace(); n != 1 {
		t.Fatalf("expected 1 swept, got %d", n)
	}
	if w.InGrace(idA) {
		t.Fatal("A should be out of grace after window")
	}
}

// TestWiringRegisterReplaces 验证同 device_id 重连顶替旧连接。
func TestWiringRegisterReplaces(t *testing.T) {
	w := NewWiring(nil)
	var id [proto.DeviceIDSize]byte
	id[0] = 9
	old := newFakeConn("old")
	w.Register(id, [16]byte{}, old)
	newer := newFakeConn("new")
	replaced := w.Register(id, [16]byte{}, newer)
	if replaced != old {
		t.Fatalf("expected old conn replaced, got %v", replaced)
	}
	if w.Lookup(id) != newer {
		t.Fatal("lookup should return new conn")
	}
	if w.ActiveCount() != 1 {
		t.Fatalf("expected 1 active, got %d", w.ActiveCount())
	}
}

// TestWiringStaleUnregisterKeepsNewSession 验证"旧连接的清理不得误删新会话"。
//
// 场景：A 断线重连 —— conn1 退出、conn2 已注册。若 conn1 的清理晚到，
// 无守卫的 Unregister 会把 conn2 的表项一并删掉；对端从此查到 peer_offline，
// 双方不断重连又互相删除，永远收敛不了。
func TestWiringStaleUnregisterKeepsNewSession(t *testing.T) {
	w := NewWiring(nil)
	var id [proto.DeviceIDSize]byte
	id[0] = 7

	old := newFakeConn("old")
	w.Register(id, [16]byte{}, old)
	newer := newFakeConn("new")
	if replaced := w.Register(id, [16]byte{}, newer); replaced != old {
		t.Fatalf("expected old replaced, got %v", replaced)
	}

	// 旧连接的清理晚到：必须不影响新会话。
	w.Unregister(id, old)
	if w.Lookup(id) != newer {
		t.Fatal("stale unregister removed the newer session")
	}
	if w.ActiveCount() != 1 {
		t.Fatalf("expected 1 active, got %d", w.ActiveCount())
	}

	// 新会话用自己那条连接注销，仍然生效。
	w.Unregister(id, newer)
	if w.Lookup(id) != nil {
		t.Fatal("newer session should be removable by its own conn")
	}
}

// TestRelayTCPDialRetryAfterPeerOffline 验证"对端尚未注册时 dial 被拒，
// 同一连接上再次 dial 能成功接线"。
//
// 这是客户端的既定重试路径（relay 拒绝后不立刻断开，由客户端决定重试）：
// 两端几乎同时启动时，先到者必然撞上 peer_offline；若 relay 不允许同连接重试，
// 客户端就只能断开重连，而断开会让 relay 注销本端，反而拉长收敛时间。
func TestRelayTCPDialRetryAfterPeerOffline(t *testing.T) {
	token := "test-token-0123456789abcdef"
	reg := newFakeRegistry(t, token)

	pkA, privA := genKeyPair(t)
	pkB, privB := genKeyPair(t)
	idA := reg.addDevice(pkA, false)
	idB := reg.addDevice(pkB, false)
	reg.addPair(idA, idB)

	_, addr := startTestServer(t, reg, token)

	// 只有 A 先接入并 Hello
	a := dialTCP(t, addr)
	a.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkA, privA, nil)))
	assertAckOK(t, a)

	// A 先 dial：此时 B 还没接入 → 必须是 peer_offline（且连接不关）
	a.writeCtl(t, proto.KindRelayDial, proto.EncodeRelayDial(pkToDevice(pkB)))
	ok, reason := waitDialResult(t, a)
	if ok || reason != proto.ReasonPeerOffline {
		t.Fatalf("expected peer_offline, got ok=%v reason=%q", ok, reason)
	}

	// 现在 B 接入并 Hello
	b := dialTCP(t, addr)
	b.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkB, privB, nil)))
	assertAckOK(t, b)

	// A 在**同一条连接**上重试 dial → 应成功；B 也 dial
	a.writeCtl(t, proto.KindRelayDial, proto.EncodeRelayDial(pkToDevice(pkB)))
	b.writeCtl(t, proto.KindRelayDial, proto.EncodeRelayDial(pkToDevice(pkA)))
	if !anyDialOK(t, a) {
		t.Fatal("A retry dial did not succeed")
	}
	if !anyDialOK(t, b) {
		t.Fatal("B dial did not succeed")
	}

	// 接线后密文双向互通
	payload := []byte("retry-then-ciphertext")
	if _, err := a.conn.Write(payload); err != nil {
		t.Fatalf("A write: %v", err)
	}
	if got := readN(t, b, len(payload)); string(got) != string(payload) {
		t.Fatalf("B got %q, want %q", got, payload)
	}
}

// TestWiringKickRemovesGrace 验证踢线不保留宽限（撤销后不能靠重连恢复）。
func TestWiringKickRemovesGrace(t *testing.T) {
	w := NewWiring(nil)
	var id [proto.DeviceIDSize]byte
	id[0] = 3
	w.Register(id, [16]byte{}, newFakeConn("x"))
	if !w.KickDevice(id) {
		t.Fatal("kick should hit")
	}
	if w.InGrace(id) {
		t.Fatal("kicked device must not be in grace")
	}
	if w.KickDevice(id) {
		t.Fatal("second kick should miss")
	}
}

// ---------- 测试替身与工具 ----------

type fakeEvent struct{ kind, a, b, id, reason string }

func (e fakeEvent) Kind() string        { return e.kind }
func (e fakeEvent) DeviceAHex() string  { return e.a }
func (e fakeEvent) DeviceBHex() string  { return e.b }
func (e fakeEvent) DeviceIDHex() string { return e.id }
func (e fakeEvent) Reason() string      { return e.reason }

type fakeConn struct {
	name   string
	mu     sync.Mutex
	writes [][]byte
	closed bool
}

func newFakeConn(name string) *fakeConn { return &fakeConn{name: name} }

func (f *fakeConn) Send(ctx context.Context, data []byte) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.closed {
		return io.ErrClosedPipe
	}
	cp := make([]byte, len(data))
	copy(cp, data)
	f.writes = append(f.writes, cp)
	return nil
}

func (f *fakeConn) Recv(ctx context.Context, buf []byte) (int, error) {
	return 0, io.EOF
}

func (f *fakeConn) Close() error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.closed = true
	return nil
}

func (f *fakeConn) RemoteAddr() string { return f.name }
func (f *fakeConn) Kind() string       { return "fake" }

// dialTCP 以 TLS 拨入 relay（测试用 InsecureSkipVerify）。
func dialTCP(t *testing.T, addr string) *clientSession {
	t.Helper()
	raw, err := net.DialTimeout("tcp", addr, 3*time.Second)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	tc := tlsClient(raw)
	if err := tc.Handshake(); err != nil {
		_ = raw.Close()
		t.Fatalf("tls handshake: %v", err)
	}
	return &clientSession{conn: tc, t: t, kind: "tcp-tls"}
}

// mustDecodeHelloPayload 从已编码的 Hello 控制帧里剥出 payload。
func mustDecodeHelloPayload(t *testing.T, frame []byte) []byte {
	t.Helper()
	ctl, _, err := proto.TryDecodeRelayCtl(frame)
	if err != nil || ctl == nil {
		t.Fatalf("decode hello frame: %v", err)
	}
	return ctl.Payload
}

func pkToDevice(pk proto.PublicKey) [proto.DeviceIDSize]byte { return pk.DeviceID() }

func assertAckOK(t *testing.T, c *clientSession) {
	t.Helper()
	frame := mustReadCtl(t, c)
	if frame.Kind != proto.KindRelayHelloAck {
		t.Fatalf("expected HelloAck, got 0x%02x", byte(frame.Kind))
	}
	ok, reason, err := proto.DecodeRelayStatus(frame.Payload)
	if err != nil {
		t.Fatal(err)
	}
	if !ok {
		t.Fatalf("hello rejected: %s", reason)
	}
}

func mustReadCtl(t *testing.T, c *clientSession) *proto.RelayCtl {
	t.Helper()
	frame, err := c.readCtl(t)
	if err != nil {
		t.Fatalf("read ctl: %v", err)
	}
	return frame
}

// anyDialOK 读若干帧，直到看到 DialResult(ok)；跳过中间的 PeerEvent。
func anyDialOK(t *testing.T, c *clientSession) bool {
	t.Helper()
	ok, _ := waitDialResult(t, c)
	return ok
}

// waitDialResult 读帧直到 DialResult，返回 (ok, reason)。
func waitDialResult(t *testing.T, c *clientSession) (bool, string) {
	t.Helper()
	for i := 0; i < 20; i++ {
		frame, err := c.readCtl(t)
		if err != nil {
			t.Fatalf("read while waiting dial result: %v", err)
		}
		if frame.Kind == proto.KindRelayDialResult {
			ok, reason, err := proto.DecodeRelayStatus(frame.Payload)
			if err != nil {
				t.Fatal(err)
			}
			return ok, reason
		}
	}
	t.Fatal("no dial result within 20 frames")
	return false, ""
}

func readN(t *testing.T, c *clientSession, n int) []byte {
	t.Helper()
	_ = c.conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	buf := make([]byte, n)
	got := 0
	for got < n {
		m, err := c.conn.Read(buf[got:])
		got += m
		if err != nil {
			t.Fatalf("read: %v (got %d/%d)", err, got, n)
		}
	}
	return buf
}

// eventuallyClosed 报告连接是否在 2s 内关闭。
func eventuallyClosed(t *testing.T, c *clientSession) bool {
	t.Helper()
	_ = c.conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 64)
	for {
		_, err := c.conn.Read(buf)
		if err != nil {
			if strings.Contains(err.Error(), "timeout") {
				return false
			}
			return true // EOF / reset
		}
	}
}

// addDeviceLookup 辅助：取 pkA 的 device_id hex（fakeRegistry 已写表）。
func (f *fakeRegistry) addDeviceLookup(pk proto.PublicKey) string {
	id := pk.DeviceID()
	return hex.EncodeToString(id[:])
}
