// Package regnotify 实现 registry → relay 的 unix socket 推送通道。
//
// 帧格式（与 .team/vps-prep/01-proto-spec.md §5 一致）：
//
//	[ u32 length BE ]
//	[ length B JSON object (UTF-8) ]
//
// JSON 字段 snake_case，事件类型用 `kind` 字段枚举：
//   - pair_added
//   - pair_revoked
//   - device_key_rotated
//   - pairing_code_issued（可选审计）
//   - ack（relay → registry 订阅端回 ACK）
//
// 投递语义：at-least-once；relay 在处理完成后回 ACK；registry 在 ACK 前最多重发 3 次。
package regnotify

import (
	"time"

	"github.com/google/uuid"

	"desklink/vps/internal/proto"
)

// EventKind 事件类型枚举。
type EventKind string

const (
	KindPairAdded         EventKind = "pair_added"
	KindPairRevoked       EventKind = "pair_revoked"
	KindDeviceKeyRotated  EventKind = "device_key_rotated"
	KindPairingCodeIssued EventKind = "pairing_code_issued"
	KindAck               EventKind = "ack"
)

// Event 是 unix socket 推送的统一事件结构。
//
// JSON tag 严格 snake_case；字段命名遵循 .team/vps-prep/01-proto-spec.md §5.2 修订（pair_id 字段 = pair_uuid）。
type Event struct {
	V              int       `json:"v"`
	Kind           EventKind `json:"kind"`
	EventID        string    `json:"event_id"`         // UUIDv7（relay 用作 ACK 去重键）
	TS             int64     `json:"ts"`               // UNIX 秒
	PairID         string    `json:"pair_id"`          // UUIDv7 字符串（pair_uuid）
	PairIDInternal string    `json:"pair_id_internal"` // 32B hex
	DeviceA        string    `json:"device_a"`         // 32B hex
	DeviceB        string    `json:"device_b"`         // 32B hex
	FingerprintA   string    `json:"fingerprint_a,omitempty"`
	FingerprintB   string    `json:"fingerprint_b,omitempty"`
	DeviceID       string    `json:"device_id,omitempty"` // 用于 device_key_rotated
	OldPubKey      string    `json:"old_pubkey,omitempty"`
	NewPubKey      string    `json:"new_pubkey,omitempty"`
	OldFingerprint string    `json:"old_fingerprint,omitempty"`
	NewFingerprint string    `json:"new_fingerprint,omitempty"`
	Reason         string    `json:"reason,omitempty"` // pair_revoked / key_rotation
	ExpiresAt      int64     `json:"expires_at,omitempty"`
	// ack-only
	AckEventID string `json:"ack_event_id,omitempty"`
}

// NewPairAddedEvent 构造 pair_added 事件。
func NewPairAddedEvent(pairUUID string, pairIDInternal [proto.PairIDSize]byte, deviceA, deviceB [proto.DeviceIDSize]byte, fpA, fpB [proto.FingerprintSize]byte) Event {
	return Event{
		V:              1,
		Kind:           KindPairAdded,
		EventID:        uuid.NewString(),
		TS:             time.Now().Unix(),
		PairID:         pairUUID,
		PairIDInternal: hexBytes(pairIDInternal[:]),
		DeviceA:        hexBytes(deviceA[:]),
		DeviceB:        hexBytes(deviceB[:]),
		FingerprintA:   hexBytes(fpA[:]),
		FingerprintB:   hexBytes(fpB[:]),
	}
}

// NewPairRevokedEvent 构造 pair_revoked 事件。
func NewPairRevokedEvent(pairUUID string, pairIDInternal [proto.PairIDSize]byte, deviceA, deviceB [proto.DeviceIDSize]byte, reason string) Event {
	return Event{
		V:              1,
		Kind:           KindPairRevoked,
		EventID:        uuid.NewString(),
		TS:             time.Now().Unix(),
		PairID:         pairUUID,
		PairIDInternal: hexBytes(pairIDInternal[:]),
		DeviceA:        hexBytes(deviceA[:]),
		DeviceB:        hexBytes(deviceB[:]),
		Reason:         reason,
	}
}

// NewDeviceKeyRotatedEvent 构造 device_key_rotated 事件。
func NewDeviceKeyRotatedEvent(deviceID [proto.DeviceIDSize]byte, oldPub, newPub proto.PublicKey) Event {
	oldFP := oldPub.Fingerprint()
	newFP := newPub.Fingerprint()
	return Event{
		V:              1,
		Kind:           KindDeviceKeyRotated,
		EventID:        uuid.NewString(),
		TS:             time.Now().Unix(),
		DeviceID:       hexBytes(deviceID[:]),
		OldPubKey:      oldPub.Hex(),
		NewPubKey:      newPub.Hex(),
		OldFingerprint: hexBytes(oldFP[:]),
		NewFingerprint: hexBytes(newFP[:]),
		Reason:         "key_rotation",
	}
}

// NewAckEvent 构造 ack 事件（relay 用）。
func NewAckEvent(eventID string) Event {
	return Event{
		V:          1,
		Kind:       KindAck,
		EventID:    uuid.NewString(),
		TS:         time.Now().Unix(),
		AckEventID: eventID,
	}
}

func hexBytes(b []byte) string {
	const hexdigits = "0123456789abcdef"
	out := make([]byte, len(b)*2)
	for i, v := range b {
		out[i*2] = hexdigits[v>>4]
		out[i*2+1] = hexdigits[v&0xF]
	}
	return string(out)
}
