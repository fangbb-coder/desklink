// P5 FileAck 位图帧编解码测试。

package proto

import (
	"bytes"
	"testing"
)

func TestFileAck_RoundTrip(t *testing.T) {
	f := &FileAckFrame{
		BaseChunk: 42,
		BitCount:  16,
		Bitmap:    []byte{0b10101010, 0b01010101},
	}
	encoded, err := EncodeFileAck(f)
	if err != nil {
		t.Fatalf("encode: %v", err)
	}
	dec, err := DecodeFileAck(encoded)
	if err != nil {
		t.Fatalf("decode: %v", err)
	}
	if dec.BaseChunk != 42 || dec.BitCount != 16 || !bytes.Equal(dec.Bitmap, f.Bitmap) {
		t.Fatalf("mismatch: %+v", dec)
	}
}

func TestFileAck_IsAcked(t *testing.T) {
	f := &FileAckFrame{BaseChunk: 100, BitCount: 8, Bitmap: []byte{0b10000000}}
	if f.IsAcked(99) {
		t.Error("before base should be false")
	}
	if !f.IsAcked(100) {
		t.Error("bit 0 (MSB) should be true")
	}
	if f.IsAcked(101) {
		t.Error("bit 1 should be false")
	}
	if f.IsAcked(108) {
		t.Error("after range should be false")
	}
}

func TestFileAck_PackUnpack(t *testing.T) {
	bits := []bool{true, false, true, false, true, true, false, true, true}
	packed := PackBitmap(bits)
	if len(packed) != 2 {
		t.Fatalf("expected 2 bytes, got %d", len(packed))
	}
	unpacked := UnpackBitmap(packed, uint32(len(bits)))
	for i, want := range bits {
		if unpacked[i] != want {
			t.Fatalf("bit %d: got %v, want %v", i, unpacked[i], want)
		}
	}
}

func TestFileAck_DecodePayloadTooShort(t *testing.T) {
	_, err := DecodeFileAck(make([]byte, 10))
	if err == nil {
		t.Fatal("expected error for short payload")
	}
}

func TestFileAck_DecodeBitmapLengthMismatch(t *testing.T) {
	bad := make([]byte, 13) // header + 1 byte, but bitCount says 9 (needs 2 bytes)
	bad[8], bad[9], bad[10], bad[11] = 0, 0, 0, 9
	_, err := DecodeFileAck(bad)
	if err == nil {
		t.Fatal("expected error for bitmap length mismatch")
	}
}
