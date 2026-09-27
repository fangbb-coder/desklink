package proto

import (
	"encoding/hex"
	"testing"
)

// TestCrossLanguageGoldenVectors 是 C# ↔ Go 字节级对齐的**唯一权威锚点**。
//
// 为什么需要这个文件：
//   - frames_test.go 里的往返测试只证明"自己编自己解能还原"，无法发现两端
//     用不同字节序却各自自洽的情形（C# 早先就是 little-endian，Go 是 big-endian，
//     两个仓库的测试都能过，但两端互相收发必然解析失败）。
//   - 这里写入两端必须共同产出的**固定字节串**，任一端改了编码就会立刻红。
//
// 对应的 C# 断言在同文件名的用例里（tests/Protocol.Tests/E2EAlignmentTests.cs）：
//   - FrameCodec_Hello_DeskLink_Matches_Go_Anchor
//   - FrameCodec_Ping_EmptyPayload_Matches_Go_RoundTrip
//   - MuxTcp_InputKey_CAFE_Sid33_Matches_Go_RoundTrip
//
// 改动任一侧编码时必须同步更新本文件的黄金串。
//
// 字节序约定（见 proto.go 编码总原则）：多字节整数统一 big-endian。
func TestCrossLanguageGoldenVectors(t *testing.T) {
	t.Run("frame_hello_desklink", func(t *testing.T) {
		// [u8 type=0x01][u16 len=8 BE][\"DeskLink\"]
		buf, err := EncodeFrame(FrameHello, []byte("DeskLink"))
		if err != nil {
			t.Fatal(err)
		}
		const want = "010008" + "4465736b4c696e6b"
		if got := hex.EncodeToString(buf); got != want {
			t.Fatalf("frame hello hex = %s, want %s", got, want)
		}
	})

	t.Run("frame_ping_empty", func(t *testing.T) {
		// [u8 type=0x04][u16 len=0 BE] → 仅 3 字节
		buf, err := EncodeFrame(FramePing, nil)
		if err != nil {
			t.Fatal(err)
		}
		const want = "040000"
		if got := hex.EncodeToString(buf); got != want {
			t.Fatalf("frame ping hex = %s, want %s", got, want)
		}
	})

	t.Run("mux_tcp_inputkey_cafe_sid33", func(t *testing.T) {
		// inner = [0x22][0x0002 BE][CA FE]
		// outer = [u32 len=6 BE][u8 sid=33=0x21][inner...]
		inner, err := EncodeFrame(FrameInputKey, []byte{0xCA, 0xFE})
		if err != nil {
			t.Fatal(err)
		}
		buf, err := EncodeMuxTcp(33, inner)
		if err != nil {
			t.Fatal(err)
		}
		// 注意 0x21 是 sid(33)，0x22 是 InputKey —— 两个值相邻，勿看串。
		const want = "00000006" + "21" + "220002cafe"
		if got := hex.EncodeToString(buf); got != want {
			t.Fatalf("mux tcp hex = %s, want %s", got, want)
		}
	})
}

// TestCrossLanguageGoldenVectors_RoundTrip 确保黄金串本身也能被本端解回原值，
// 避免"黄金串写错但恰好与实现同错"的假绿。
func TestCrossLanguageGoldenVectors_RoundTrip(t *testing.T) {
	hello, _ := hex.DecodeString("0100084465736b4c696e6b")
	ft, payload, consumed, err := TryDecodeFrame(hello)
	if err != nil || ft != FrameHello || string(payload) != "DeskLink" || consumed != len(hello) {
		t.Fatalf("hello golden decode: type=%v payload=%q consumed=%d err=%v", ft, payload, consumed, err)
	}

	mux, _ := hex.DecodeString("0000000621220002cafe")
	sid, inner, consumed, err := TryDecodeMuxTcp(mux)
	if err != nil || sid != 33 || consumed != len(mux) {
		t.Fatalf("mux golden decode: sid=%d consumed=%d err=%v", sid, consumed, err)
	}
	ft2, payload2, _, err := TryDecodeFrame(inner)
	if err != nil || ft2 != FrameInputKey || hex.EncodeToString(payload2) != "cafe" {
		t.Fatalf("mux golden inner decode: type=%v payload=%x err=%v", ft2, payload2, err)
	}
}

// goldenSeq 生成 start, start+1, ... 的 n 字节序列（固定测试输入）。
func goldenSeq(start byte, n int) []byte {
	out := make([]byte, n)
	for i := range out {
		out[i] = start + byte(i)
	}
	return out
}

// TestCrossLanguageGoldenVectors_RelayCtl 覆盖 P3/P4 新增的 **relay 明文控制层**。
//
// 对应 C# 断言（tests/Protocol.Tests/E2EAlignmentTests.cs）：
//   - RelayCtl_HelloFrame_Matches_Go_Anchor
//   - RelayCtl_StatusFrame_Matches_Go_Anchor
//   - RelayCtl_DialFrame_Matches_Go_Anchor
//
// 这一层跑在 QUIC/TLS 之上、SIGMA 之下，是 relay 用来查接线表的明文协议。
// 字节一旦漂移，客户端会在 Hello 阶段被 relayd 拒绝（更糟：静默错位后
// 把控制帧当业务密文，破坏 E2E 流）。
//
// 挑战签名 transcript（DeviceChallenge.Transcript）的黄金向量在同包的
// relay 测试里：src/vps/internal/relay/alignment_test.go
// （它依赖 relay.ChallengeTranscript，无法放进本包）。
//
// 固定输入（两侧一致）：deviceID 0x00..0x1F、hint 0x10..0x1F、
// nonce 0x20..0x3F、ed25519Pub 0x40..0x5F、signature 0x60..0x9F、
// challengeID 0x01020304、unix 1700000000。
func TestCrossLanguageGoldenVectors_RelayCtl(t *testing.T) {
	var id [DeviceIDSize]byte
	copy(id[:], goldenSeq(0x00, 32))
	var hint [16]byte
	copy(hint[:], goldenSeq(0x10, 16))
	nonce := goldenSeq(0x20, 32)
	sig := goldenSeq(0x60, 64)

	// Hello payload 布局：[u16 ver][32B deviceId][16B hint][u32 challengeId]
	//                     [i64 unix][32B nonce][u16 sigLen][64B sig]
	const wantHelloPayload = "0001" +
		"000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f" +
		"101112131415161718191a1b1c1d1e1f" +
		"01020304" + "000000006553f100" +
		"202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f" +
		"0040" +
		"606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f" +
		"808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f"

	t.Run("relay_ctl_hello_frame", func(t *testing.T) {
		payload, err := EncodeRelayHello(1, id, hint, 0x01020304, 1700000000, nonce, sig)
		if err != nil {
			t.Fatal(err)
		}
		if got := hex.EncodeToString(payload); got != wantHelloPayload {
			t.Fatalf("relay hello payload = %s, want %s", got, wantHelloPayload)
		}
		// 帧头：[u8 kind=0x01][u16 len BE = 0x00a0 = 160]
		frame, err := EncodeRelayCtl(KindRelayHello, payload)
		if err != nil {
			t.Fatal(err)
		}
		const wantFrame = "0100a0" + wantHelloPayload
		if got := hex.EncodeToString(frame); got != wantFrame {
			t.Fatalf("relay hello frame = %s, want %s", got, wantFrame)
		}
		// 往返：黄金串必须能被本端解回原值。
		ctl, consumed, err := TryDecodeRelayCtl(frame)
		if err != nil || ctl == nil || consumed != len(frame) || ctl.Kind != KindRelayHello {
			t.Fatalf("relay hello decode: kind=%v consumed=%d err=%v", ctl, consumed, err)
		}
		decoded, err := DecodeRelayHello(ctl.Payload)
		if err != nil {
			t.Fatal(err)
		}
		if decoded.DeviceID != id || decoded.ProtoVersion != 1 ||
			decoded.ChallengeID != 0x01020304 || decoded.ChallengeUnix != 1700000000 {
			t.Fatalf("relay hello round-trip mismatch: %+v", decoded)
		}
	})

	t.Run("relay_ctl_status_frame", func(t *testing.T) {
		// DialResult(ok,"ok")：[u8 kind=0x04][u16 len=5][u8 ok=1][u16 reasonLen=2]["ok"]
		status, err := EncodeRelayStatus(true, ReasonOK)
		if err != nil {
			t.Fatal(err)
		}
		frame, err := EncodeRelayCtl(KindRelayDialResult, status)
		if err != nil {
			t.Fatal(err)
		}
		const want = "0400050100026f6b"
		if got := hex.EncodeToString(frame); got != want {
			t.Fatalf("relay status frame = %s, want %s", got, want)
		}
	})

	t.Run("relay_ctl_dial_frame", func(t *testing.T) {
		// Dial(peer)：[u8 kind=0x03][u16 len=32][32B deviceId]
		frame, err := EncodeRelayCtl(KindRelayDial, id[:])
		if err != nil {
			t.Fatal(err)
		}
		const want = "030020" + "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"
		if got := hex.EncodeToString(frame); got != want {
			t.Fatalf("relay dial frame = %s, want %s", got, want)
		}
	})
}
