package proto

import (
	"bytes"
	"encoding/hex"
	"errors"
	"testing"
)

// 与 C# 侧 ProtocolConstants 必须一致的关键常量。
// 任何字段变更需同步更新 src/Protocol/DeskLink.Protocol/Common/ProtocolConstants.cs。
func TestConstants_Alignment(t *testing.T) {
	if FrameProtocolVersion != 1 {
		t.Fatalf("FrameProtocolVersion = %d, want 1", FrameProtocolVersion)
	}
	if FrameMagicNumber != 0x444C4E4B {
		t.Fatalf("FrameMagicNumber = 0x%X, want 0x444C4E4B", FrameMagicNumber)
	}
	if MaxFramePayloadSize != 256*1024 {
		t.Fatalf("MaxFramePayloadSize = %d, want %d", MaxFramePayloadSize, 256*1024)
	}
	if MaxStreamId != 255 {
		t.Fatalf("MaxStreamId = %d, want 255", MaxStreamId)
	}
	if ReservedStreamId != 0 {
		t.Fatalf("ReservedStreamId = %d, want 0", ReservedStreamId)
	}
	if FrameHello != 0x01 || FramePing != 0x04 || FrameDesktopVideo != 0x10 ||
		FrameInputKey != 0x22 || FrameFileComplete != 0x38 {
		t.Fatalf("frame type constants diverged from C# spec")
	}
	if StreamDesktop != 0x10 || StreamControl != 0x20 || StreamFile != 0x30 {
		t.Fatalf("logical stream constants diverged from C# spec")
	}
	if PathRelay != 0x01 || PathDirect != 0x02 {
		t.Fatalf("path kind constants diverged from C# spec")
	}
}

func TestStreamIdFromLogical(t *testing.T) {
	cases := []struct {
		kind    LogicalStream
		sub     byte
		wantSid byte
		wantErr bool
	}{
		{StreamDesktop, 0, 16, false},
		{StreamControl, 0, 32, false},
		{StreamFile, 0, 48, false},
		{StreamFile, 32, 80, false},
		{StreamFile, 250, 0, true}, // 48 + 250 > 255
		{LogicalStream(0x40), 0, 0, true},
	}
	for _, tc := range cases {
		sid, err := StreamIdFromLogical(tc.kind, tc.sub)
		if (err != nil) != tc.wantErr {
			t.Fatalf("StreamIdFromLogical(%d,%d) err=%v wantErr=%v", tc.kind, tc.sub, err, tc.wantErr)
		}
		if !tc.wantErr && sid != tc.wantSid {
			t.Fatalf("StreamIdFromLogical(%d,%d)=%d want %d", tc.kind, tc.sub, sid, tc.wantSid)
		}
	}
}

func TestEncodeDecodeFrame_RoundTrip(t *testing.T) {
	payload := []byte{0xDE, 0xAD, 0xBE, 0xEF}
	buf, err := EncodeFrame(FrameInputKey, payload)
	if err != nil {
		t.Fatal(err)
	}
	t1, p1, consumed, err := TryDecodeFrame(buf)
	if err != nil || t1 != FrameInputKey || !bytes.Equal(p1, payload) {
		t.Fatalf("TryDecodeFrame got type=%v payload=%x err=%v", t1, p1, err)
	}
	if consumed != len(buf) {
		t.Fatalf("consumed=%d want %d", consumed, len(buf))
	}
}

func TestEncodeFrame_Oversize(t *testing.T) {
	big := make([]byte, MaxFramePayloadSize+1)
	if _, err := EncodeFrame(FrameFileChunk, big); err == nil {
		t.Fatal("expected error for oversize payload")
	}
}

// TestEncodeFrame_Uint16WireLimit 锁定"帧长度字段是 u16"这一硬约束。
//
// 回归的事故：EncodeFrame 早期只校验 MaxFramePayloadSize(256KB)，而长度字段写入用
// uint16(len(payload))。于是 65536..262144 的负载会被**静默截断**长度字段，
// payload 字节却全量写出——接收端按错误的 len 切片，整条流从此错位且不报错。
// 实测 70000B 编码成 len=4464，接收端只消费 4467B，剩余 65536B 全部错位。
func TestEncodeFrame_Uint16WireLimit(t *testing.T) {
	// 1) 超限必须报错（且是可判定的 sentinel），绝不静默截断。
	for _, n := range []int{MaxFramePayloadWireSize + 1, 70000, MaxFramePayloadSize} {
		if _, err := EncodeFrame(FrameFileChunk, make([]byte, n)); err == nil {
			t.Fatalf("EncodeFrame(%d bytes) must fail: u16 length field cannot represent it", n)
		} else if !errors.Is(err, ErrFramePayloadTooLarge) {
			t.Fatalf("EncodeFrame(%d bytes) error = %v, want ErrFramePayloadTooLarge", n, err)
		}
	}

	// 2) 边界值 65535 必须仍可编码且能完整往返（一个字节都不能错位）。
	max := make([]byte, MaxFramePayloadWireSize)
	for i := range max {
		max[i] = byte(i)
	}
	buf, err := EncodeFrame(FrameFileChunk, max)
	if err != nil {
		t.Fatalf("EncodeFrame(65535 bytes) must succeed: %v", err)
	}
	if len(buf) != FrameHeaderSize+MaxFramePayloadWireSize {
		t.Fatalf("encoded len = %d, want %d", len(buf), FrameHeaderSize+MaxFramePayloadWireSize)
	}
	ft, payload, consumed, err := TryDecodeFrame(buf)
	if err != nil {
		t.Fatal(err)
	}
	if ft != FrameFileChunk || len(payload) != MaxFramePayloadWireSize || consumed != len(buf) {
		t.Fatalf("round-trip: type=%v len=%d consumed=%d (want %v/%d/%d)",
			ft, len(payload), consumed, FrameFileChunk, MaxFramePayloadWireSize, len(buf))
	}
	if !bytes.Equal(payload, max) {
		t.Fatal("round-trip payload bytes differ")
	}
}

func TestTryDecodeFrame_ShortBuffer(t *testing.T) {
	t1, _, consumed, err := TryDecodeFrame([]byte{0x20})
	if err != nil || t1 != 0 || consumed != 0 {
		t.Fatalf("expected nil return for short buffer, got t=%v consumed=%d err=%v", t1, consumed, err)
	}
}

func TestEncodeDecodeMuxTcp_RoundTrip(t *testing.T) {
	inner, _ := EncodeFrame(FrameInputKey, []byte{0xCA, 0xFE})
	sid := byte(33)
	buf, err := EncodeMuxTcp(sid, inner)
	if err != nil {
		t.Fatal(err)
	}
	gotSid, gotInner, consumed, err := TryDecodeMuxTcp(buf)
	if err != nil {
		t.Fatal(err)
	}
	if gotSid != sid {
		t.Fatalf("sid got=%d want=%d", gotSid, sid)
	}
	if !bytes.Equal(gotInner, inner) {
		t.Fatalf("inner mismatch")
	}
	if consumed != len(buf) {
		t.Fatalf("consumed=%d want %d", consumed, len(buf))
	}
}

func TestTryDecodeMuxTcp_ShortBuffer(t *testing.T) {
	if _, _, _, err := TryDecodeMuxTcp([]byte{0, 0, 0}); err != nil {
		t.Fatalf("short buffer should return nil err, got %v", err)
	}
}

// 人工对齐用：C# 与 Go 编同一帧应当得到相同字节。
func TestHexFrame_E2EAlignment(t *testing.T) {
	// Hello(0x01) + payload="DeskLink" (8 bytes) → 01 00 08 44 65 73 6B 4C 69 6E 6B
	buf, err := EncodeFrame(FrameHello, []byte("DeskLink"))
	if err != nil {
		t.Fatal(err)
	}
	want := "010008" + hex.EncodeToString([]byte("DeskLink"))
	if got := hex.EncodeToString(buf); got != want {
		t.Fatalf("frame hex=%s want=%s", got, want)
	}
}
