// Package regapi 实现 registryd 的 loopback HTTP API。
//
// 监听地址严格限制 127.0.0.1（由 server.go 强制），Bearer token 鉴权由 auth.go 中间件执行。
// 端点清单见 .team/vps-prep/01-proto-spec.md §6.2。
package regapi

import (
	"crypto/subtle"
	"errors"
	"fmt"
	"net/http"
	"os"
	"strings"
)

// AuthError 是 token 缺失/无效的错误。
var ErrAuthInvalid = errors.New("regapi: invalid or missing token")

// LoadToken 从文件读取 token（trim 末尾换行），要求文件存在且非空。
// 文件权限建议 0600（由 systemd LoadCredential= 注入）。
// 对外暴露，便于 cmd/registryd 在启动时调用。
func LoadToken(path string) ([]byte, error) {
	if path == "" {
		return nil, errors.New("regapi: empty token file path")
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("regapi: read token: %w", err)
	}
	tok := strings.TrimSpace(string(b))
	if tok == "" {
		return nil, errors.New("regapi: empty token in file")
	}
	return []byte(tok), nil
}

// loadToken 是 LoadToken 的小写别名（保持 authMiddleware 包内调用兼容性）。
var loadToken = LoadToken

// authMiddleware 校验 Authorization: Bearer <token>，使用常量时间比较避免 timing 攻击。
func authMiddleware(expected []byte, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		raw := r.Header.Get("Authorization")
		const prefix = "Bearer "
		if !strings.HasPrefix(raw, prefix) {
			writeError(w, http.StatusUnauthorized, "auth_missing", "missing or malformed Authorization header")
			return
		}
		got := []byte(strings.TrimPrefix(raw, prefix))
		if subtle.ConstantTimeCompare(got, expected) != 1 {
			writeError(w, http.StatusUnauthorized, "auth_invalid", "invalid token")
			return
		}
		next.ServeHTTP(w, r)
	})
}

// clientIP 提取 r.RemoteAddr 的 IP；unix socket 走 127.0.0.1（loopback）。
func clientIP(r *http.Request) string {
	// 优先 X-Forwarded-For（仅 loopback 信任）；正常情况 r.RemoteAddr 直接用。
	if v := r.Header.Get("X-Forwarded-For"); v != "" {
		if i := strings.IndexByte(v, ','); i >= 0 {
			return strings.TrimSpace(v[:i])
		}
		return strings.TrimSpace(v)
	}
	host := r.RemoteAddr
	if i := strings.LastIndexByte(host, ':'); i >= 0 {
		host = host[:i]
	}
	if host == "" {
		return "127.0.0.1"
	}
	return host
}
