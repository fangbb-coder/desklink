package regnotify

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// Publisher 持有 unix socket 服务端，接受 relay 连接后推送事件。
//
// 帧格式：[u32 length BE][JSON payload]
// ACK 协议：relay 处理完成后发 ack 帧（Kind=KindAck，event_id 含原 event_id）；
// Publisher 在收到 ACK 后从 pending map 删除该 event。
// 未 ACK 的事件以 at-least-once 语义在连接断开前最多重发 3 次（间隔 1s/2s/4s）。
type Publisher struct {
	socketPath string
	log        *slog.Logger

	mu      sync.Mutex
	clients map[net.Conn]struct{}
	pending map[string]pendingEvent // event_id → pending

	listener net.Listener
	closed   chan struct{}
	wg       sync.WaitGroup
}

type pendingEvent struct {
	event     Event
	conn      net.Conn
	attempts  int
	lastSent  time.Time
	nextRetry time.Time
}

// NewPublisher 创建 Publisher；socket 文件在 Close 时删除。
func NewPublisher(socketPath string, log *slog.Logger) (*Publisher, error) {
	if socketPath == "" {
		return nil, errors.New("regnotify: empty socket path")
	}
	abs, err := filepath.Abs(socketPath)
	if err != nil {
		return nil, fmt.Errorf("regnotify: bad socket path: %w", err)
	}
	if err := os.MkdirAll(filepath.Dir(abs), 0o755); err != nil {
		return nil, fmt.Errorf("regnotify: mkdir socket dir: %w", err)
	}
	// 清残留
	_ = os.Remove(abs)
	l, err := net.Listen("unix", abs)
	if err != nil {
		return nil, fmt.Errorf("regnotify: listen: %w", err)
	}
	if err := os.Chmod(abs, 0o600); err != nil {
		_ = l.Close()
		return nil, fmt.Errorf("regnotify: chmod: %w", err)
	}
	return &Publisher{
		socketPath: abs,
		log:        log,
		clients:    make(map[net.Conn]struct{}),
		pending:    make(map[string]pendingEvent),
		listener:   l,
		closed:     make(chan struct{}),
	}, nil
}

// SocketPath 返回绑定的 socket 绝对路径。
func (p *Publisher) SocketPath() string { return p.socketPath }

// Run 启动 accept 循环；ctx 取消后优雅退出。
func (p *Publisher) Run(ctx context.Context) error {
	acceptDone := make(chan error, 1)
	go func() {
		acceptDone <- p.acceptLoop(ctx)
	}()
	p.wg.Add(1)
	go func() {
		defer p.wg.Done()
		p.retryLoop(ctx)
	}()
	select {
	case <-ctx.Done():
		return p.Close()
	case err := <-acceptDone:
		return err
	}
}

func (p *Publisher) acceptLoop(ctx context.Context) error {
	for {
		conn, err := p.listener.Accept()
		if err != nil {
			select {
			case <-p.closed:
				return nil
			default:
			}
			if errors.Is(err, net.ErrClosed) {
				return nil
			}
			return fmt.Errorf("regnotify: accept: %w", err)
		}
		p.wg.Add(1)
		go func(c net.Conn) {
			defer p.wg.Done()
			p.handleConn(ctx, c)
		}(conn)
	}
}

func (p *Publisher) handleConn(ctx context.Context, conn net.Conn) {
	p.mu.Lock()
	p.clients[conn] = struct{}{}
	p.mu.Unlock()
	defer func() {
		p.mu.Lock()
		delete(p.clients, conn)
		// 该连接上未 ACK 的事件下次重连后由 relay 走 /v1/events?since=<id> 增量重放
		for id, pe := range p.pending {
			if pe.conn == conn {
				delete(p.pending, id)
			}
		}
		p.mu.Unlock()
		_ = conn.Close()
	}()
	for {
		select {
		case <-ctx.Done():
			return
		case <-p.closed:
			return
		default:
		}
		_ = conn.SetReadDeadline(time.Now().Add(30 * time.Second))
		frame, err := readSocketFrame(conn)
		if err != nil {
			if errors.Is(err, io.EOF) || isClosedErr(err) {
				return
			}
			if isTimeout(err) {
				continue
			}
			p.log.Warn("regnotify: read frame error", "err", err)
			return
		}
		var ev Event
		if err := json.Unmarshal(frame, &ev); err != nil {
			p.log.Warn("regnotify: bad json", "err", err)
			continue
		}
		if ev.Kind == KindAck && ev.AckEventID != "" {
			p.mu.Lock()
			delete(p.pending, ev.AckEventID)
			p.mu.Unlock()
			if p.log != nil {
				p.log.Debug("regnotify: ack received", "event_id", ev.AckEventID)
			}
		}
	}
}

func (p *Publisher) retryLoop(ctx context.Context) {
	t := time.NewTicker(1 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-p.closed:
			return
		case now := <-t.C:
			p.resendPending(now)
		}
	}
}

func (p *Publisher) resendPending(now time.Time) {
	p.mu.Lock()
	defer p.mu.Unlock()
	for id, pe := range p.pending {
		if now.Before(pe.nextRetry) {
			continue
		}
		if pe.attempts >= 3 {
			delete(p.pending, id)
			p.log.Warn("regnotify: giving up after 3 attempts", "event_id", id)
			continue
		}
		if err := writeEvent(pe.conn, pe.event); err != nil {
			p.log.Warn("regnotify: resend failed", "event_id", id, "err", err)
			delete(p.pending, id)
			continue
		}
		pe.attempts++
		pe.lastSent = now
		pe.nextRetry = now.Add(time.Duration(1<<pe.attempts) * time.Second)
		p.pending[id] = pe
	}
}

// Publish 推送事件给所有已连接 relay 客户端（at-least-once）。
func (p *Publisher) Publish(ev Event) {
	p.mu.Lock()
	defer p.mu.Unlock()
	for c := range p.clients {
		if err := writeEvent(c, ev); err != nil {
			p.log.Warn("regnotify: write event failed", "err", err)
			continue
		}
		now := time.Now()
		p.pending[ev.EventID] = pendingEvent{
			event:     ev,
			conn:      c,
			attempts:  1,
			lastSent:  now,
			nextRetry: now.Add(2 * time.Second),
		}
	}
}

// Close 关闭 listener 并断开所有连接。
func (p *Publisher) Close() error {
	select {
	case <-p.closed:
		return nil
	default:
		close(p.closed)
	}
	err := p.listener.Close()
	p.mu.Lock()
	for c := range p.clients {
		_ = c.Close()
	}
	p.mu.Unlock()
	p.wg.Wait()
	_ = os.Remove(p.socketPath)
	return err
}

func writeEvent(w io.Writer, ev Event) error {
	body, err := json.Marshal(ev)
	if err != nil {
		return fmt.Errorf("regnotify: marshal event: %w", err)
	}
	return writeSocketFrame(w, body)
}

var _ = errors.Is // keep errors import for future typed errors
