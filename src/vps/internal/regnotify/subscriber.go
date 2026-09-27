package regnotify

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"sync"
	"time"
)

// Subscriber 是 relay 端的 regnotify 客户端：连 registry unix socket 收事件，回 ACK。
type Subscriber struct {
	socketPath string
	log        *slog.Logger

	mu        sync.Mutex
	conn      net.Conn
	closed    bool
	closeCh   chan struct{}
	events    chan Event
	ackFn     func(eventID string) error
	reconnect time.Duration
}

// NewSubscriber 创建 Subscriber；socket path 为 registry unix socket。
func NewSubscriber(socketPath string, log *slog.Logger, ackFn func(eventID string) error) *Subscriber {
	if ackFn == nil {
		ackFn = func(string) error { return nil }
	}
	return &Subscriber{
		socketPath: socketPath,
		log:        log,
		closeCh:    make(chan struct{}),
		events:     make(chan Event, 64),
		ackFn:      ackFn,
		reconnect:  2 * time.Second,
	}
}

// Events 返回事件通道；relay 在 Run 期间持续读取。
func (s *Subscriber) Events() <-chan Event { return s.events }

// Run 启动订阅循环；ctx 取消后退出。
//
// 自动重连：连接断开后等待 reconnect 时间再 dial。registry 重启期间不丢失消息语义
// 由 relay 在重新连接时走 `GET /v1/events?since=<last_ts>` 增量重放补齐。
func (s *Subscriber) Run(ctx context.Context) error {
	for {
		select {
		case <-ctx.Done():
			return s.Close()
		case <-s.closeCh:
			return nil
		default:
		}
		err := s.dialAndRead(ctx)
		if err != nil {
			if errors.Is(err, context.Canceled) {
				return s.Close()
			}
			if s.log != nil {
				s.log.Warn("regnotify subscriber: connection lost, reconnecting", "err", err)
			}
			select {
			case <-time.After(s.reconnect):
			case <-ctx.Done():
				return s.Close()
			}
		}
	}
}

func (s *Subscriber) dialAndRead(ctx context.Context) error {
	d := net.Dialer{Timeout: 5 * time.Second}
	conn, err := d.DialContext(ctx, "unix", s.socketPath)
	if err != nil {
		return fmt.Errorf("regnotify subscriber: dial: %w", err)
	}
	s.mu.Lock()
	if s.closed {
		s.mu.Unlock()
		_ = conn.Close()
		return nil
	}
	s.conn = conn
	s.mu.Unlock()
	defer func() {
		s.mu.Lock()
		s.conn = nil
		s.mu.Unlock()
		_ = conn.Close()
	}()
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-s.closeCh:
			return nil
		default:
		}
		_ = conn.SetReadDeadline(time.Now().Add(45 * time.Second))
		body, err := readSocketFrame(conn)
		if err != nil {
			if isClosedErr(err) {
				return nil
			}
			if isTimeout(err) {
				continue
			}
			return fmt.Errorf("regnotify subscriber: read: %w", err)
		}
		var ev Event
		if err := json.Unmarshal(body, &ev); err != nil {
			s.log.Warn("regnotify subscriber: bad json", "err", err)
			continue
		}
		// ack events 不对外暴露（registry 不应该 ack 给 relay）；
		// 仅在测试或自检场景出现。
		select {
		case s.events <- ev:
		case <-ctx.Done():
			return ctx.Err()
		case <-s.closeCh:
			return nil
		}
		// send ACK
		ack := NewAckEvent(ev.EventID)
		if err := writeEvent(conn, ack); err != nil {
			s.log.Warn("regnotify subscriber: ack failed", "err", err)
		}
		if err := s.ackFn(ev.EventID); err != nil {
			s.log.Warn("regnotify subscriber: ack callback failed", "err", err)
		}
	}
}

// Close 停止订阅并关闭连接。
func (s *Subscriber) Close() error {
	s.mu.Lock()
	if s.closed {
		s.mu.Unlock()
		return nil
	}
	s.closed = true
	conn := s.conn
	close(s.closeCh)
	s.mu.Unlock()
	if conn != nil {
		_ = conn.Close()
	}
	return nil
}

// (no additional helpers; readSocketFrame 来自 io.go)
