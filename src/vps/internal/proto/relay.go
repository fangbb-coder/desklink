// Package proto 子文件 relay：relayd 与 C# DeskLink.Service 之间"传输层控制帧"的
// 字节级格式。这一层跑在 QUIC / TCP-TLS 之上、E2E（SIGMA）之下，明文但不含任何
// 业务数据——只承载"我是谁 + 我的挑战签名 + 我要连谁"。
//
// 为什么需要独立的控制帧层：
//   - relay 必须知道连接背后的 device_id，才能查接线表决定把密文泵给谁；
//   - relay 必须验证设备身份（Ed25519 挑战签名），否则任何人都能冒充别人占位；
//   - 这些信息必须在 E2E 建立之前以明文交换，因此需要自己一套独立的帧格式，
//     不能复用内层业务帧（那条通道整个是 E2E 密文）。
//
// 统一帧格式（大端）：
//
//	[ u8  kind ]
//	[ u16 payloadLen BE ]
//	[ payloadLen B payload ]
//
// 控制帧类型：
//
//	KindRelayHello        (0x01) client → relay  建立会话，携带 device_id 与挑战签名
//	KindRelayHelloAck     (0x02) relay  → client 会话建立结果（ok / 原因）
//	KindRelayDial         (0x03) client → relay  请求接线到某个 peer device_id
//	KindRelayDialResult   (0x04) relay  → client 接线结果（ok / 原因）
//	KindRelayPeerEvent    (0x05) relay  → client 对端上下线通知（宽限窗内缓存）
//	KindRelayBye          (0x06) 双向     优雅断开（不触发宽限窗）
//	KindRelayPing         (0x07) 双向     保活
//	KindRelayPong         (0x08) 双向     保活应答
//
// 设计取舍：控制帧与业务密文在**同一条字节流**上先后出现。建立阶段 relay 只读
// 控制帧；一旦 DialResult(ok) 返回，这条流上之后的字节全部被 pump 原样转发。
// 这样 TCP/TLS 路径无需额外端口，QUIC 路径无需额外流（P5 若改多流也兼容——
// 控制流用 stream 0，业务流用其他 stream）。
package proto

import (
	"encoding/binary"
	"errors"
	"fmt"
)

// RelayKind 是 relay 控制帧类型。
type RelayKind byte

const (
	KindRelayHello      RelayKind = 0x01
	KindRelayHelloAck   RelayKind = 0x02
	KindRelayDial       RelayKind = 0x03
	KindRelayDialResult RelayKind = 0x04
	KindRelayPeerEvent  RelayKind = 0x05
	KindRelayBye        RelayKind = 0x06
	KindRelayPing       RelayKind = 0x07
	KindRelayPong       RelayKind = 0x08
)

// RelayCtlMaxPayload 是单个控制帧 payload 上限。控制帧只含 device_id / 签名 /
// 短原因字符串，4 KB 绰绰有余；上限收紧避免被恶意长度字段撑爆内存。
const RelayCtlMaxPayload = 4 * 1024

// RelayHelloAckReason 是 Hello/Dial 的失败原因枚举（客户端据此决定是否重试）。
const (
	// ReasonOK 表示成功。
	ReasonOK = "ok"
	// ReasonAuthFailed 挑战签名无效 / device 未注册。
	ReasonAuthFailed = "auth_failed"
	// ReasonProtoMismatch 协议版本不匹配。
	ReasonProtoMismatch = "proto_mismatch"
	// ReasonNotPaired 两端未配对（接线校验失败）。
	ReasonNotPaired = "not_paired"
	// ReasonPeerOffline 对端当前不在线（且已过宽限窗）。
	ReasonPeerOffline = "peer_offline"
	// ReasonRevoked 配对已被撤销，拒绝接线。
	ReasonRevoked = "revoked"
	// ReasonRateLimited 触发限速。
	ReasonRateLimited = "rate_limited"
	// ReasonInternal relay 内部错误。
	ReasonInternal = "internal"
	// ReasonBadFrame 帧格式错误。
	ReasonBadFrame = "bad_frame"
	// ReasonAlreadyWired 本设备已有活跃会话（relayd 单实例语义；新连接顶替旧的）。
	ReasonAlreadyWired = "already_wired"
)

// RelayCtlFrameSize 控制帧头大小：1B kind + 2B len。
const RelayCtlFrameSize = 1 + 2

// ChallengeTranscriptDomain 是设备挑战签名的域分隔前缀。
//
// 该常量被 relayd（internal/relay）、registryd（internal/regapi）与 C# 客户端
// 三方共用，任何改动必须同步。签名内容 = Domain || challengeID(BE32) ||
// unixSeconds(BE64) || nonce(32) || device_id(32) || ed25519_pubkey(32)。
const ChallengeTranscriptDomain = "desklink/device-challenge/v1"

// ErrRelayCtlTooLarge 当控制帧声明长度超限时返回。
var ErrRelayCtlTooLarge = errors.New("proto: relay control frame too large")

// EncodeRelayCtl 把 (kind, payload) 编码为控制帧字节。
func EncodeRelayCtl(kind RelayKind, payload []byte) ([]byte, error) {
	if len(payload) > RelayCtlMaxPayload {
		return nil, fmt.Errorf("%w: %d > %d", ErrRelayCtlTooLarge, len(payload), RelayCtlMaxPayload)
	}
	buf := make([]byte, RelayCtlFrameSize+len(payload))
	buf[0] = byte(kind)
	binary.BigEndian.PutUint16(buf[1:3], uint16(len(payload)))
	copy(buf[3:], payload)
	return buf, nil
}

// RelayCtl 是解码后的控制帧。
type RelayCtl struct {
	Kind    RelayKind
	Payload []byte
}

// TryDecodeRelayCtl 从 src 解析一帧控制帧。
//
// 语义与 TryDecodeFrame 一致：
//   - 字节不足一帧：返回 (nil, 0, nil)，调用方应继续读；
//   - 解码成功：返回 (*RelayCtl, consumed, nil)；
//   - 格式错误：返回 (nil, 0, err)。
func TryDecodeRelayCtl(src []byte) (*RelayCtl, int, error) {
	if len(src) < RelayCtlFrameSize {
		return nil, 0, nil
	}
	kind := RelayKind(src[0])
	plen := int(binary.BigEndian.Uint16(src[1:3]))
	if plen > RelayCtlMaxPayload {
		return nil, 0, fmt.Errorf("%w: declared %d", ErrRelayCtlTooLarge, plen)
	}
	if len(src) < RelayCtlFrameSize+plen {
		return nil, 0, nil
	}
	payload := make([]byte, plen)
	copy(payload, src[RelayCtlFrameSize:RelayCtlFrameSize+plen])
	return &RelayCtl{Kind: kind, Payload: payload}, RelayCtlFrameSize + plen, nil
}

// ---------- RelayHello payload ----------
//
// [ u16 protoVersion BE ][ 32 B device_id ][ 16 B device_id_hint ]
// [ 4 B challenge_id BE ][ 8 B challenge_unix BE ][ 32 B nonce ]
// [ u16 sigLen BE ][ sigLen B Ed25519 signature ]
//
// 其中 signature 的 transcript 必须与 registry 的 verifier 完全一致，见
// internal/relay/challenge.go 的 ChallengeTranscript。
//
// nonce 必须明文出现在 Hello 里：relay 不持有客户端密钥，无法从签名反推 nonce，
// 而 transcript 的构造需要它。nonce 只用于防重放（配合 challengeUnix 时间窗）。

// EncodeRelayHello 编码 Hello payload。
func EncodeRelayHello(protoVersion uint16, deviceID [DeviceIDSize]byte, hint [16]byte, challengeID uint32, challengeUnix int64, nonce []byte, sig []byte) ([]byte, error) {
	if len(sig) != 64 {
		return nil, fmt.Errorf("proto: relay hello signature must be 64 bytes, got %d", len(sig))
	}
	if len(nonce) != 32 {
		return nil, fmt.Errorf("proto: relay hello nonce must be 32 bytes, got %d", len(nonce))
	}
	buf := make([]byte, 0, 2+DeviceIDSize+16+4+8+32+2+len(sig))
	var n [8]byte
	binary.BigEndian.PutUint16(n[:2], protoVersion)
	buf = append(buf, n[:2]...)
	buf = append(buf, deviceID[:]...)
	buf = append(buf, hint[:]...)
	binary.BigEndian.PutUint32(n[:4], challengeID)
	buf = append(buf, n[:4]...)
	binary.BigEndian.PutUint64(n[:], uint64(challengeUnix))
	buf = append(buf, n[:]...)
	buf = append(buf, nonce...)
	binary.BigEndian.PutUint16(n[:2], uint16(len(sig)))
	buf = append(buf, n[:2]...)
	buf = append(buf, sig...)
	return buf, nil
}

// RelayHello 是解码后的 Hello payload。
type RelayHello struct {
	ProtoVersion  uint16
	DeviceID      [DeviceIDSize]byte
	DeviceIDHint  [16]byte
	ChallengeID   uint32
	ChallengeUnix int64
	Nonce         []byte
	Signature     []byte
}

// DecodeRelayHello 解析 Hello payload。
func DecodeRelayHello(p []byte) (*RelayHello, error) {
	const fixed = 2 + DeviceIDSize + 16 + 4 + 8 + 32 + 2
	if len(p) < fixed {
		return nil, fmt.Errorf("proto: relay hello payload too short: %d < %d", len(p), fixed)
	}
	var h RelayHello
	off := 0
	h.ProtoVersion = binary.BigEndian.Uint16(p[off:])
	off += 2
	copy(h.DeviceID[:], p[off:off+DeviceIDSize])
	off += DeviceIDSize
	copy(h.DeviceIDHint[:], p[off:off+16])
	off += 16
	h.ChallengeID = binary.BigEndian.Uint32(p[off:])
	off += 4
	h.ChallengeUnix = int64(binary.BigEndian.Uint64(p[off:]))
	off += 8
	h.Nonce = make([]byte, 32)
	copy(h.Nonce, p[off:off+32])
	off += 32
	sigLen := int(binary.BigEndian.Uint16(p[off:]))
	off += 2
	if sigLen != 64 {
		return nil, fmt.Errorf("proto: relay hello sigLen must be 64, got %d", sigLen)
	}
	if len(p) < off+sigLen {
		return nil, fmt.Errorf("proto: relay hello truncated signature")
	}
	h.Signature = make([]byte, sigLen)
	copy(h.Signature, p[off:off+sigLen])
	return &h, nil
}

// ---------- RelayHelloAck / DialResult payload ----------
//
// [ u8 ok ][ u16 reasonLen BE ][ reason UTF-8 ]
//
// 成功时 reason = ReasonOK。

// EncodeRelayStatus 编码 HelloAck / DialResult payload。
func EncodeRelayStatus(ok bool, reason string) ([]byte, error) {
	rb := []byte(reason)
	if len(rb) > RelayCtlMaxPayload-3 {
		return nil, fmt.Errorf("%w: reason too long", ErrRelayCtlTooLarge)
	}
	buf := make([]byte, 0, 3+len(rb))
	if ok {
		buf = append(buf, 1)
	} else {
		buf = append(buf, 0)
	}
	var n [2]byte
	binary.BigEndian.PutUint16(n[:], uint16(len(rb)))
	buf = append(buf, n[:]...)
	buf = append(buf, rb...)
	return buf, nil
}

// DecodeRelayStatus 解析 HelloAck / DialResult payload。
func DecodeRelayStatus(p []byte) (ok bool, reason string, err error) {
	if len(p) < 3 {
		return false, "", fmt.Errorf("proto: relay status payload too short: %d", len(p))
	}
	ok = p[0] != 0
	rl := int(binary.BigEndian.Uint16(p[1:3]))
	if len(p) < 3+rl {
		return false, "", fmt.Errorf("proto: relay status reason truncated")
	}
	return ok, string(p[3 : 3+rl]), nil
}

// ---------- RelayDial payload ----------
//
// [ 32 B peer_device_id ]
//
// 请求接线到 peer。relay 侧会查接线表：peer 是否在线；再用 registry 校验
// (self, peer) 是否为有效未撤销配对。

// EncodeRelayDial 编码 Dial payload。
func EncodeRelayDial(peer [DeviceIDSize]byte) []byte {
	out := make([]byte, DeviceIDSize)
	copy(out, peer[:])
	return out
}

// DecodeRelayDial 解析 Dial payload。
func DecodeRelayDial(p []byte) ([DeviceIDSize]byte, error) {
	var id [DeviceIDSize]byte
	if len(p) != DeviceIDSize {
		return id, fmt.Errorf("proto: relay dial payload must be %d bytes, got %d", DeviceIDSize, len(p))
	}
	copy(id[:], p)
	return id, nil
}

// ---------- RelayPeerEvent payload ----------
//
// [ u8 event ][ 32 B device_id ]
//
// event: 0x01 = peer_online, 0x02 = peer_offline, 0x03 = peer_ready（对端已完成 dial）。

// RelayPeerEventKind 是对端上下线事件类型。
type RelayPeerEventKind byte

const (
	PeerEventOnline  RelayPeerEventKind = 0x01
	PeerEventOffline RelayPeerEventKind = 0x02
	PeerEventReady   RelayPeerEventKind = 0x03
)

// EncodeRelayPeerEvent 编码 PeerEvent payload。
func EncodeRelayPeerEvent(kind RelayPeerEventKind, deviceID [DeviceIDSize]byte) []byte {
	out := make([]byte, 1+DeviceIDSize)
	out[0] = byte(kind)
	copy(out[1:], deviceID[:])
	return out
}

// DecodeRelayPeerEvent 解析 PeerEvent payload。
func DecodeRelayPeerEvent(p []byte) (RelayPeerEventKind, [DeviceIDSize]byte, error) {
	var id [DeviceIDSize]byte
	if len(p) != 1+DeviceIDSize {
		return 0, id, fmt.Errorf("proto: relay peer event payload must be %d bytes, got %d", 1+DeviceIDSize, len(p))
	}
	copy(id[:], p[1:])
	return RelayPeerEventKind(p[0]), id, nil
}
