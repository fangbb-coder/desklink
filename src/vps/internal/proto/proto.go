// Package proto 承载 VPS（relayd / registryd）侧使用的协议常量与
// 最小解析工具。它不存放 E2E 加密或业务帧逻辑——那些在 P1（DeskLink.Protocol）
// 与 relayd 的 pump 子包里。
//
// 编码总原则：
//   - 多字节整数统一 big-endian；
//   - 变长字段统一 [u32 length][bytes...] 长度前缀；
//   - 所有哈希与签名为原始字节（不 base64）。
//
// 详细规格见 .team/vps-prep/01-proto-spec.md。
package proto

import (
	"errors"
	"fmt"
)

// ProtoVersion 是 registry/relay 当前实现所认识的协议版本。
// 与 P1 DeskLink.Protocol.ProtoVersion 必须保持一致。
const ProtoVersion uint16 = 1

// MinProtoVersion 是仍兼容的最旧版本。当前与 ProtoVersion 相同。
const MinProtoVersion uint16 = 1

// SchemeName 是 unix socket 帧编码方案标记。
const SchemeName = "length-prefixed-json"

// FrameMaxBytes 是单条 unix socket 帧字节上限。
const FrameMaxBytes = 64 * 1024

// PairingCodeTTLSeconds 是配对码有效期（5 分钟）。
const PairingCodeTTLSeconds int64 = 300

// ErrProtoVersion 在收到不匹配版本的帧时返回。
var ErrProtoVersion = errors.New("proto: version mismatch")

// ErrFrameTooLarge 在长度前缀声明超过 FrameMaxBytes 时返回。
var ErrFrameTooLarge = errors.New("proto: frame too large")

// ErrTruncated 在长度前缀声明与缓冲区不匹配时返回。
var ErrTruncated = errors.New("proto: truncated buffer")

// VersionError 携带期望与实际版本号。
type VersionError struct {
	Expected uint16
	Got      uint16
}

func (e *VersionError) Error() string {
	return fmt.Sprintf("proto: version mismatch (want %d, got %d)", e.Expected, e.Got)
}

// IsVersionError 报告 err 是否为 VersionError。
func IsVersionError(err error) bool {
	var ve *VersionError
	return errors.As(err, &ve)
}
