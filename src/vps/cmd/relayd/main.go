// Command relayd 是 DeskLink VPS 端中继服务。
//
// 启动顺序：
//  1. 解析 flag（--listen / --registry / --token-file / --cert / --key / --socket / -v）
//  2. 加载 registry token
//  3. 启动 regnotify.Subscriber（订阅 registry 事件：撤销踢线）
//  4. 启动 relay.Server（QUIC + TCP/TLS 双监听）
//  5. 把订阅事件转成 relay 的踢线动作
//  6. 等待 SIGINT/SIGTERM，优雅关闭
//
// 拓扑（同机部署）：
//
//	[客户端] ⇄ relayd (QUIC/UDP + TCP/TLS) ⇄ [客户端]
//	                │ loopback + Bearer
//	                ▼
//	           registryd (127.0.0.1:7860, SQLite)
//	                │ unix socket push
//	                ▲
//	           regnotify.sock
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"log/slog"
	"os"
	"os/signal"
	"sync"
	"syscall"

	"desklink/vps/internal/regapi"
	"desklink/vps/internal/regnotify"
	"desklink/vps/internal/relay"
)

func main() {
	var (
		listen     = flag.String("listen", ":9443", "中继监听地址（QUIC 绑 UDP、TCP/TLS 绑 TCP 同端口号）")
		registry   = flag.String("registry", "http://127.0.0.1:7860", "registryd loopback HTTP 地址")
		tokenFile  = flag.String("token-file", "", "registry bearer token 文件（required）")
		certPath   = flag.String("cert", "", "TCP/TLS 服务端证书 PEM（生产必填）")
		keyPath    = flag.String("key", "", "TCP/TLS 服务端私钥 PEM（生产必填）")
		socketPath = flag.String("socket", "", "regnotify unix socket 路径（默认 <registry 同目录>/regnotify.sock；--socket 显式覆盖）")
		insecure   = flag.Bool("insecure-tls", false, "使用内存自签证书（仅供本机测试；生产禁用）")
		verbose    = flag.Bool("v", false, "verbose 日志")
	)
	flag.Parse()

	logLevel := slog.LevelInfo
	if *verbose {
		logLevel = slog.LevelDebug
	}
	log := slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: logLevel}))

	if *tokenFile == "" {
		fmt.Fprintln(os.Stderr, "relayd: --token-file is required")
		os.Exit(2)
	}
	if !*insecure && (*certPath == "" || *keyPath == "") {
		fmt.Fprintln(os.Stderr, "relayd: --cert and --key are required (or pass --insecure-tls for local tests)")
		os.Exit(2)
	}

	token, err := regapi.LoadToken(*tokenFile)
	if err != nil {
		fmt.Fprintf(os.Stderr, "relayd: load token: %v\n", err)
		os.Exit(1)
	}

	ctx, cancel := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer cancel()

	reg := relay.NewRegClient(*registry, string(token))

	srv := relay.NewServer(relay.ServerConfig{
		ListenAddr:       *listen,
		RegistryBaseURL:  *registry,
		RegistryToken:    string(token),
		TLSCertPath:      *certPath,
		TLSKeyPath:       *keyPath,
		AllowInsecureTLS: *insecure,
		MaxConnPerIP:     64,
	}, reg, log)

	var wg sync.WaitGroup
	errCh := make(chan error, 3)

	// regnotify 订阅（可选：socket 不存在时仅告警，relay 仍可服务）
	if *socketPath != "" {
		sub := regnotify.NewSubscriber(*socketPath, log, nil)
		wg.Add(1)
		go func() {
			defer wg.Done()
			if err := sub.Run(ctx); err != nil && !errors.Is(err, context.Canceled) {
				log.Warn("relayd: regnotify subscriber stopped", "err", err)
			}
		}()
		// 事件转发循环
		wg.Add(1)
		go func() {
			defer wg.Done()
			for {
				select {
				case <-ctx.Done():
					return
				case ev, ok := <-sub.Events():
					if !ok {
						return
					}
					srv.HandleNotifyEvent(notifyAdapter{ev})
				}
			}
		}()
	} else {
		log.Warn("relayd: regnotify socket not configured; revocation kick disabled")
	}

	// relay 主服务
	wg.Add(1)
	go func() {
		defer wg.Done()
		if err := srv.Run(ctx); err != nil && !errors.Is(err, context.Canceled) {
			errCh <- fmt.Errorf("relay server: %w", err)
		}
	}()

	select {
	case <-ctx.Done():
		log.Info("relayd: shutdown signal received")
	case err := <-errCh:
		log.Error("relayd: component failed", "err", err)
		cancel()
	}

	_ = srv.Close()
	wg.Wait()
	log.Info("relayd: stopped")
}

// notifyAdapter 把 regnotify.Event 适配成 relay.EventLike。
//
// 这样 relay 包不必 import regnotify，保持核心包的可测性与依赖方向干净。
type notifyAdapter struct {
	ev regnotify.Event
}

func (a notifyAdapter) Kind() string        { return string(a.ev.Kind) }
func (a notifyAdapter) DeviceAHex() string  { return a.ev.DeviceA }
func (a notifyAdapter) DeviceBHex() string  { return a.ev.DeviceB }
func (a notifyAdapter) DeviceIDHex() string { return a.ev.DeviceID }
func (a notifyAdapter) Reason() string      { return a.ev.Reason }
