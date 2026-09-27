package proto

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"

	"lukechampine.com/blake3"
)

// Ed25519PublicKeySize 与 RFC 8032 一致。
const Ed25519PublicKeySize = 32

// FingerprintSize 与 SHA-256 一致；与 P1 DeskLink.Protocol.Fingerprint 常量保持一致。
const FingerprintSize = 32

// DeviceIDSize = BLAKE3-256 输出 32 字节；与 P1 协议层预留的 device_id_hint 字段一致。
//
// 实现：lukechampine.com/blake3 v1.4.1（纯 Go BLAKE3，与 C# P1 Blake3.Managed 输出 bit-equal）。
// 2026-09-24 从 BLAKE2b-256 占位升级为真实 BLAKE3（Leader 裁决 a 项）。
const DeviceIDSize = 32

// Domain separation 字符串（与 .team/vps-prep/01-proto-spec.md §2.2 / §2.5 一致）。
const (
	domainDevice   = "desklink/device/v1"
	domainPairInst = "desklink/pair-instance/v1"
)

// ErrBadPublicKeySize 当公钥长度不是 32 字节时返回。
var ErrBadPublicKeySize = errors.New("proto: bad Ed25519 public key size")

// PublicKey 包装 32 字节 Ed25519 公钥。
type PublicKey [Ed25519PublicKeySize]byte

// ParsePublicKey 接受任意长度字节切片，返回 32 字节定长公钥。
func ParsePublicKey(b []byte) (PublicKey, error) {
	var pk PublicKey
	if len(b) != Ed25519PublicKeySize {
		return pk, fmt.Errorf("%w: got %d", ErrBadPublicKeySize, len(b))
	}
	copy(pk[:], b)
	return pk, nil
}

// Hex 返回小写 hex 字符串。
func (pk PublicKey) Hex() string {
	return hex.EncodeToString(pk[:])
}

// ParsePublicKeyHex 接受 hex 字符串，返回 32 字节公钥。
func ParsePublicKeyHex(s string) (PublicKey, error) {
	b, err := hex.DecodeString(s)
	if err != nil {
		return PublicKey{}, fmt.Errorf("proto: bad hex: %w", err)
	}
	return ParsePublicKey(b)
}

// Fingerprint 计算公钥指纹 = SHA-256(pk)。
// 与 P1 DeskLink.Protocol.Fingerprint 字段定义一致；UI 短指纹取前 16B hex。
func (pk PublicKey) Fingerprint() [FingerprintSize]byte {
	return sha256.Sum256(pk[:])
}

// FingerprintHex 返回 32B 摘要的小写 hex。
func (pk PublicKey) FingerprintHex() string {
	sum := pk.Fingerprint()
	return hex.EncodeToString(sum[:])
}

// ShortFingerprint 返回前 16B hex（32 字符），用于 UI TOFU 短展示。
func (pk PublicKey) ShortFingerprint() string {
	sum := pk.Fingerprint()
	return hex.EncodeToString(sum[:16])
}

// DeviceID 计算 device_id = BLAKE3-256("desklink/device/v1" || pk)。
// 必须与 P1 C# 端 Blake3.Managed bit-equal；详见 .team/vps-prep/05-blake3-cross-check.md。
func (pk PublicKey) DeviceID() [DeviceIDSize]byte {
	h := blake3.New(32, nil)
	h.Write([]byte(domainDevice))
	h.Write(pk[:])
	var out [DeviceIDSize]byte
	copy(out[:], h.Sum(nil))
	return out
}

// DeviceIDHex 返回 device_id 的小写 hex。
func (pk PublicKey) DeviceIDHex() string {
	id := pk.DeviceID()
	return hex.EncodeToString(id[:])
}

// ParseDeviceIDHex 接受 hex 字符串，返回 32B device_id。
func ParseDeviceIDHex(s string) ([DeviceIDSize]byte, error) {
	var id [DeviceIDSize]byte
	b, err := hex.DecodeString(s)
	if err != nil {
		return id, fmt.Errorf("proto: bad hex: %w", err)
	}
	if len(b) != DeviceIDSize {
		return id, fmt.Errorf("proto: bad device_id size: %d", len(b))
	}
	copy(id[:], b)
	return id, nil
}
