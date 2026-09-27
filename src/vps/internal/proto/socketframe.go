package proto

import (
	"encoding/binary"
	"errors"
	"fmt"
)

// SocketFrameMaxBytes 是 unix socket 推送单帧字节上限（与 .team/vps-prep/01-proto-spec.md §5.1 一致）。
// 区别于业务帧 MaxFramePayloadSize（256 KiB）；socket 帧用于推送较短的 JSON 事件。
const SocketFrameMaxBytes = 64 * 1024

// EncodeSocketFrame 编码 length-prefixed 帧：[u32 length BE][payload...]
// 命名区分：frames.go 中的 EncodeFrame(t FrameType, payload) 是业务帧（与 P1 C# 协议层对齐）；
// 本函数是 unix socket 推送专用（length-prefix JSON），避免重名冲突。
func EncodeSocketFrame(payload []byte) ([]byte, error) {
	if uint32(len(payload)) > SocketFrameMaxBytes {
		return nil, fmt.Errorf("proto: socket payload %d exceeds SocketFrameMaxBytes: %w", len(payload), ErrFrameTooLarge)
	}
	frame := make([]byte, 4+len(payload))
	binary.BigEndian.PutUint32(frame, uint32(len(payload)))
	copy(frame[4:], payload)
	return frame, nil
}

// DecodeSocketFrame 从 src 解析一帧 socket 推送，返回 (payload, 消费字节数)。
// 命名区分：与 TryDecodeFrame 不同——socket 帧是严格 length-prefix（[u32][...]），
// 业务帧是 [u8 type][u16 len][...]。
func DecodeSocketFrame(src []byte) ([]byte, int, error) {
	if len(src) < 4 {
		return nil, 0, ErrTruncated
	}
	length := binary.BigEndian.Uint32(src[:4])
	if length > SocketFrameMaxBytes {
		return nil, 0, fmt.Errorf("proto: socket declared length %d exceeds SocketFrameMaxBytes: %w", length, ErrFrameTooLarge)
	}
	total := 4 + int(length)
	if uint32(len(src)) < uint32(total) {
		return nil, 0, ErrTruncated
	}
	payload := make([]byte, length)
	copy(payload, src[4:total])
	return payload, total, nil
}

// IsSocketFrameTruncated 报告 err 是否为 socket 帧 truncated 或包装了它。
func IsSocketFrameTruncated(err error) bool {
	return errors.Is(err, ErrTruncated)
}

// IsSocketFrameTooLarge 报告 err 是否为 socket 帧 too large 或包装了它。
func IsSocketFrameTooLarge(err error) bool {
	return errors.Is(err, ErrFrameTooLarge)
}
