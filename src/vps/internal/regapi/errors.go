package regapi

import (
	"crypto/rand"
	"encoding/hex"
	"net/http"
)

// 错误码枚举（与 .team/vps-prep/01-proto-spec.md §6.3 对齐）。
const (
	ErrProtoVersion      = "proto_version_mismatch"
	ErrAuthMissing       = "auth_missing"
	ErrDeviceNotFound    = "device_not_found"
	ErrPairNotFound      = "pair_not_found"
	ErrPairAlreadyExists = "pair_already_exists"
	ErrCodeInvalid       = "pairing_code_invalid"
	ErrCodeExpired       = "pairing_code_expired"
	ErrCodeConsumed      = "pairing_code_consumed"
	ErrRateLimited       = "rate_limited"
	ErrBadRequest        = "bad_request"
	ErrInternal          = "internal"
)

// errResponse 是错误响应体。
type errResponse struct {
	Error   string `json:"error"`
	Message string `json:"message"`
	TraceID string `json:"trace_id"`
}

// writeError 写 JSON 错误响应，附带 trace_id（4B 随机 hex）便于排查。
func writeError(w http.ResponseWriter, status int, code, msg string) {
	traceID := make([]byte, 4)
	_, _ = rand.Read(traceID)
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	w.Write([]byte(`{"error":"` + code + `","message":"` + escapeJSON(msg) + `","trace_id":"` + hex.EncodeToString(traceID) + `"}`))
}

func escapeJSON(s string) string {
	out := make([]byte, 0, len(s))
	for i := 0; i < len(s); i++ {
		switch s[i] {
		case '"', '\\':
			out = append(out, '\\', s[i])
		case '\n':
			out = append(out, '\\', 'n')
		case '\r':
			out = append(out, '\\', 'r')
		case '\t':
			out = append(out, '\\', 't')
		default:
			if s[i] < 0x20 {
				out = append(out, '?')
			} else {
				out = append(out, s[i])
			}
		}
	}
	return string(out)
}

// writeJSON 写 JSON 响应体（手动序列化避免引入 encoding/json 调用外开销）。
func writeJSON(w http.ResponseWriter, status int, body string) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Connection", "close")
	w.WriteHeader(status)
	_, _ = w.Write([]byte(body))
}
