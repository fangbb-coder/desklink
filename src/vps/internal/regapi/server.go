package regapi

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"strconv"
	"time"
)

// Server 是 regapi HTTP 服务，**仅**监听 127.0.0.1。
type Server struct {
	addr     string
	token    []byte
	handlers *Handlers
	log      *slog.Logger

	srv *http.Server
	ln  net.Listener
}

// NewServer 构造 Server；token 必须为非空字节切片（来自 auth.loadToken）。
func NewServer(addr string, token []byte, handlers *Handlers, log *slog.Logger) *Server {
	return &Server{
		addr:     addr,
		token:    token,
		handlers: handlers,
		log:      log,
	}
}

// Run 启动 HTTP 服务；ctx 取消后优雅关闭。
func (s *Server) Run(ctx context.Context) error {
	mux := http.NewServeMux()
	s.routes(mux)

	// 强制 127.0.0.1 监听：解析 addr 中端口部分，固定 host=127.0.0.1。
	host, port, err := splitHostPort(s.addr)
	if err != nil {
		return fmt.Errorf("regapi: bad addr %q: %w", s.addr, err)
	}
	if host != "" && host != "127.0.0.1" && host != "localhost" {
		return fmt.Errorf("regapi: refusing non-loopback bind %q", s.addr)
	}
	bindAddr := "127.0.0.1:" + strconv.Itoa(port)
	ln, err := net.Listen("tcp", bindAddr)
	if err != nil {
		return fmt.Errorf("regapi: listen %s: %w", bindAddr, err)
	}
	s.ln = ln

	s.srv = &http.Server{
		Handler:      authMiddleware(s.token, mux),
		ReadTimeout:  10 * time.Second,
		WriteTimeout: 10 * time.Second,
		IdleTimeout:  60 * time.Second,
	}
	errCh := make(chan error, 1)
	go func() { errCh <- s.srv.Serve(ln) }()

	select {
	case <-ctx.Done():
		shutCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		return s.srv.Shutdown(shutCtx)
	case err := <-errCh:
		if errors.Is(err, http.ErrServerClosed) {
			return nil
		}
		return err
	}
}

// Addr 返回实际绑定地址（loopback），用于测试或日志。
func (s *Server) Addr() string {
	if s.ln == nil {
		return ""
	}
	return s.ln.Addr().String()
}

// Close 主动关闭服务。
func (s *Server) Close() error {
	if s.srv == nil {
		return nil
	}
	return s.srv.Close()
}

func splitHostPort(addr string) (host string, port int, err error) {
	h, ps, e := net.SplitHostPort(addr)
	if e != nil {
		// addr 形如 "127.0.0.1:7860" 或 ":7860"
		if len(addr) > 0 && addr[0] == ':' {
			p, pe := strconv.Atoi(addr[1:])
			if pe != nil {
				return "", 0, pe
			}
			return "127.0.0.1", p, nil
		}
		return "", 0, e
	}
	p, e := strconv.Atoi(ps)
	if e != nil {
		return "", 0, e
	}
	if h == "" {
		h = "127.0.0.1"
	}
	return h, p, nil
}
