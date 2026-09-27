// P5 FileAck 位图帧编解码（文件传输骨架）。
//
// 帧格式（与 C# 侧 FileAckFrame 对齐）：
//   [u64 baseChunk  BE]  // 此位图覆盖的首个 chunk 序号
//   [u32 bitCount   BE]  // 位图有多少个有效位
//   [bitmap bytes...]    // bitCount 个位，每字节 8 位，高位在前（MSB）
//
// 语义：
//   - bit i == 1  表示 chunk (baseChunk + i) 已被对端成功接收。
//   - bit i == 0  表示未确认（可能丢失、可能尚未发送）。
//   - 发送方收到 ack 后从发送窗口中移除对应 chunk，重传窗口只保留 0 位。
//   - 单个 ack 帧最多覆盖 65535*8 = 524280 个 chunk（约 512K chunks），
//     对 64KB 分块来说覆盖 32GB 文件；超大文件需要多个 ack 帧（baseChunk 递进）。

package proto

import (
	"encoding/binary"
	"errors"
	"fmt"
)

// FileAckFrame 文件 chunk 确认位图帧。
type FileAckFrame struct {
	BaseChunk uint64
	BitCount  uint32
	Bitmap    []byte
}

// FileAckHeaderSize 帧头固定 12 字节：u64 + u32。
const FileAckHeaderSize = 8 + 4

// EncodeFileAck 序列化为字节（不含内层帧头——由 EncodeFrame 负责包 [type][u16 len]）。
func EncodeFileAck(f *FileAckFrame) ([]byte, error) {
	if f.BitCount == 0 && len(f.Bitmap) != 0 {
		return nil, errors.New("fileack: BitCount=0 but Bitmap non-empty")
	}
	expected := int((f.BitCount + 7) / 8)
	if len(f.Bitmap) != expected {
		return nil, fmt.Errorf("fileack: bitmap length %d != expected %d for BitCount=%d",
			len(f.Bitmap), expected, f.BitCount)
	}
	buf := make([]byte, FileAckHeaderSize+len(f.Bitmap))
	binary.BigEndian.PutUint64(buf[0:8], f.BaseChunk)
	binary.BigEndian.PutUint32(buf[8:12], f.BitCount)
	copy(buf[FileAckHeaderSize:], f.Bitmap)
	return buf, nil
}

// DecodeFileAck 从 payload 解码。调用方应先用 TryDecodeFrame 剥掉内层帧头。
func DecodeFileAck(payload []byte) (*FileAckFrame, error) {
	if len(payload) < FileAckHeaderSize {
		return nil, fmt.Errorf("fileack: payload too short (%d < %d)", len(payload), FileAckHeaderSize)
	}
	baseChunk := binary.BigEndian.Uint64(payload[0:8])
	bitCount := binary.BigEndian.Uint32(payload[8:12])
	bitmap := payload[FileAckHeaderSize:]

	expected := int((bitCount + 7) / 8)
	if len(bitmap) != expected {
		return nil, fmt.Errorf("fileack: bitmap length %d != expected %d for BitCount=%d",
			len(bitmap), expected, bitCount)
	}

	return &FileAckFrame{BaseChunk: baseChunk, BitCount: bitCount, Bitmap: bitmap}, nil
}

// IsAcked 查询 chunk idx 是否被 ack（idx 必须是 BaseChunk 之后的偏移）。
func (f *FileAckFrame) IsAcked(chunkIdx uint64) bool {
	if chunkIdx < f.BaseChunk {
		return false
	}
	off := chunkIdx - f.BaseChunk
	if off >= uint64(f.BitCount) {
		return false
	}
	byteIdx := off / 8
	bitIdx := 7 - int(off%8) // MSB 在前
	return (f.Bitmap[byteIdx] & (1 << bitIdx)) != 0
}

// PackBitmap 把 []bool 打包成位图字节（MSB 在前）。
func PackBitmap(bits []bool) []byte {
	if len(bits) == 0 {
		return nil
	}
	bytes := make([]byte, (len(bits)+7)/8)
	for i, b := range bits {
		if b {
			bytes[i/8] |= 1 << (7 - (i % 8))
		}
	}
	return bytes
}

// UnpackBitmap 把位图字节解包成 []bool（MSB 在前）。
func UnpackBitmap(bitmap []byte, bitCount uint32) []bool {
	bits := make([]bool, bitCount)
	for i := uint32(0); i < bitCount; i++ {
		byteIdx := i / 8
		bitIdx := 7 - int(i%8)
		bits[i] = (bitmap[byteIdx] & (1 << bitIdx)) != 0
	}
	return bits
}
