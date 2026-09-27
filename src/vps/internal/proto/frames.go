// Package proto 子包 frames：DeskLink 内层帧目录与编解码（与 C# 侧 Common/ProtocolConstants、Frames/Frame.cs、Mux/MuxFrame.cs 对齐）。
//
// 帧格式：
//
//	QUIC 路径：每个内层帧直接走一个原生 QUIC 流，sid 由调用方约定。
//	TCP  路径：[u32 len][u8 sid][内层帧 bytes...]
//
// 内层帧格式（未加密视图）：
//
//	[u8 type][u16 payloadLen][payload bytes...]
//
// 帧 payload 上限 256 KB，关闭语义固定（Close 帧必须显式发出）。
//
// 注意：本文件只承载帧格式常量与编解码；E2E 加密与会话密钥派生由 C# DeskLink.Protocol
// 全权实现，VPS 端只看到密文。
package proto

import (
	"encoding/binary"
	"errors"
	"fmt"
)

const (
	// ProtocolVersion 与 MagicNumber 与 C# 侧必须严格一致。
	// C# 侧定义在 src/Protocol/DeskLink.Protocol/Common/ProtocolConstants.cs。
	FrameProtocolVersion uint16 = 1
	FrameMagicNumber     uint32 = 0x444C4E4B // "DLNK"

	// MaxFramePayloadSize 帧负载的**缓冲区/DoS 保护**上界（256 KB）。
	//
	// 注意：它**不是**可编码上限。帧头的长度字段是 u16，真正能编码的最大负载
	// 是 MaxFramePayloadWireSize（65535）。本常量只用于给接收侧缓冲区、
	// mux 外层长度做宽松上界，允许比线上格式更大。
	MaxFramePayloadSize = 256 * 1024

	// MaxFramePayloadWireSize 是**线上格式**允许的最大帧负载。
	//
	// 帧头是 [u8 type][u16 len BE]，len 为 u16 → 上限 65535 字节。
	//
	// 为什么必须单独存在（真 bug 记录）：
	//   EncodeFrame 早期只校验 MaxFramePayloadSize(256KB)，而写长度用的是
	//   uint16(len(payload))。于是 65536..262144 之间的负载会**静默回绕**长度字段，
	//   但 payload 字节仍然全量写出：接收端按错误的 len 切片，剩下的字节被当成
	//   下一帧的帧头，整条流从此错位且不报错。
	//   实测：70000B 负载被编码成 len=4464，接收端只消费 4467B，剩余 65536B 全错位。
	//
	// 需要更大的负载时必须由上层分片（DESIGN.md 的 FileChunk / DesktopVideo
	// 分片语义），而不是把长度字段撑爆。
	MaxFramePayloadWireSize = 65535

	// MaxStreamId TCP mux sid 上限 255。
	MaxStreamId byte = 255
	// ReservedStreamId sid=0 保留给握手/控制流。
	ReservedStreamId byte = 0
)

// ErrFramePayloadTooLarge 表示帧负载超过线上格式上限（MaxFramePayloadWireSize）。
// 调用方应当分片，而不是重试或截断。
var ErrFramePayloadTooLarge = errors.New("proto: frame payload exceeds u16 wire limit")

// PathKind 路径类型。
type PathKind byte

const (
	PathRelay  PathKind = 0x01
	PathDirect PathKind = 0x02
)

// LogicalStream 三类逻辑流。
type LogicalStream byte

const (
	StreamDesktop LogicalStream = 0x10
	StreamControl LogicalStream = 0x20
	StreamFile    LogicalStream = 0x30
)

// FrameType 帧类型目录（与 C# ProtocolConstants.FrameType 对齐）。
type FrameType byte

const (
	FrameHello    FrameType = 0x01
	FrameHelloAck FrameType = 0x02
	FrameClose    FrameType = 0x03
	FramePing     FrameType = 0x04
	FramePong     FrameType = 0x05

	FrameDesktopVideo  FrameType = 0x10
	FrameDesktopConfig FrameType = 0x11

	FrameInputMouseMove   FrameType = 0x20
	FrameInputMouseButton FrameType = 0x21
	FrameInputKey         FrameType = 0x22
	FrameInputWheel       FrameType = 0x23
	FrameSessionControl   FrameType = 0x24
	FrameCursorShape      FrameType = 0x25

	FrameListRequest  FrameType = 0x30
	FrameListResponse FrameType = 0x31
	FrameFileOpen     FrameType = 0x32
	FrameFileChunk    FrameType = 0x33
	FrameFileAck      FrameType = 0x34
	FrameFilePause    FrameType = 0x35
	FrameFileResume   FrameType = 0x36
	FrameFileCancel   FrameType = 0x37
	FrameFileComplete FrameType = 0x38
)

// FrameHeaderSize 内层帧头大小：1 byte type + 2 bytes payload len。
const FrameHeaderSize = 1 + 2

// MuxTcpHeaderSize TCP mux 头大小：4 bytes len + 1 byte sid。
const MuxTcpHeaderSize = 4 + 1

// StreamIdFromLogical 按逻辑流与 sub 偏移得到 sid。
//
// 2026-09-23 vps-backend-engineer 补：原实现 `base + sub` 在 byte 类型下溢出时
// 不会触发 `sid > MaxStreamId` 检查（48+250=298 截断为 42）。这里改为 uint16
// 累加后显式比较，避免 byte overflow 漏报。
func StreamIdFromLogical(kind LogicalStream, sub byte) (byte, error) {
	var base byte
	switch kind {
	case StreamDesktop:
		base = 16
	case StreamControl:
		base = 32
	case StreamFile:
		base = 48
	default:
		return 0, errors.New("invalid logical stream")
	}
	sum := uint16(base) + uint16(sub)
	if sum > uint16(MaxStreamId) {
		return 0, errors.New("sub overflow")
	}
	return byte(sum), nil
}

// EncodeFrame 把 (type, payload) 编码为内层帧字节。
//
// 负载上限是 MaxFramePayloadWireSize（65535，由 u16 长度字段决定），
// **不是** MaxFramePayloadSize。超过必须报错：写长度用的是 uint16，
// 静默回绕会让接收端按错误长度切片，导致整条流错位（见常量处的事故记录）。
func EncodeFrame(t FrameType, payload []byte) ([]byte, error) {
	if len(payload) > MaxFramePayloadWireSize {
		return nil, fmt.Errorf("%w: %d > %d (needs fragmentation)",
			ErrFramePayloadTooLarge, len(payload), MaxFramePayloadWireSize)
	}
	buf := make([]byte, FrameHeaderSize+len(payload))
	buf[0] = byte(t)
	binary.BigEndian.PutUint16(buf[1:3], uint16(len(payload)))
	copy(buf[3:], payload)
	return buf, nil
}

// TryDecodeFrame 从 src 解码一帧；返回 (type, payload, consumed, err)。
// 完整返回 (frame, payload, consumed, nil)；不完整返回 (0, nil, 0, nil)；错误返回 (0, nil, 0, err)。
func TryDecodeFrame(src []byte) (FrameType, []byte, int, error) {
	if len(src) < FrameHeaderSize {
		return 0, nil, 0, nil
	}
	t := FrameType(src[0])
	plen := int(binary.BigEndian.Uint16(src[1:3]))
	// 注：plen 来自 u16，天然 ≤ MaxFramePayloadWireSize，此处无需再比较上限。
	// 真正需要把关的是**编码侧**（EncodeFrame），否则长度字段会静默回绕。
	if len(src) < FrameHeaderSize+plen {
		return 0, nil, 0, nil
	}
	payload := make([]byte, plen)
	copy(payload, src[3:3+plen])
	return t, payload, FrameHeaderSize + plen, nil
}

// EncodeMuxTcp 把 (sid, innerFrame) 编码为 TCP mux 外层格式。
func EncodeMuxTcp(sid byte, innerFrame []byte) ([]byte, error) {
	if sid > MaxStreamId {
		return nil, errors.New("sid overflow")
	}
	lenField := uint32(1 + len(innerFrame))
	buf := make([]byte, 4+lenField)
	binary.BigEndian.PutUint32(buf[0:4], lenField)
	buf[4] = sid
	copy(buf[5:], innerFrame)
	return buf, nil
}

// TryDecodeMuxTcp 从 src 解码 TCP mux 外层；返回 (sid, innerFrame, consumed, error)。
func TryDecodeMuxTcp(src []byte) (byte, []byte, int, error) {
	if len(src) < 5 {
		return 0, nil, 0, nil
	}
	l := binary.BigEndian.Uint32(src[0:4])
	// mux 外层 len = sid(1) + innerFrame 长度。innerFrame 可能是：
	//   - 明文内层帧：3(帧头) + payload，payload ≤ MaxFramePayloadWireSize
	//   - E2E 密文：8(计数器) + 3(帧头) + payload + 16(AEAD tag)
	// 故合法上界约 MaxFramePayloadWireSize + 28。
	// 这里保留 MaxFramePayloadSize（256KB）作为**宽松的 DoS 保护**——它不小于任何
	// 合法帧，真正的负载上限由 EncodeFrame 的 u16 校验把关。
	if l == 0 || l > uint32(MaxFramePayloadSize+1) {
		return 0, nil, 0, errors.New("mux tcp len invalid")
	}
	if uint32(len(src)) < 4+l {
		return 0, nil, 0, nil
	}
	sid := src[4]
	inner := make([]byte, int(l)-1)
	copy(inner, src[5:5+int(l)-1])
	return sid, inner, int(4 + l), nil
}
