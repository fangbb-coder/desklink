package regapi

import (
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"net/url"
	"strings"

	"testing"
	"time"

	"desklink/vps/internal/regstore"
)

type testHarness struct {
	server *Server
	store  *regstore.Store
	addr   string
	token  string
}

func newTestHarness(t *testing.T) *testHarness {
	t.Helper()
	ctx := context.Background()
	store, err := regstore.Open(ctx, t.TempDir())
	if err != nil {
		t.Fatal(err)
	}

	tokenBytes := []byte("0123456789abcdef0123456789abcdef")
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	srv := NewServer("127.0.0.1:0", tokenBytes, &Handlers{
		Store:     store,
		Publisher: nil,
		Log:       logger,
	}, logger)

	if err := srv.listenAndServeForTest(ctx); err != nil {
		t.Fatalf("listenAndServeForTest: %v", err)
	}
	t.Cleanup(func() {
		_ = store.Close()
		_ = srv.Close()
	})

	return &testHarness{
		server: srv,
		store:  store,
		addr:   srv.Addr(),
		token:  string(tokenBytes),
	}
}

// listenAndServeForTest 直接绑端口并启动 Serve goroutine。
func (s *Server) listenAndServeForTest(ctx context.Context) error {
	mux := http.NewServeMux()
	s.routes(mux)
	_, port, err := splitHostPort(s.addr)
	if err != nil {
		return err
	}
	bindAddr := fmt.Sprintf("127.0.0.1:%d", port)
	ln, err := net.Listen("tcp", bindAddr)
	if err != nil {
		return fmt.Errorf("regapi test listen %s: %w", bindAddr, err)
	}
	s.ln = ln
	s.srv = &http.Server{
		Handler:      authMiddleware(s.token, mux),
		ReadTimeout:  10 * time.Second,
		WriteTimeout: 10 * time.Second,
	}
	// 起 Serve 协程即可；这里不等待它结束（harness 的 Shutdown 会关掉 listener）。
	// 早期写法还建了一个 sync.WaitGroup 并 `_ = wg`，既是空操作又触发
	// go vet 的 "copies lock value"（WaitGroup 内含 noCopy）。
	go func() {
		_ = s.srv.Serve(ln)
	}()
	_ = ctx
	return nil
}

func newRequest(t *testing.T, method, rawURL, token string, body any) *http.Request {
	t.Helper()
	var buf []byte
	if body != nil {
		b, err := json.Marshal(body)
		if err != nil {
			t.Fatal(err)
		}
		buf = b
	}
	req, err := http.NewRequest(method, rawURL, bytes.NewReader(buf))
	if err != nil {
		t.Fatal(err)
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	return req
}

func (h *testHarness) do(t *testing.T, method, path string, body any) *http.Response {
	t.Helper()
	rawURL := "http://" + h.addr + path
	req := newRequest(t, method, rawURL, h.token, body)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("HTTP %s %s: %v", method, path, err)
	}
	return resp
}

func mustReadAll(t *testing.T, r io.Reader) []byte {
	t.Helper()
	b, err := io.ReadAll(r)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func TestServerBinds127001Only(t *testing.T) {
	h := newTestHarness(t)
	if h.addr == "" {
		t.Fatal("expected bound addr")
	}
	u, err := url.Parse("http://" + h.addr)
	if err != nil {
		t.Fatal(err)
	}
	if u.Hostname() != "127.0.0.1" {
		t.Fatalf("expected 127.0.0.1, got %q", u.Hostname())
	}
}

func TestAuthMiddleware_401WhenMissing(t *testing.T) {
	h := newTestHarness(t)
	resp, err := http.Get("http://" + h.addr + "/v1/devices")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("expected 401, got %d", resp.StatusCode)
	}
}

func TestAuthMiddleware_401WhenBadToken(t *testing.T) {
	h := newTestHarness(t)
	req := newRequest(t, "GET", "http://"+h.addr+"/v1/devices", "WRONG", nil)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("expected 401, got %d", resp.StatusCode)
	}
}

func TestRegisterAndHeartbeatAndList(t *testing.T) {
	h := newTestHarness(t)
	pubKeyHex := "11" + hex.EncodeToString(make([]byte, 31))
	resp := h.do(t, "POST", "/v1/devices/register", map[string]any{
		"ed25519_pubkey": pubKeyHex,
		"platform":       "windows-11",
		"agent_major":    1,
		"agent_minor":    2,
		"agent_patch":    3,
	})
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		body := mustReadAll(t, resp.Body)
		t.Fatalf("register status=%d body=%s", resp.StatusCode, body)
	}
	var r struct {
		DeviceIDHex string `json:"device_id_hex"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&r); err != nil {
		t.Fatal(err)
	}
	if r.DeviceIDHex == "" {
		t.Fatal("empty device_id_hex")
	}

	resp2 := h.do(t, "POST", "/v1/devices/heartbeat", map[string]any{
		"device_id_hex": r.DeviceIDHex,
		"ts":            time.Now().Unix(),
	})
	defer resp2.Body.Close()
	if resp2.StatusCode != http.StatusOK {
		t.Fatalf("heartbeat status=%d", resp2.StatusCode)
	}

	resp3 := h.do(t, "GET", "/v1/devices", nil)
	defer resp3.Body.Close()
	if resp3.StatusCode != http.StatusOK {
		t.Fatalf("list status=%d", resp3.StatusCode)
	}
}

func TestPairingCodeIssueRedeemRevoke(t *testing.T) {
	h := newTestHarness(t)
	pubA := "aa" + hex.EncodeToString(make([]byte, 31))
	pubB := "bb" + hex.EncodeToString(make([]byte, 31))
	idA := registerDeviceViaHTTP(t, h, pubA)
	registerDeviceViaHTTP(t, h, pubB)

	// Issue
	resp := h.do(t, "POST", "/v1/pairing-codes", map[string]any{
		"issuer_device_id_hex": idA,
		"ttl":                  300,
	})
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		body := mustReadAll(t, resp.Body)
		t.Fatalf("issue status=%d body=%s", resp.StatusCode, body)
	}
	var issued struct {
		Code string `json:"code"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&issued); err != nil {
		t.Fatal(err)
	}
	if issued.Code == "" {
		t.Fatal("empty code")
	}

	// Redeem
	//
	// 注意字段语义（P3 起统一命名）：这里两个参数都是 **Ed25519 公钥 hex**，
	// 不是 device_id。旧字段名 device_id_hex / issuer_pubkey 仍兼容，见
	// TestPairingCodeLegacyFieldNamesStillAccepted 与
	// TestPairingCodeRedeemWithDeviceIDReturns400。
	resp = h.do(t, "POST", "/v1/pairing-codes/redeem", map[string]any{
		"redeemer_pubkey_hex": pubB,
		"code":                issued.Code,
		"issuer_pubkey_hex":   pubA,
	})
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		body := mustReadAll(t, resp.Body)
		t.Fatalf("redeem status=%d body=%s", resp.StatusCode, body)
	}
	var redeem struct {
		PairUUID string `json:"pair_uuid"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&redeem); err != nil {
		t.Fatal(err)
	}
	if redeem.PairUUID == "" {
		t.Fatal("empty pair_uuid")
	}

	// Revoke
	req := newRequest(t, "DELETE", "http://"+h.addr+"/v1/pairs/"+redeem.PairUUID, h.token, nil)
	req.Header.Set("X-Revoked-By-DeviceID-Hex", idA)
	respRev, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer respRev.Body.Close()
	if respRev.StatusCode != http.StatusOK {
		body := mustReadAll(t, respRev.Body)
		t.Fatalf("revoke status=%d body=%s", respRev.StatusCode, body)
	}

	// 二次撤销 → already_revoked=true
	req2 := newRequest(t, "DELETE", "http://"+h.addr+"/v1/pairs/"+redeem.PairUUID, h.token, nil)
	req2.Header.Set("X-Revoked-By-DeviceID-Hex", idA)
	respRev2, err := http.DefaultClient.Do(req2)
	if err != nil {
		t.Fatal(err)
	}
	defer respRev2.Body.Close()
	var secondRev struct {
		AlreadyRevoked bool `json:"already_revoked"`
	}
	if err := json.NewDecoder(respRev2.Body).Decode(&secondRev); err != nil {
		t.Fatal(err)
	}
	if !secondRev.AlreadyRevoked {
		t.Fatal("expected already_revoked=true")
	}
}

// TestPairingCodeRedeemWithDeviceIDReturns400 是 P3 命名修正的回归用例。
//
// 历史现象：调用方把 redeem 的 `device_id_hex` 当成 device_id 传（名字确实这么写），
// 公钥解析仍会"成功"（device_id 也是 32 字节），但推导出的 device_id 不在 devices
// 表里，最后撞 pairs 表 FOREIGN KEY 约束 → 无信息量的 500。
//
// 修正后：提前校验"公钥已注册"，返回 400 + 可操作提示，并显式点出
// "这个字段要的是公钥，不是 device_id"。
func TestPairingCodeRedeemWithDeviceIDReturns400(t *testing.T) {
	h := newTestHarness(t)
	pubA := "aa" + hex.EncodeToString(make([]byte, 31))
	pubB := "bb" + hex.EncodeToString(make([]byte, 31))
	idA := registerDeviceViaHTTP(t, h, pubA)
	idB := registerDeviceViaHTTP(t, h, pubB)

	resp := h.do(t, "POST", "/v1/pairing-codes", map[string]any{
		"issuer_device_id_hex": idA, "ttl": 300,
	})
	defer resp.Body.Close()
	var issued struct {
		Code string `json:"code"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&issued); err != nil {
		t.Fatal(err)
	}

	// 误用：把 device_id 填进公钥字段（旧名 device_id_hex 的典型误用）。
	resp2 := h.do(t, "POST", "/v1/pairing-codes/redeem", map[string]any{
		"redeemer_pubkey_hex": idB, // ← 这是 device_id，不是公钥
		"code":                issued.Code,
		"issuer_pubkey_hex":   pubA,
	})
	defer resp2.Body.Close()

	if resp2.StatusCode != http.StatusBadRequest {
		body := mustReadAll(t, resp2.Body)
		t.Fatalf("expected 400 for device_id-in-pubkey-field, got %d body=%s", resp2.StatusCode, body)
	}
	body := mustReadAll(t, resp2.Body)
	if !strings.Contains(string(body), ErrDeviceNotFound) {
		t.Fatalf("expected error code %q in body, got %s", ErrDeviceNotFound, body)
	}
	// 提示必须点出"要的是公钥"，否则调用方仍不知道该怎么改。
	if !strings.Contains(string(body), "public key") {
		t.Fatalf("error message should mention public key, got %s", body)
	}
}

// TestPairingCodeIssueUnregisteredDeviceReturns400 验证出码端未注册时给出明确 400，
// 而不是让码发出去、等到领取时才失败。
func TestPairingCodeIssueUnregisteredDeviceReturns400(t *testing.T) {
	h := newTestHarness(t)
	// 未注册的 device_id（32 字节 hex）
	unknown := hex.EncodeToString(bytes.Repeat([]byte{0x7f}, 32))

	resp := h.do(t, "POST", "/v1/pairing-codes", map[string]any{
		"issuer_device_id_hex": unknown, "ttl": 300,
	})
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusBadRequest {
		body := mustReadAll(t, resp.Body)
		t.Fatalf("expected 400, got %d body=%s", resp.StatusCode, body)
	}
}

// TestPairingCodeRedeemLegacyFieldNamesStillAccepted 锁定 Deprecated 兼容路径：
// 旧字段名（device_id_hex=领取端公钥、issuer_pubkey=出码端公钥）必须继续可用。
func TestPairingCodeRedeemLegacyFieldNamesStillAccepted(t *testing.T) {
	h := newTestHarness(t)
	pubA := "e1" + hex.EncodeToString(make([]byte, 31))
	pubB := "e2" + hex.EncodeToString(make([]byte, 31))
	idA := registerDeviceViaHTTP(t, h, pubA)
	registerDeviceViaHTTP(t, h, pubB)

	// issue 也用旧名
	resp := h.do(t, "POST", "/v1/pairing-codes", map[string]any{
		"device_id_hex": idA, "ttl": 300,
	})
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		body := mustReadAll(t, resp.Body)
		t.Fatalf("legacy issue status=%d body=%s", resp.StatusCode, body)
	}
	var issued struct {
		Code string `json:"code"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&issued); err != nil {
		t.Fatal(err)
	}

	resp2 := h.do(t, "POST", "/v1/pairing-codes/redeem", map[string]any{
		"device_id_hex": pubB, "code": issued.Code, "issuer_pubkey": pubA,
	})
	defer resp2.Body.Close()
	if resp2.StatusCode != http.StatusOK {
		body := mustReadAll(t, resp2.Body)
		t.Fatalf("legacy redeem status=%d body=%s", resp2.StatusCode, body)
	}
}

func TestRateLimit_RedeemByCode(t *testing.T) {
	// 本用例刻意使用**旧字段名**（device_id_hex / issuer_pubkey），
	// 顺带覆盖 P3 命名修正后保留的 Deprecated 兼容路径。
	h := newTestHarness(t)
	pubA := "cc" + hex.EncodeToString(make([]byte, 31))
	pubB := "dd" + hex.EncodeToString(make([]byte, 31))
	idA := registerDeviceViaHTTP(t, h, pubA)
	registerDeviceViaHTTP(t, h, pubB)

	resp := h.do(t, "POST", "/v1/pairing-codes", map[string]any{"device_id_hex": idA, "ttl": 300})
	defer resp.Body.Close()
	var issued struct {
		Code string `json:"code"`
	}
	json.NewDecoder(resp.Body).Decode(&issued)

	resp = h.do(t, "POST", "/v1/pairing-codes/redeem", map[string]any{
		"device_id_hex": pubB, "code": issued.Code, "issuer_pubkey": pubA,
	})
	resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("first redeem: %d", resp.StatusCode)
	}

	var blocked int
	for i := 0; i < 10; i++ {
		resp = h.do(t, "POST", "/v1/pairing-codes/redeem", map[string]any{
			"device_id_hex": pubB, "code": issued.Code, "issuer_pubkey": pubA,
		})
		resp.Body.Close()
		if resp.StatusCode == http.StatusTooManyRequests {
			blocked++
		}
	}
	if blocked < 1 {
		t.Fatal("expected at least one 429")
	}
}

func TestSplitHostPort(t *testing.T) {
	for _, c := range []struct {
		in   string
		want string
	}{
		{"127.0.0.1:7860", "7860"},
		{":7860", "7860"},
		{"localhost:7860", "7860"},
	} {
		_, p, err := splitHostPort(c.in)
		if err != nil {
			t.Fatalf("%q: %v", c.in, err)
		}
		if fmt.Sprintf("%d", p) != c.want {
			t.Fatalf("%q: port=%d want %s", c.in, p, c.want)
		}
	}
}

func registerDeviceViaHTTP(t *testing.T, h *testHarness, pubKeyHex string) string {
	t.Helper()
	resp := h.do(t, "POST", "/v1/devices/register", map[string]any{
		"ed25519_pubkey": pubKeyHex,
	})
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		body := mustReadAll(t, resp.Body)
		t.Fatalf("register %s: %d %s", pubKeyHex, resp.StatusCode, body)
	}
	var r struct {
		DeviceIDHex string `json:"device_id_hex"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&r); err != nil {
		t.Fatal(err)
	}
	return r.DeviceIDHex
}
