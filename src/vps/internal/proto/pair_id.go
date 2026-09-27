package proto

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"

	"lukechampine.com/blake3"
)

// PairIDSize 是 pair_id 内部句柄字节数（与 .team/vps-prep/03-sqlite-schema.md §2.5 一致）。
const PairIDSize = 32

// PairServerNonceSize 是 pair_id 派生中 server_nonce 字节数（>=16 保证充分熵）。
const PairServerNonceSize = 16

// PairID 是 pair_id 内部句柄。
type PairID [PairIDSize]byte

// PairServerNonce 是派生 pair_id 用的 server-side random。
type PairServerNonce [PairServerNonceSize]byte

// NewPairServerNonce 用 crypto/rand 生成 16 字节随机。
func NewPairServerNonce() (PairServerNonce, error) {
	var n PairServerNonce
	if _, err := rand.Read(n[:]); err != nil {
		return n, fmt.Errorf("proto: rand read: %w", err)
	}
	return n, nil
}

// Hex 返回小写 hex。
func (n PairServerNonce) Hex() string {
	return hex.EncodeToString(n[:])
}

// ParsePairServerNonceHex 接受 32 字符 hex 字符串。
func ParsePairServerNonceHex(s string) (PairServerNonce, error) {
	var n PairServerNonce
	b, err := hex.DecodeString(s)
	if err != nil {
		return n, fmt.Errorf("proto: bad hex: %w", err)
	}
	if len(b) != PairServerNonceSize {
		return n, fmt.Errorf("proto: bad nonce size: %d", len(b))
	}
	copy(n[:], b)
	return n, nil
}

// PairIDHex 返回小写 hex。
func (p PairID) Hex() string {
	return hex.EncodeToString(p[:])
}

// ParsePairIDHex 接受 hex 字符串，返回 32B pair_id。
func ParsePairIDHex(s string) (PairID, error) {
	var p PairID
	b, err := hex.DecodeString(s)
	if err != nil {
		return p, fmt.Errorf("proto: bad hex: %w", err)
	}
	if len(b) != PairIDSize {
		return p, fmt.Errorf("proto: bad pair_id size: %d", len(b))
	}
	copy(p[:], b)
	return p, nil
}

// DerivePairID 计算 pair_id = BLAKE3-256(domainPairInst || device_a || device_b ||
// created_at_unixns || server_nonce)。
//
// 实现：lukechampine.com/blake3 v1.4.1（纯 Go BLAKE3，与 C# P1 Blake3.Managed 输出 bit-equal）。
//
// 设计依据：.team/vps-prep/03-sqlite-schema.md §2.5 pair_id 派生澄清。
//   - 字典序 device_a < device_b 必须在调用方已校验（避免同一对在不同调用产生不同 pair_id）；
//   - created_at_unixns 为纳秒级 UNIX 时间戳；
//   - server_nonce 16B 来自 crypto/rand，确保撤销→重配对 pair_id 不同。
//
// 本函数不对输入做合法性校验（性能与简洁度），调用方负责。
func DerivePairID(deviceA, deviceB [DeviceIDSize]byte, createdAtUnixNs int64, nonce PairServerNonce) PairID {
	h := blake3.New(32, nil)
	h.Write([]byte(domainPairInst))
	h.Write(deviceA[:])
	h.Write(deviceB[:])
	// big-endian int64
	for i := 7; i >= 0; i-- {
		h.Write([]byte{byte(createdAtUnixNs >> (i * 8))})
	}
	h.Write(nonce[:])
	var out PairID
	copy(out[:], h.Sum(nil))
	return out
}

// CompareDeviceID 字典序比较两 device_id，返回 -1/0/1。
// 用于保证 (device_a, device_b) 的固定字典序输入。
func CompareDeviceID(a, b [DeviceIDSize]byte) int {
	for i := 0; i < DeviceIDSize; i++ {
		if a[i] < b[i] {
			return -1
		}
		if a[i] > b[i] {
			return 1
		}
	}
	return 0
}

// EnsureOrder 按字典序重排 deviceA、deviceB；返回 (min, max)。
func EnsureOrder(deviceA, deviceB [DeviceIDSize]byte) (min, max [DeviceIDSize]byte) {
	if CompareDeviceID(deviceA, deviceB) <= 0 {
		return deviceA, deviceB
	}
	return deviceB, deviceA
}

// ErrInvalidDeviceIDOrder 当 (a, b) 未按字典序排序时返回（仅用于业务断言）。
var ErrInvalidDeviceIDOrder = errors.New("proto: device_a must be lexicographically <= device_b")
