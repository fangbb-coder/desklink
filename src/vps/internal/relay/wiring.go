// Package relay 子文件 wiring：内存接线表 + 宽限窗 + 密文泵。
//
// 设计要点（DESIGN.md 偏差项 #3）：
//   - relay **并非**字面"无状态"：它维护内存接线表（device_id → 活跃连接），
//     以及 30s 重连宽限窗（对端短暂掉线时保留接线，避免会话被拆）。
//   - relay 重启即丢接线表 → 两端退避重连 + 重新握手（可接受，自用场景）。
//   - relay **绝不解析**业务字节：一旦接线建立，两个方向原样 dup 字节。
//     这一条是硬约束——E2E 密钥只在两端，relay 看不到明文。
package relay

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"

	"desklink/vps/internal/proto"
)

// ErrPeerOffline 接线时 peer 不存在或已过宽限窗。
var ErrPeerOffline = errors.New("relay: peer offline")

// ErrNotPaired 两端不是有效配对。
var ErrNotPaired = errors.New("relay: not paired")

// GraceWindow 是重连宽限窗时长。对端断线后保留接线位 30s，
// 期间若同 device_id 重新 Hello 则复用接线（不通知本次会话被拆）。
const GraceWindow = 30 * time.Second

// Conn 是接线表中一条"设备接入会话"的抽象。
//
// relay 只通过这个接口收发字节，不关心底层是 QUIC 流还是 TLS 连接。
// 这也是为什么 pump 可以完全只做 io.Copy。
type Conn interface {
	// Send 写一段字节（控制帧或已接线后的业务密文）。
	Send(ctx context.Context, data []byte) error
	// Recv 读一段字节，返回 n 与 err。语义与 net.Conn.Read 一致。
	Recv(ctx context.Context, buf []byte) (int, error)
	// Close 关闭底层传输；幂等。
	Close() error
	// RemoteAddr 返回可读远端地址（日志）。
	RemoteAddr() string
	// Kind 返回传输类型（quic / tcp-tls），用于日志与测试断言。
	Kind() string
}

// entry 是接线表中的一条记录。
type entry struct {
	DeviceID [proto.DeviceIDSize]byte
	Conn     Conn
	// Hint 客户端携带的 device_id_hint（日志/调试）。
	Hint [16]byte
	// Peer 是当前已接线的对端 device_id（NoneDevice 表示未接线）。
	Peer [proto.DeviceIDSize]byte
	// Wired 表示是否已完成 Dial 且两个方向已开始泵送。
	Wired bool
	// connectedAt / lastSeen 用于宽限窗与保活。
	connectedAt time.Time
	lastSeen    time.Time

	// dialGate 在"配对首次接线成功"时创建，由配对的**两侧共享**同一个对象。
	//
	// 用途：先到者（赢得 Wire 的一方，也是唯一的泵持有者）在发送
	// DialResult(ok) 之后要等它关闭，才进入 Pump；后到者在自己的 Dial
	// 处理完成时关闭它。
	//
	// 为什么需要它：relay 的 Pump 是双向的，只要一侧起了泵就能同时搬运两个
	// 方向。但后到者此刻可能仍在控制帧读循环里读自己那条连接；若泵提前启动，
	// 同一字节流会被两个 goroutine 并发读取，导致字节乱序——而 E2E 层是严格
	// 递增的 AEAD 计数器，乱序会直接解密失败。等后到者退出读循环后再起泵，
	// 即可保证"同一连接在任一时刻只有一个读者"。
	dialGate *dialGate

	// sessionEnd 在本会话被移除（Unregister / KickDevice / 被顶替）时关闭。
	// 后到者阻塞等待它来保持连接存活（既不读数据也不起第二个泵），
	// 直到接线结束再让本会话正常退出、由客户端重新握手。
	sessionEnd chan struct{}
	closeOnce  sync.Once
}

// closeSessionEnd 幂等地关闭 sessionEnd。
func (e *entry) closeSessionEnd() {
	e.closeOnce.Do(func() { close(e.sessionEnd) })
}

// dialGate 是"两侧都已完成控制阶段"的一次性信号。
//
// 用 sync.Once 保证 close 只发生一次（两侧都可能触发 signal）。
type dialGate struct {
	once sync.Once
	ch   chan struct{}
}

// newDialGate 创建一个未触发的门。
func newDialGate() *dialGate { return &dialGate{ch: make(chan struct{})} }

// signal 触发门（幂等）。
func (g *dialGate) signal() { g.once.Do(func() { close(g.ch) }) }

// wait 等待门触发；超时或 ctx 取消返回 false。
func (g *dialGate) wait(ctx context.Context, timeout time.Duration) bool {
	t := time.NewTimer(timeout)
	defer t.Stop()
	select {
	case <-g.ch:
		return true
	case <-t.C:
		return false
	case <-ctx.Done():
		return false
	}
}

// NoneDevice 是"无对端"哨兵值（全零 device_id 不会被真实 BLAKE3 输出命中到
// 可忽略概率；这里显式定义便于可读性）。
var NoneDevice = [proto.DeviceIDSize]byte{}

// Wiring 是并发安全的内存接线表。
//
// 锁策略：单把 Mutex 保护整表。接线操作短（查表 + 赋值），锁竞争不是瓶颈——
// 真正的数据面（pump）在锁外进行。
type Wiring struct {
	mu      sync.Mutex
	entries map[[proto.DeviceIDSize]byte]*entry
	// grace 记录刚断线、处于宽限窗内的设备 → 断线时刻。
	grace map[[proto.DeviceIDSize]byte]time.Time
	// log 可选日志。
	log func(format string, args ...any)
	// now 可注入时钟（测试）。
	now func() time.Time
}

// NewWiring 构造接线表。
func NewWiring(log func(format string, args ...any)) *Wiring {
	return &Wiring{
		entries: make(map[[proto.DeviceIDSize]byte]*entry),
		grace:   make(map[[proto.DeviceIDSize]byte]time.Time),
		log:     log,
		now:     time.Now,
	}
}

// SetClock 注入测试时钟。
func (w *Wiring) SetClock(now func() time.Time) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.now = now
}

// Register 登记一个新接入会话。
//
// 若同 device_id 已有活跃会话：旧会话被顶替（描述"同设备多开"：新连接赢，
// 旧连接收到 ReasonAlreadyWired 后应关闭）。返回被顶替的旧连接（可能 nil）。
func (w *Wiring) Register(deviceID [proto.DeviceIDSize]byte, hint [16]byte, c Conn) (replaced Conn) {
	w.mu.Lock()
	defer w.mu.Unlock()
	now := w.now()
	if old, ok := w.entries[deviceID]; ok {
		replaced = old.Conn
		// 被顶替的连接若已接线，需让对端也结束（避免留下半死会话）。
		if old.Wired {
			w.wakePeerLocked(old.Peer, deviceID)
		}
		old.closeSessionEnd()
	}
	delete(w.grace, deviceID)
	w.entries[deviceID] = &entry{
		DeviceID:    deviceID,
		Conn:        c,
		Hint:        hint,
		Peer:        NoneDevice,
		Wired:       false,
		connectedAt: now,
		lastSeen:    now,
		sessionEnd:  make(chan struct{}),
	}
	if w.log != nil {
		w.log("wiring: register device=%s addr=%s kind=%s", short(deviceID), c.RemoteAddr(), c.Kind())
	}
	return replaced
}

// Unregister 移除会话；如该会话已接线，把 peer 转入宽限窗并唤醒对端会话。
//
// 参数 c 是"正在退出的那条连接"。**只有当前表项仍指向 c 时才移除**——
// 这是必须的守卫，否则会出现这类永久性故障：
//
//	A 断线重连（conn1 退出、conn2 注册）。若 conn1 的清理晚于 conn2 的注册，
//	无条件的 Unregister(A) 会把 conn2 的表项一并删掉 → 对端从此查到
//	peer_offline，双方不断重连又不断互相删除，永远收敛不了
//	（实测表现为两个实例互相报 peer_offline 直到超时）。
//
// 返回 peer device_id（NoneDevice 表示无 peer 或未命中）。
func (w *Wiring) Unregister(deviceID [proto.DeviceIDSize]byte, c Conn) [proto.DeviceIDSize]byte {
	w.mu.Lock()
	defer w.mu.Unlock()
	e, ok := w.entries[deviceID]
	if !ok || e.Conn != c {
		// 未注册，或已被更新的连接顶替：交给新会话负责注销，这里什么也不做。
		return NoneDevice
	}
	peer := e.Peer
	delete(w.entries, deviceID)
	w.grace[deviceID] = w.now()
	e.closeSessionEnd()
	if e.Wired && peer != NoneDevice {
		// 解除 peer 的接线并唤醒其等待中的会话。
		//
		// 注意：这里**不**再向 peer 连接写 PeerEvent(offline)。
		// 原因：本函数可能在"泵仍在搬运对端字节"时被调用，而对端连接的写者
		// 只能是它自己那条 serveSession；跨 goroutine 写会与泵/控制帧交错，
		// 把控制帧混进业务密文。对端通过"连接被关闭"即可感知断开并重连，
		// 这也是 DESIGN.md"每次重连都是完整重握手"的既定路径。
		w.wakePeerLocked(peer, deviceID)
	}
	if w.log != nil {
		w.log("wiring: unregister device=%s (grace %s)", short(deviceID), GraceWindow)
	}
	return peer
}

// wakePeerLocked 解除 peer 的接线标记并唤醒其等待中的会话。
// 调用方必须持锁。
func (w *Wiring) wakePeerLocked(peer, wentOffline [proto.DeviceIDSize]byte) {
	pe, ok := w.entries[peer]
	if !ok {
		return
	}
	pe.Peer = NoneDevice
	pe.Wired = false
	pe.dialGate = nil
	pe.closeSessionEnd()
}

// Wire 把 (a, b) 两条会话接起来。要求：
//   - a、b 都已 Register 且在表中；
//   - a/b 自身尚未接线（Wired=false）；
//   - 调用方已完成配对校验（本函数不查 registry）。
//
// 返回 newlyWired：true 表示本次调用真正完成了接线（调用方应成为唯一的泵持有者）；
// false 表示该配对已被对端接线（此时 err 非 nil，调用方应视为幂等成功但**不要**再起泵）。
//
// 成功后两条 entry 都置 Wired=true、互相指向，并共享同一个 dialGate。
func (w *Wiring) Wire(a, b [proto.DeviceIDSize]byte) (bool, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	ea, okA := w.entries[a]
	eb, okB := w.entries[b]
	if !okA || !okB {
		return false, ErrPeerOffline
	}
	if ea.Wired || eb.Wired {
		return false, fmt.Errorf("relay: device already wired (a=%v b=%v)", ea.Wired, eb.Wired)
	}
	gate := newDialGate()
	ea.Peer = b
	ea.Wired = true
	ea.dialGate = gate
	eb.Peer = a
	eb.Wired = true
	eb.dialGate = gate
	if w.log != nil {
		w.log("wiring: wired %s <-> %s", short(a), short(b))
	}
	return true, nil
}

// SignalDialGate 由"后到者"在自身 Dial 处理完成后调用，通知先到者可以开始泵送。
// 幂等；设备不存在或未接线时为空操作。
func (w *Wiring) SignalDialGate(deviceID [proto.DeviceIDSize]byte) {
	w.mu.Lock()
	e, ok := w.entries[deviceID]
	var gate *dialGate
	if ok {
		gate = e.dialGate
	}
	w.mu.Unlock()
	if gate != nil {
		gate.signal()
	}
}

// WaitDialGate 由先到者调用：等待"对端也完成了 Dial"（或超时）。
// 返回 true 表示门已被触发；false 表示超时/取消。
//
// 超时后调用方仍可继续（best-effort），避免对端只 Hello 不 Dial 时永久卡住。
func (w *Wiring) WaitDialGate(ctx context.Context, deviceID [proto.DeviceIDSize]byte, timeout time.Duration) bool {
	w.mu.Lock()
	e, ok := w.entries[deviceID]
	var gate *dialGate
	if ok {
		gate = e.dialGate
	}
	w.mu.Unlock()
	if gate == nil {
		return true // 未接线/已被唤醒：无需等待
	}
	return gate.wait(ctx, timeout)
}

// WaitUnwired 阻塞直到 deviceID 的会话结束（sessionEnd 关闭）或 ctx 取消。
//
// 供"后到者"使用：它不起泵，只是保持连接存活（不读任何字节），
// 直到接线结束（对端断开 / 被踢 / 被顶替）后再让本会话退出。
func (w *Wiring) WaitUnwired(ctx context.Context, deviceID [proto.DeviceIDSize]byte) {
	w.mu.Lock()
	e, ok := w.entries[deviceID]
	var end chan struct{}
	if ok {
		end = e.sessionEnd
	}
	w.mu.Unlock()
	if end == nil {
		return
	}
	select {
	case <-end:
	case <-ctx.Done():
	}
}

// PeerOf 返回 deviceID 当前已接线的 peer（NoneDevice 表示未接线）。
func (w *Wiring) PeerOf(deviceID [proto.DeviceIDSize]byte) [proto.DeviceIDSize]byte {
	w.mu.Lock()
	defer w.mu.Unlock()
	if e, ok := w.entries[deviceID]; ok {
		return e.Peer
	}
	return NoneDevice
}

// Lookup 返回 deviceID 的活跃会话（nil 表示不在线）。
func (w *Wiring) Lookup(deviceID [proto.DeviceIDSize]byte) Conn {
	w.mu.Lock()
	defer w.mu.Unlock()
	if e, ok := w.entries[deviceID]; ok {
		return e.Conn
	}
	return nil
}

// InGrace 报告 deviceID 是否处于宽限窗内。
func (w *Wiring) InGrace(deviceID [proto.DeviceIDSize]byte) bool {
	w.mu.Lock()
	defer w.mu.Unlock()
	t, ok := w.grace[deviceID]
	if !ok {
		return false
	}
	if w.now().Sub(t) > GraceWindow {
		delete(w.grace, deviceID)
		return false
	}
	return true
}

// SweepGrace 清理已过窗的宽限记录；返回清理条数。由后台 ticker 调用。
func (w *Wiring) SweepGrace() int {
	w.mu.Lock()
	defer w.mu.Unlock()
	n := 0
	now := w.now()
	for id, t := range w.grace {
		if now.Sub(t) > GraceWindow {
			delete(w.grace, id)
			n++
		}
	}
	return n
}

// KickDevice 强制踢下线某设备（撤销 / 密钥轮换）。返回是否命中。
//
// 撤销路径（DESIGN.md 决策 #7）：registry 推 pair_revoked → relay 订阅到 →
// 对两端分别 KickDevice → 两端连接关闭 → 客户端重新握手时被 registry 拒。
func (w *Wiring) KickDevice(deviceID [proto.DeviceIDSize]byte) bool {
	w.mu.Lock()
	e, ok := w.entries[deviceID]
	if ok {
		delete(w.entries, deviceID)
		delete(w.grace, deviceID) // 撤销不保留宽限
	}
	peer := NoneDevice
	if ok {
		peer = e.Peer
		e.closeSessionEnd()
	}
	// 对端也要解除接线并唤醒（否则它会等待一个永不再来的对端）
	if ok && peer != NoneDevice {
		w.wakePeerLocked(peer, deviceID)
	}
	w.mu.Unlock()
	if ok {
		_ = e.Conn.Close()
		if w.log != nil {
			w.log("wiring: kicked device=%s (peer=%s)", short(deviceID), short(peer))
		}
	}
	return ok
}

// KickPair 撤销时对配对两端一起踢。
func (w *Wiring) KickPair(a, b [proto.DeviceIDSize]byte) {
	w.KickDevice(a)
	w.KickDevice(b)
}

// ActiveCount 返回在线设备数（测试/指标）。
func (w *Wiring) ActiveCount() int {
	w.mu.Lock()
	defer w.mu.Unlock()
	return len(w.entries)
}

// short 返回 device_id 前 8 字节 hex，用于日志。
func short(id [proto.DeviceIDSize]byte) string {
	const hexdigits = "0123456789abcdef"
	out := make([]byte, 16)
	for i := 0; i < 8; i++ {
		out[i*2] = hexdigits[id[i]>>4]
		out[i*2+1] = hexdigits[id[i]&0xF]
	}
	return string(out)
}

// Pump 在两个 Conn 之间双向泵送字节，直到任一侧出错或 ctx 取消。
//
// 关键约束：**只搬字节，不解析**。relay 在这里对业务内容零可见性。
// 返回时：两侧都关闭；第一个方向出错即整体结束（半关闭会拖慢对端感知）。
func Pump(ctx context.Context, a, b Conn) {
	pumpCtx, cancel := context.WithCancel(ctx)
	defer cancel()

	errCh := make(chan error, 2)
	copyDir := func(dst, src Conn) {
		// 32 KB 复用缓冲：足够吞下大块视频/文件密文，又不过度占内存。
		buf := make([]byte, 32*1024)
		for {
			n, err := src.Recv(pumpCtx, buf)
			if n > 0 {
				if werr := dst.Send(pumpCtx, buf[:n]); werr != nil {
					errCh <- werr
					return
				}
			}
			if err != nil {
				errCh <- err
				return
			}
		}
	}
	go copyDir(b, a)
	go copyDir(a, b)

	// 任一方向结束 → 取消另一方向并关闭两侧
	<-errCh
	cancel()
	_ = a.Close()
	_ = b.Close()
	// 等另一 goroutine 退出，避免泄漏
	select {
	case <-errCh:
	case <-time.After(2 * time.Second):
	}
}
