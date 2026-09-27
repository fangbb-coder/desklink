package regapi

import (
	"context"
	"crypto/ed25519"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"strconv"
	"strings"
	"time"

	"desklink/vps/internal/proto"
	"desklink/vps/internal/regnotify"
	"desklink/vps/internal/regstore"
)

// Handlers 持有 regapi 端点的业务依赖。
type Handlers struct {
	Store     *regstore.Store
	Publisher *regnotify.Publisher // 可为 nil（单元测试）
	Log       *slog.Logger
}

// maxBodyBytes 单请求 body 上限（与 .team/vps-prep/01-proto-spec.md §6.1 一致）。
const maxBodyBytes = 64 * 1024

// RegisterBody 是 POST /v1/devices/register 的入参。
type RegisterBody struct {
	DeviceIDHex   string `json:"device_id_hex,omitempty"`
	Ed25519PubKey string `json:"ed25519_pubkey"`
	NicknameCT    []byte `json:"nickname_ct,omitempty"`
	Platform      string `json:"platform,omitempty"`
	AgentMajor    uint8  `json:"agent_major,omitempty"`
	AgentMinor    uint8  `json:"agent_minor,omitempty"`
	AgentPatch    uint8  `json:"agent_patch,omitempty"`
}

// HeartbeatBody 是 POST /v1/devices/heartbeat 的入参。
type HeartbeatBody struct {
	DeviceIDHex string `json:"device_id_hex"`
	TS          int64  `json:"ts,omitempty"`
}

// VerifyChallengeBody 是 POST /v1/devices/verify-challenge 的入参。
type VerifyChallengeBody struct {
	ChallengeID uint32 `json:"challenge_id"`
	PubKey      string `json:"ed25519_pubkey"`
	Signature   []byte `json:"signature"`
	DeviceIDHex string `json:"device_id_hex"`
	TS          string `json:"timestamp"`
	Nonce       []byte `json:"nonce"`
	Hint        string `json:"hint"`
}

// PairingCodeIssueBody 是 POST /v1/pairing-codes 的入参。
//
// 字段命名修正（P3）：本接口要的是**出码端 device_id**，不是公钥。
// 早期字段名 `device_id_hex` 与 redeem 接口的同名字段（那里是公钥）语义冲突，
// 因此改为显式的 `issuer_device_id_hex`；旧名保留为 Deprecated 兼容字段。
type PairingCodeIssueBody struct {
	// IssuerDeviceIDHex 出码端 device_id（64 hex 字符）。
	IssuerDeviceIDHex string `json:"issuer_device_id_hex"`
	// TTLSeconds 配对码有效期（秒）；0 时由 Store 取默认值。
	TTLSeconds int64 `json:"ttl,omitempty"`

	// LegacyDeviceIDHex 是 issuer_device_id_hex 的旧名（Deprecated）。
	LegacyDeviceIDHex string `json:"device_id_hex,omitempty"`
}

// issuerDeviceIDHex 返回出码端 device_id：新字段优先，回退旧字段名。
func (b PairingCodeIssueBody) issuerDeviceIDHex() string {
	if b.IssuerDeviceIDHex != "" {
		return b.IssuerDeviceIDHex
	}
	return b.LegacyDeviceIDHex
}

// PairingCodeRedeemBody 是 POST /v1/pairing-codes/redeem 的入参。
//
// 字段命名修正（P3）：本接口的两个参数都是 **Ed25519 公钥 hex**，不是 device_id。
// 早期版本把领取端公钥命名为 `device_id_hex`，与 issue 接口的同名字段
// （那里确实是 device_id）语义冲突。调用方按名字传 device_id 时，公钥解析仍会
// "成功"（都是 32 字节），但推导出的 device_id 不在 devices 表里，最终撞上
// pairs 表的外键约束，只返回一个无信息量的 500。
//
// 现在统一为显式的 `*_pubkey_hex`；旧名保留为 Deprecated 兼容字段，
// 且 handler 会提前校验"公钥已注册"，把误用变成可操作的 400。
type PairingCodeRedeemBody struct {
	// RedeemerPubKeyHex 领取端 Ed25519 公钥 hex（64 hex 字符）。
	RedeemerPubKeyHex string `json:"redeemer_pubkey_hex"`
	// IssuerPubKeyHex 出码端 Ed25519 公钥 hex（64 hex 字符）。
	IssuerPubKeyHex string `json:"issuer_pubkey_hex"`
	// Code 6 位十进制配对码。
	Code string `json:"code"`

	// LegacyDeviceIDHex 是 redeemer_pubkey_hex 的旧名（Deprecated）。
	// 名字有误导性：其内容是**公钥 hex**，不是 device_id。
	LegacyDeviceIDHex string `json:"device_id_hex,omitempty"`
	// LegacyIssuerPubKey 是 issuer_pubkey_hex 的旧名（Deprecated）。
	LegacyIssuerPubKey string `json:"issuer_pubkey,omitempty"`
}

// redeemerPubKeyHex 返回领取端公钥 hex：新字段优先，回退旧字段名。
func (b PairingCodeRedeemBody) redeemerPubKeyHex() string {
	if b.RedeemerPubKeyHex != "" {
		return b.RedeemerPubKeyHex
	}
	return b.LegacyDeviceIDHex
}

// issuerPubKeyHex 返回出码端公钥 hex：新字段优先，回退旧字段名。
func (b PairingCodeRedeemBody) issuerPubKeyHex() string {
	if b.IssuerPubKeyHex != "" {
		return b.IssuerPubKeyHex
	}
	return b.LegacyIssuerPubKey
}

// RotateKeyBody 是 POST /v1/devices/{device_id_hex}/rotate-key 的入参。
type RotateKeyBody struct {
	OldPubKey string `json:"old_pubkey"`
	NewPubKey string `json:"new_pubkey"`
	Sig       []byte `json:"sig"`
}

// Routes 注册 11 个端点 + 鉴权中间件。
func (s *Server) routes(mux *http.ServeMux) {
	h := s.handlers
	mux.HandleFunc("POST /v1/devices/register", h.handleRegister)
	mux.HandleFunc("POST /v1/devices/heartbeat", h.handleHeartbeat)
	mux.HandleFunc("POST /v1/devices/verify-challenge", h.handleVerifyChallenge)
	mux.HandleFunc("PUT /v1/devices/{device_id_hex}/meta", h.handleUpdateMeta)
	mux.HandleFunc("POST /v1/devices/{device_id_hex}/rotate-key", h.handleRotateKey)
	mux.HandleFunc("POST /v1/pairing-codes", h.handleIssueCode)
	mux.HandleFunc("POST /v1/pairing-codes/redeem", h.handleRedeemCode)
	mux.HandleFunc("DELETE /v1/pairs/{pair_uuid}", h.handleRevokePair)
	mux.HandleFunc("GET /v1/devices", h.handleListDevices)
	mux.HandleFunc("GET /v1/devices/{device_id_hex}/pairs", h.handleListPairsByDevice)
	mux.HandleFunc("GET /v1/events", h.handleListEvents)
}

// ---------- handlers ----------

func (h *Handlers) handleRegister(w http.ResponseWriter, r *http.Request) {
	var body RegisterBody
	if !readJSON(w, r, &body) {
		return
	}
	pk, err := proto.ParsePublicKeyHex(body.Ed25519PubKey)
	if err != nil {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "bad ed25519_pubkey: "+err.Error())
		return
	}
	rec := &regstore.DeviceRecord{
		PubKey:       pk,
		NicknameCT:   body.NicknameCT,
		NicknameAlgo: 0,
		Platform:     body.Platform,
		AgentMajor:   body.AgentMajor,
		AgentMinor:   body.AgentMinor,
		AgentPatch:   body.AgentPatch,
	}
	if err := h.Store.RegisterDevice(r.Context(), rec); err != nil {
		writeError(w, http.StatusInternalServerError, ErrInternal, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, fmt.Sprintf(`{"device_id_hex":"%s"}`, hex.EncodeToString(rec.DeviceID[:])))
}

func (h *Handlers) handleHeartbeat(w http.ResponseWriter, r *http.Request) {
	var body HeartbeatBody
	if !readJSON(w, r, &body) {
		return
	}
	id, err := proto.ParseDeviceIDHex(body.DeviceIDHex)
	if err != nil {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "bad device_id_hex")
		return
	}
	ts := body.TS
	if ts == 0 {
		ts = time.Now().Unix()
	}
	if err := h.Store.Heartbeat(r.Context(), id, ts); err != nil {
		if errors.Is(err, regstore.ErrDeviceNotFound) {
			writeError(w, http.StatusNotFound, ErrDeviceNotFound, "device not found")
			return
		}
		writeError(w, http.StatusInternalServerError, ErrInternal, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, fmt.Sprintf(`{"last_seen":%d}`, ts))
}

func (h *Handlers) handleVerifyChallenge(w http.ResponseWriter, r *http.Request) {
	// 鉴权 + 限速
	if !h.allowRate(r, regstore.LimitChallenge, "") {
		writeError(w, http.StatusTooManyRequests, ErrRateLimited, "too many challenge attempts")
		return
	}
	var body VerifyChallengeBody
	if !readJSON(w, r, &body) {
		return
	}
	pk, err := proto.ParsePublicKeyHex(body.PubKey)
	if err != nil {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "bad ed25519_pubkey")
		return
	}
	rec, err := h.Store.GetDeviceByPubKey(r.Context(), pk)
	if err != nil {
		writeError(w, http.StatusNotFound, ErrDeviceNotFound, "device not found")
		return
	}
	// 验签：relay 不做，仅代理；本端（registry）做 Ed25519 verify。
	// P3 起真实验签（P2 时为 nil 占位）。
	if err := verifyEd25519(rec, body, body.Signature); err != nil {
		writeError(w, http.StatusUnauthorized, "sig_invalid", err.Error())
		return
	}
	writeJSON(w, http.StatusOK, fmt.Sprintf(`{"ok":true,"device_id_hex":"%s"}`, hex.EncodeToString(rec.DeviceID[:])))
}

func (h *Handlers) handleUpdateMeta(w http.ResponseWriter, r *http.Request) {
	idHex := r.PathValue("device_id_hex")
	id, err := proto.ParseDeviceIDHex(idHex)
	if err != nil {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "bad device_id_hex")
		return
	}
	body := struct {
		NicknameCT   []byte  `json:"nickname_ct,omitempty"`
		NicknameAlgo *int32  `json:"nickname_algo,omitempty"`
		Platform     *string `json:"platform,omitempty"`
		AgentMajor   *uint8  `json:"agent_major,omitempty"`
		AgentMinor   *uint8  `json:"agent_minor,omitempty"`
		AgentPatch   *uint8  `json:"agent_patch,omitempty"`
	}{}
	if !readJSON(w, r, &body) {
		return
	}
	var agent *regstore.AgentVersion
	if body.AgentMajor != nil && body.AgentMinor != nil && body.AgentPatch != nil {
		agent = &regstore.AgentVersion{Major: *body.AgentMajor, Minor: *body.AgentMinor, Patch: *body.AgentPatch}
	}
	if err := h.Store.UpdateDeviceMeta(r.Context(), id, body.NicknameCT, body.NicknameAlgo, body.Platform, agent); err != nil {
		if errors.Is(err, regstore.ErrDeviceNotFound) {
			writeError(w, http.StatusNotFound, ErrDeviceNotFound, "device not found")
			return
		}
		writeError(w, http.StatusInternalServerError, ErrInternal, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, `{"ok":true}`)
}

func (h *Handlers) handleRotateKey(w http.ResponseWriter, r *http.Request) {
	idHex := r.PathValue("device_id_hex")
	id, err := proto.ParseDeviceIDHex(idHex)
	if err != nil {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "bad device_id_hex")
		return
	}
	var body RotateKeyBody
	if !readJSON(w, r, &body) {
		return
	}
	oldPK, err := proto.ParsePublicKeyHex(body.OldPubKey)
	if err != nil {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "bad old_pubkey")
		return
	}
	newPK, err := proto.ParsePublicKeyHex(body.NewPubKey)
	if err != nil {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "bad new_pubkey")
		return
	}
	if err := h.Store.RotateKey(r.Context(), id, oldPK, newPK, body.Sig, 0); err != nil {
		if errors.Is(err, regstore.ErrDeviceNotFound) {
			writeError(w, http.StatusNotFound, ErrDeviceNotFound, "device not found")
			return
		}
		writeError(w, http.StatusBadRequest, ErrBadRequest, err.Error())
		return
	}
	// 推送 device_key_rotated 事件
	if h.Publisher != nil {
		h.Publisher.Publish(regnotify.NewDeviceKeyRotatedEvent(id, oldPK, newPK))
	}
	writeJSON(w, http.StatusOK, fmt.Sprintf(`{"new_fingerprint_hex":"%s"}`, newPK.FingerprintHex()))
}

func (h *Handlers) handleIssueCode(w http.ResponseWriter, r *http.Request) {
	var body PairingCodeIssueBody
	if !readJSON(w, r, &body) {
		return
	}
	issuerHex := body.issuerDeviceIDHex()
	if issuerHex == "" {
		writeError(w, http.StatusBadRequest, ErrBadRequest,
			"issuer_device_id_hex is required (the issuing device's device_id, 64 hex chars)")
		return
	}
	id, err := proto.ParseDeviceIDHex(issuerHex)
	if err != nil {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "bad issuer_device_id_hex: "+err.Error())
		return
	}
	// 出码端必须已注册：否则码发出去也没人能领（redeem 会因对端查不到而失败）。
	if _, err := h.Store.GetDevice(r.Context(), id); err != nil {
		if errors.Is(err, regstore.ErrDeviceNotFound) {
			writeError(w, http.StatusBadRequest, ErrDeviceNotFound,
				"issuer_device_id_hex is not a registered device")
			return
		}
		writeError(w, http.StatusInternalServerError, ErrInternal, err.Error())
		return
	}
	ttl := time.Duration(body.TTLSeconds) * time.Second
	code, err := h.Store.IssuePairingCode(r.Context(), id, ttl)
	if err != nil {
		writeError(w, http.StatusInternalServerError, ErrInternal, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, fmt.Sprintf(`{"code":"%s","expires_at":%d}`, code.String(), time.Now().Add(ttl).Unix()))
}

// handleRedeemCode 处理配对码领取。
//
// 入参语义（P3 修正，详见 PairingCodeRedeemBody 的注释）：
//   - redeemer_pubkey_hex：领取端 **Ed25519 公钥 hex**（不是 device_id）
//   - issuer_pubkey_hex  ：出码端 **Ed25519 公钥 hex**
//   - code               ：6 位十进制配对码
//
// 旧字段名 device_id_hex / issuer_pubkey 仍被接受（Deprecated）。
func (h *Handlers) handleRedeemCode(w http.ResponseWriter, r *http.Request) {
	var body PairingCodeRedeemBody
	if !readJSON(w, r, &body) {
		return
	}
	if !h.allowRate(r, regstore.LimitRedeemByIP, "") {
		writeError(w, http.StatusTooManyRequests, ErrRateLimited, "too many redeem attempts by IP")
		return
	}
	if !h.allowRate(r, regstore.LimitRedeemByCode, body.Code) {
		writeError(w, http.StatusTooManyRequests, ErrRateLimited, "too many redeem attempts for this code")
		return
	}
	code, err := proto.ParsePairingCode(body.Code)
	if err != nil {
		writeError(w, http.StatusBadRequest, ErrCodeInvalid, "bad code format")
		return
	}

	redeemerHex := body.redeemerPubKeyHex()
	if redeemerHex == "" {
		writeError(w, http.StatusBadRequest, ErrBadRequest,
			"redeemer_pubkey_hex is required (the redeeming device's Ed25519 public key, 64 hex chars)")
		return
	}
	issuerHex := body.issuerPubKeyHex()
	if issuerHex == "" {
		writeError(w, http.StatusBadRequest, ErrBadRequest,
			"issuer_pubkey_hex is required (the issuing device's Ed25519 public key, 64 hex chars)")
		return
	}

	redeemerPK, err := proto.ParsePublicKeyHex(redeemerHex)
	if err != nil {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "bad redeemer_pubkey_hex: "+err.Error())
		return
	}
	issuerPK, err := proto.ParsePublicKeyHex(issuerHex)
	if err != nil {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "bad issuer_pubkey_hex: "+err.Error())
		return
	}

	// 关键：先确认两端公钥**都已注册**，再进事务。
	//
	// 这两个参数是公钥而非 device_id。若调用方按旧字段名传了 device_id，
	// 公钥解析依然会"成功"（device_id 恰好也是 32 字节），但推导出的 device_id
	// 不在 devices 表里；等到写 pairs 行时才会撞上 FOREIGN KEY 约束，
	// 最后只冒出一个无信息量的 500（历史现象）。
	// 这里提前查表，把这类误用变成可操作的 400。
	if _, err := h.Store.GetDeviceByPubKey(r.Context(), redeemerPK); err != nil {
		if errors.Is(err, regstore.ErrDeviceNotFound) {
			writeError(w, http.StatusBadRequest, ErrDeviceNotFound,
				"redeemer_pubkey_hex is not a registered device public key "+
					"(this field takes the Ed25519 public key, not the device_id)")
			return
		}
		writeError(w, http.StatusInternalServerError, ErrInternal, err.Error())
		return
	}
	if _, err := h.Store.GetDeviceByPubKey(r.Context(), issuerPK); err != nil {
		if errors.Is(err, regstore.ErrDeviceNotFound) {
			writeError(w, http.StatusBadRequest, ErrDeviceNotFound,
				"issuer_pubkey_hex is not a registered device public key")
			return
		}
		writeError(w, http.StatusInternalServerError, ErrInternal, err.Error())
		return
	}

	result, err := h.Store.RedeemPairingCode(r.Context(), code, issuerPK, redeemerPK)
	if err != nil {
		switch {
		case errors.Is(err, regstore.ErrPairingCodeInvalid):
			writeError(w, http.StatusBadRequest, ErrCodeInvalid, "invalid code")
		case errors.Is(err, regstore.ErrPairingCodeExpired):
			writeError(w, http.StatusBadRequest, ErrCodeExpired, "code expired")
		case errors.Is(err, regstore.ErrPairingCodeConsumed):
			writeError(w, http.StatusBadRequest, ErrCodeConsumed, "code already consumed")
		case errors.Is(err, regstore.ErrPairAlreadyExists):
			// 两设备间已有活跃配对：客户端可引导用户先撤销旧配对。
			writeError(w, http.StatusConflict, ErrPairAlreadyExists,
				"an active pair between these devices already exists; revoke it first")
		default:
			writeError(w, http.StatusInternalServerError, ErrInternal, err.Error())
		}
		return
	}
	// 推送 pair_added 事件
	if h.Publisher != nil {
		h.Publisher.Publish(regnotify.NewPairAddedEvent(
			result.Pair.PairUUID, result.Pair.PairID,
			result.Pair.DeviceA, result.Pair.DeviceB,
			result.Pair.FingerprintA, result.Pair.FingerprintB,
		))
	}
	writeJSON(w, http.StatusOK, fmt.Sprintf(`{"pair_uuid":"%s","peer_device_id_hex":"%s","peer_pubkey_hex":"%s"}`,
		result.Pair.PairUUID,
		hex.EncodeToString(result.IssuerDeviceID[:]),
		issuerPK.Hex(),
	))
}

func (h *Handlers) handleRevokePair(w http.ResponseWriter, r *http.Request) {
	pairUUID := r.PathValue("pair_uuid")
	if pairUUID == "" {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "empty pair_uuid")
		return
	}
	by := proto.PublicKey{} // 由 relay 在请求 header X-Revoked-By-DeviceID-Hex 提供
	if v := r.Header.Get("X-Revoked-By-DeviceID-Hex"); v != "" {
		id, err := proto.ParseDeviceIDHex(v)
		if err != nil {
			writeError(w, http.StatusBadRequest, ErrBadRequest, "bad X-Revoked-By-DeviceID-Hex")
			return
		}
		_ = by
		rec, already, err := h.Store.RevokePairByUUID(r.Context(), pairUUID, id, regstore.RevokeReasonUserRevoked)
		if err != nil {
			if errors.Is(err, regstore.ErrPairNotFound) {
				writeError(w, http.StatusNotFound, ErrPairNotFound, "pair not found")
				return
			}
			writeError(w, http.StatusInternalServerError, ErrInternal, err.Error())
			return
		}
		if h.Publisher != nil && !already {
			h.Publisher.Publish(regnotify.NewPairRevokedEvent(
				rec.PairUUID, rec.PairID,
				rec.DeviceA, rec.DeviceB,
				string(regstore.RevokeReasonUserRevoked),
			))
		}
		writeJSON(w, http.StatusOK, fmt.Sprintf(`{"ok":true,"already_revoked":%t}`, already))
		return
	}
	// 缺 header：按 spec 视为 bad request
	writeError(w, http.StatusBadRequest, ErrBadRequest, "missing X-Revoked-By-DeviceID-Hex header")
}

func (h *Handlers) handleListDevices(w http.ResponseWriter, r *http.Request) {
	devs, err := h.Store.ListDevices(r.Context())
	if err != nil {
		writeError(w, http.StatusInternalServerError, ErrInternal, err.Error())
		return
	}
	var sb strings.Builder
	sb.WriteString(`{"devices":[`)
	for i, d := range devs {
		if i > 0 {
			sb.WriteString(",")
		}
		fmt.Fprintf(&sb, `{"device_id_hex":"%s","ed25519_pubkey_hex":"%s","fingerprint_hex":"%s","last_seen":%d,"revoked":%t}`,
			hex.EncodeToString(d.DeviceID[:]),
			d.PubKey.Hex(),
			hex.EncodeToString(d.Fingerprint[:]),
			d.LastSeenAt,
			d.Revoked)
	}
	sb.WriteString(`]}`)
	writeJSON(w, http.StatusOK, sb.String())
}

func (h *Handlers) handleListPairsByDevice(w http.ResponseWriter, r *http.Request) {
	idHex := r.PathValue("device_id_hex")
	id, err := proto.ParseDeviceIDHex(idHex)
	if err != nil {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "bad device_id_hex")
		return
	}
	pairs, err := h.Store.ListPairsByDevice(r.Context(), id)
	if err != nil {
		writeError(w, http.StatusInternalServerError, ErrInternal, err.Error())
		return
	}
	var sb strings.Builder
	sb.WriteString(`{"pairs":[`)
	for i, p := range pairs {
		if i > 0 {
			sb.WriteString(",")
		}
		fmt.Fprintf(&sb, `{"pair_uuid":"%s","peer_device_id_hex":"%s"}`,
			p.PairUUID,
			peerDeviceID(p, id),
		)
	}
	sb.WriteString(`]}`)
	writeJSON(w, http.StatusOK, sb.String())
}

func peerDeviceID(p *regstore.PairRecord, self [proto.DeviceIDSize]byte) string {
	if p.DeviceA == self {
		return hex.EncodeToString(p.DeviceB[:])
	}
	return hex.EncodeToString(p.DeviceA[:])
}

func (h *Handlers) handleListEvents(w http.ResponseWriter, r *http.Request) {
	// 当前不实现完整 events API（见 01-proto-spec.md §5.4：relay 重连走此通道增量重放）；
	// 先返回空集 + 文档待 P3 由 regclient 实现。
	writeJSON(w, http.StatusOK, `{"events":[]}`)
}

// ---------- helpers ----------

func readJSON[T any](w http.ResponseWriter, r *http.Request, dst *T) bool {
	r.Body = http.MaxBytesReader(w, r.Body, maxBodyBytes)
	dec := json.NewDecoder(r.Body)
	dec.DisallowUnknownFields()
	if err := dec.Decode(dst); err != nil {
		if errors.Is(err, io.EOF) {
			writeError(w, http.StatusBadRequest, ErrBadRequest, "empty body")
		} else {
			writeError(w, http.StatusBadRequest, ErrBadRequest, "bad json: "+err.Error())
		}
		return false
	}
	if dec.More() {
		writeError(w, http.StatusBadRequest, ErrBadRequest, "trailing content")
		return false
	}
	return true
}

func (h *Handlers) allowRate(r *http.Request, kind regstore.LimitKind, code string) bool {
	return h.Store.RateLimiter().Allow(clientIP(r), kind, code)
}

// challengeDomain 是设备挑战签名的域分隔前缀（与 proto.ChallengeTranscriptDomain
// 及 C# 侧 DeviceChallenge 对齐；三方必须一致）。
//
// 签名内容（transcript）= challengeDomain || challenge_id(4B BE) || ts(8B BE) ||
// nonce(32B) || device_id(32B) || ed25519_pubkey(32B)。
//
// 之所以把 device_id 与 pubkey 也纳入 transcript：即使攻击者拿到某设备的
// nonce/challenge_id，也无法用另一把密钥伪造出通过验证的签名（防密钥替换）。
const challengeDomain = proto.ChallengeTranscriptDomain

// ChallengeTimestampSkewSeconds 是挑战时间戳允许的最大偏移（秒）。
// relay 与 registry 可能不同步（自用 VPS 上通常由 NTP 同步）；给 120s 容忍窗
// 避免轻微时钟漂移导致所有连接失败，同时仍能拒绝重放很久以前的旧签名。
const ChallengeTimestampSkewSeconds = 120

// verifyEd25519 用 rec.PubKey 验证 signature 对 transcript 的 Ed25519 签名。
//
// 语义（P3 实现，替换 P2 的 nil 占位）：
//   - rec.PubKey 必须与 body.PubKey 一致（防止攻击者用自己的 pubkey 覆盖他人设备）；
//   - signature 必须恰好 64 字节；
//   - transcript 由 (challenge_id, ts, nonce, device_id, pubkey) 确定性派生。
//
// 返回 nil 表示验证通过；否则返回带原因的 error（由 handleVerifyChallenge 转为
// HTTP 401 sig_invalid）。
func verifyEd25519(rec *regstore.DeviceRecord, body VerifyChallengeBody, signature []byte) error {
	if rec == nil {
		return errors.New("device record is nil")
	}
	if len(signature) != ed25519.SignatureSize {
		return fmt.Errorf("signature must be %d bytes, got %d", ed25519.SignatureSize, len(signature))
	}
	tr, err := transcript(body, rec)
	if err != nil {
		return err
	}
	if !ed25519.Verify(ed25519.PublicKey(rec.PubKey[:]), tr, signature) {
		return errors.New("ed25519 verify failed")
	}
	return nil
}

// transcript 构造被签名的确定性字节串。任何字段缺失都会返回 error 而不是
// 静默降级，因为静默降级等于放宽鉴权强度。
func transcript(body VerifyChallengeBody, rec *regstore.DeviceRecord) ([]byte, error) {
	if rec == nil {
		return nil, errors.New("device record is nil")
	}
	if len(body.Nonce) != challengeNonceLen {
		return nil, fmt.Errorf("nonce must be %d bytes, got %d", challengeNonceLen, len(body.Nonce))
	}
	if body.TS == "" {
		return nil, errors.New("timestamp is required")
	}
	// 时间戳容差校验：接受 RFC3339(unix 秒) 或纯 unix 秒数字。
	tsUnix, err := parseChallengeTS(body.TS)
	if err != nil {
		return nil, err
	}
	now := time.Now().Unix()
	if diff := now - tsUnix; diff > ChallengeTimestampSkewSeconds || diff < -ChallengeTimestampSkewSeconds {
		return nil, fmt.Errorf("timestamp outside %ds skew window (delta=%ds)", ChallengeTimestampSkewSeconds, diff)
	}
	buf := make([]byte, 0, len(challengeDomain)+4+8+challengeNonceLen+2*proto.DeviceIDSize)
	buf = append(buf, challengeDomain...)
	var num [8]byte
	binary.BigEndian.PutUint32(num[:4], body.ChallengeID)
	buf = append(buf, num[:4]...)
	binary.BigEndian.PutUint64(num[:], uint64(tsUnix))
	buf = append(buf, num[:]...)
	buf = append(buf, body.Nonce...)
	buf = append(buf, rec.DeviceID[:]...)
	buf = append(buf, rec.PubKey[:]...)
	return buf, nil
}

// challengeNonceLen 与 C#/客户端约定一致：32 字节随机 nonce。
const challengeNonceLen = 32

// parseChallengeTS 解析挑战时间戳；支持 RFC3339 与 unix 秒两种形式。
func parseChallengeTS(s string) (int64, error) {
	if t, err := time.Parse(time.RFC3339, s); err == nil {
		return t.Unix(), nil
	}
	if n, err := strconv.ParseInt(s, 10, 64); err == nil {
		return n, nil
	}
	return 0, fmt.Errorf("timestamp %q is neither RFC3339 nor unix seconds", s)
}

var _ = context.Background // 保留 import
