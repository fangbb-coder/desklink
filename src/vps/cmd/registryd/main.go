// Command registryd 是 DeskLink VPS 端注册中心服务。
//
// 启动顺序：
//  1. 解析 flag（--data-dir / --listen / --socket / --token-file / --print-config）
//  2. 加载 token
//  3. 打开 SQLite（regstore.Open 自动 Migrate）
//  4. 启动 regnotify.Publisher（unix socket server）
//  5. 启动 regapi.Server（loopback HTTP）
//  6. 等待 SIGINT/SIGTERM，优雅关闭
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
	"time"

	"desklink/vps/internal/regapi"
	"desklink/vps/internal/regnotify"
	"desklink/vps/internal/regstore"
)

func main() {
	var (
		dataDir    = flag.String("data-dir", "./data/registryd", "registry data directory")
		listen     = flag.String("listen", "127.0.0.1:7860", "loopback HTTP listen address (host restricted to 127.0.0.1)")
		socketPath = flag.String("socket", "", "unix socket path for relay subscription; default <data-dir>/regnotify.sock")
		tokenFile  = flag.String("token-file", "", "path to bearer token file (chmod 0600); required")
		verbose    = flag.Bool("v", false, "verbose logging")
	)
	flag.Parse()

	logLevel := slog.LevelInfo
	if *verbose {
		logLevel = slog.LevelDebug
	}
	log := slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: logLevel}))

	if *tokenFile == "" {
		fmt.Fprintln(os.Stderr, "registryd: --token-file is required")
		os.Exit(2)
	}
	if *socketPath == "" {
		*socketPath = *dataDir + "/regnotify.sock"
	}

	token, err := regapi.LoadToken(*tokenFile)
	if err != nil {
		fmt.Fprintf(os.Stderr, "registryd: load token: %v\n", err)
		os.Exit(1)
	}

	ctx, cancel := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer cancel()

	store, err := regstore.Open(ctx, *dataDir)
	if err != nil {
		fmt.Fprintf(os.Stderr, "registryd: open store: %v\n", err)
		os.Exit(1)
	}
	defer func() { _ = store.Close() }()

	publisher, err := regnotify.NewPublisher(*socketPath, log)
	if err != nil {
		fmt.Fprintf(os.Stderr, "registryd: publisher: %v\n", err)
		os.Exit(1)
	}

	handlers := &regapi.Handlers{
		Store:     store,
		Publisher: publisher,
		Log:       log,
	}
	srv := regapi.NewServer(*listen, token, handlers, log)

	var wg sync.WaitGroup
	wg.Add(3)
	errCh := make(chan error, 2)

	go func() {
		defer wg.Done()
		if err := publisher.Run(ctx); err != nil && !errors.Is(err, context.Canceled) {
			errCh <- fmt.Errorf("publisher: %w", err)
		}
	}()
	go func() {
		defer wg.Done()
		if err := srv.Run(ctx); err != nil && !errors.Is(err, context.Canceled) {
			errCh <- fmt.Errorf("api: %w", err)
		}
	}()
	// 后台：清理过期/已消费配对码。启动先清一次，之后每小时一次；
	// 失败只告警不退出（清理是非关键路径，库不可用时 api 协程会先报错）。
	go func() {
		defer wg.Done()
		cleanupPairingCodes(ctx, store, log)
		ticker := time.NewTicker(time.Hour)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				cleanupPairingCodes(ctx, store, log)
			}
		}
	}()

	select {
	case <-ctx.Done():
		log.Info("registryd: shutdown signal received")
	case err := <-errCh:
		log.Error("registryd: component failed", "err", err)
		cancel()
	}

	// 优雅关闭：先关 publisher，再关 srv（HTTP handler 依赖 publisher）
	_ = publisher.Close()
	_ = srv.Close()
	wg.Wait()
	log.Info("registryd: stopped")
}

// LoadToken 是 regapi.LoadToken 的对外别名（main 不引 internal 包符号表深）。
var _ = regapi.LoadToken

// cleanupPairingCodes 跑一次过期配对码清理；失败仅告警（不影响服务可用性）。
func cleanupPairingCodes(ctx context.Context, store *regstore.Store, log *slog.Logger) {
	n, err := store.CleanupExpiredPairingCodes(ctx)
	if err != nil {
		log.Warn("registryd: cleanup pairing codes failed", "err", err)
		return
	}
	if n > 0 {
		log.Info("registryd: cleaned pairing codes", "count", n)
	}
}
