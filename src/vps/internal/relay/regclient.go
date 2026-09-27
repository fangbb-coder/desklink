// Package relay 子文件 regclient：relayd 访问 registryd 的 loopback HTTP 客户端。
//
// 设计（DESIGN.md 偏差项 #5）：registry **严格不暴露公网**，只经 relay 代理访问。
// relayd 与 registryd 同机部署，relay 通过 127.0.0.1 + Bearer token 调 registry API。
//
// 本客户端只封 relay 需要的 4 个调用：
//   - VerifyChallenge  : 校验设备挑战签名（登记 + 返回 device_id）
//   - GetDevice        : 按 device_id 取设备信息（拿 pubkey/fingerprint/revoked）
//   - GetActivePair    : 校验 (deviceA, deviceB) 是否为有效未撤销配对
//   - RevokePair       : 代客户端发起撤销（relay 转发 X-Revoked-By-DeviceID-Hex）
package relay

import (
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"

	"desklink/vps/internal/proto"
)

// RegClient 是 relayd 侧的 registry HTTP 客户端。
//
// 并发安全：底层 http.Client 并发安全；RegClient 自身无可变字段。
type RegClient struct {
	baseURL string
	token   string
	http    *http.Client
}

// NewRegClient 构造客户端。baseURL 形如 http://127.0.0.1:7860。
func NewRegClient(baseURL, token string) *RegClient {
	return &RegClient{
		baseURL: strings.TrimRight(baseURL, "/"),
		token:   token,
		http: &http.Client{
			// relay 与 registry 同机；2s 足够。超时避免 registry 卡死拖垮 relay 接线。
			Timeout: 5 * time.Second,
		},
	}
}

// DeviceInfo 是 registry 返回的设备视图（relay 只读关心字段）。
type DeviceInfo struct {
	DeviceIDHex    string `json:"device_id_hex"`
	PubKeyHex      string `json:"ed25519_pubkey_hex"`
	FingerprintHex string `json:"fingerprint_hex"`
	LastSeen       int64  `json:"last_seen"`
	Revoked        bool   `json:"revoked"`
}

// PairCheck 是 GetActivePair 的结果。
type PairCheck struct {
	Found    bool
	PairUUID string
}

// ErrRegNotFound registry 返回 404（设备/配对不存在）。
var ErrRegNotFound = fmt.Errorf("relay/regclient: not found")

// do 是统一请求发送 + 错误映射。
func (c *RegClient) do(ctx context.Context, method, path string, body any, out any) error {
	var rdr io.Reader
	if body != nil {
		buf, err := json.Marshal(body)
		if err != nil {
			return fmt.Errorf("relay/regclient: marshal: %w", err)
		}
		rdr = bytes.NewReader(buf)
	}
	req, err := http.NewRequestWithContext(ctx, method, c.baseURL+path, rdr)
	if err != nil {
		return fmt.Errorf("relay/regclient: new request: %w", err)
	}
	req.Header.Set("Authorization", "Bearer "+c.token)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	resp, err := c.http.Do(req)
	if err != nil {
		return fmt.Errorf("relay/regclient: http: %w", err)
	}
	defer resp.Body.Close()
	raw, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return fmt.Errorf("relay/regclient: read body: %w", err)
	}
	if resp.StatusCode == http.StatusNotFound {
		return ErrRegNotFound
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("relay/regclient: status %d: %s", resp.StatusCode, truncate(string(raw), 200))
	}
	if out == nil {
		return nil
	}
	if err := json.Unmarshal(raw, out); err != nil {
		return fmt.Errorf("relay/regclient: unmarshal: %w", err)
	}
	return nil
}

// verifyChallengeReq 与 regapi.VerifyChallengeBody 字段严格一致（snake_case）。
type verifyChallengeReq struct {
	ChallengeID uint32 `json:"challenge_id"`
	PubKey      string `json:"ed25519_pubkey"`
	Signature   []byte `json:"signature"`
	DeviceIDHex string `json:"device_id_hex"`
	TS          string `json:"timestamp"`
	Nonce       []byte `json:"nonce"`
	Hint        string `json:"hint"`
}

// VerifyChallenge 调 registry 校验设备挑战；成功返回 device_id hex。
//
// 注意：真实验签在 registry 侧完成（regapi.verifyEd25519）。relay 只转发，
// 不持有 registry 的密钥信任根——这样"谁能通过鉴权"只有一处判定。
func (c *RegClient) VerifyChallenge(ctx context.Context, challengeID uint32, pubKeyHex string, sig []byte, deviceIDHex, ts string, nonce []byte, hint string) (string, error) {
	req := verifyChallengeReq{
		ChallengeID: challengeID,
		PubKey:      pubKeyHex,
		Signature:   sig,
		DeviceIDHex: deviceIDHex,
		TS:          ts,
		Nonce:       nonce,
		Hint:        hint,
	}
	var out struct {
		OK          bool   `json:"ok"`
		DeviceIDHex string `json:"device_id_hex"`
	}
	if err := c.do(ctx, http.MethodPost, "/v1/devices/verify-challenge", req, &out); err != nil {
		return "", err
	}
	if !out.OK {
		return "", fmt.Errorf("relay/regclient: registry rejected challenge")
	}
	return out.DeviceIDHex, nil
}

// DeviceByPubKey 按公钥查设备（verify-challenge 返回的 device_id 可交叉核对）。
func (c *RegClient) DeviceByPubKey(ctx context.Context, pubKeyHex string) (*DeviceInfo, error) {
	// registry 未提供"按 pubkey 取设备"的独立端点；用 verify-challenge 已返回
	// device_id，这里改为按 device_id 查询。保留本函数签名以便未来扩展。
	return nil, fmt.Errorf("relay/regclient: DeviceByPubKey not implemented (use list-by-device-id)")
}

// ListDevices 全量列设备，返回 device_id → DeviceInfo 映射。
//
// 为什么需要：relay 只拿到 device_id（客户端 Hello 携带），但验签需要 pubkey。
// registry 的 GET /v1/devices 已返回 pubkey，relay 在启动/定期刷新时缓存一份
// 内存映射，避免每次 Hello 都打 registry（接线路径上 registry 不应成为瓶颈）。
func (c *RegClient) ListDevices(ctx context.Context) ([]DeviceInfo, error) {
	var out struct {
		Devices []DeviceInfo `json:"devices"`
	}
	if err := c.do(ctx, http.MethodGet, "/v1/devices", nil, &out); err != nil {
		return nil, err
	}
	return out.Devices, nil
}

// GetDeviceByID 按 device_id hex 取设备（走 ListDevices 过滤；registry 无单设备端点）。
func (c *RegClient) GetDeviceByID(ctx context.Context, deviceIDHex string) (*DeviceInfo, error) {
	devs, err := c.ListDevices(ctx)
	if err != nil {
		return nil, err
	}
	for i := range devs {
		if strings.EqualFold(devs[i].DeviceIDHex, deviceIDHex) {
			return &devs[i], nil
		}
	}
	return nil, ErrRegNotFound
}

// GetActivePair 校验 (deviceA, deviceB) 是否为有效未撤销配对。
//
// registry 的 GET /v1/devices/{id}/pairs 返回该设备的全部有效配对；
// relay 取 deviceA 的列表，检查其中是否含 deviceB。这样只需一个端点。
func (c *RegClient) GetActivePair(ctx context.Context, deviceA, deviceB [proto.DeviceIDSize]byte) (*PairCheck, error) {
	aHex := hex.EncodeToString(deviceA[:])
	bHex := hex.EncodeToString(deviceB[:])
	var out struct {
		Pairs []struct {
			PairUUID        string `json:"pair_uuid"`
			PeerDeviceIDHex string `json:"peer_device_id_hex"`
		} `json:"pairs"`
	}
	if err := c.do(ctx, http.MethodGet, "/v1/devices/"+aHex+"/pairs", nil, &out); err != nil {
		return nil, err
	}
	for _, p := range out.Pairs {
		if strings.EqualFold(p.PeerDeviceIDHex, bHex) {
			return &PairCheck{Found: true, PairUUID: p.PairUUID}, nil
		}
	}
	return &PairCheck{Found: false}, nil
}

// RevokePair 代客户端发起配对撤销。
//
// byDeviceIDHex 会写入 X-Revoked-By-DeviceID-Hex 头（regapi 对其有强制校验）。
// registry 侧会推送 pair_revoked 事件，relay 的 regnotify 订阅者收到后踢线。
func (c *RegClient) RevokePair(ctx context.Context, pairUUID, byDeviceIDHex string) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodDelete, c.baseURL+"/v1/pairs/"+pairUUID, nil)
	if err != nil {
		return fmt.Errorf("relay/regclient: new request: %w", err)
	}
	req.Header.Set("Authorization", "Bearer "+c.token)
	req.Header.Set("X-Revoked-By-DeviceID-Hex", byDeviceIDHex)
	resp, err := c.http.Do(req)
	if err != nil {
		return fmt.Errorf("relay/regclient: http: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusNotFound {
		return ErrRegNotFound
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		raw, _ := io.ReadAll(io.LimitReader(resp.Body, 512))
		return fmt.Errorf("relay/regclient: revoke status %d: %s", resp.StatusCode, truncate(string(raw), 200))
	}
	return nil
}

// truncate 截断日志用字符串，避免超长响应刷屏。
func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n] + "..."
}
