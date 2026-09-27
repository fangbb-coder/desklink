// 挑战签名 transcript 的跨语言黄金向量。
//
// 为什么单独放这里：`ChallengeTranscript` 属于 relay 包（relay 侧验签入口），
// 而 proto 包无法反向依赖 relay，所以这条向量不能并入
// src/vps/internal/proto/crosslang_golden_test.go（那里是 relay 控制帧的锚点）。
//
// 锚点：本文件的期望值与 C# 侧
//
//	tests/Protocol.Tests/E2EAlignmentTests.cs 的
//	DeviceChallenge_Transcript_Matches_Go_Anchor 用例**逐字节相同**。
//	relayd、registryd（regapi）与 C# 客户端三方都用同一个 transcript，
//	任何一方的字段顺序/字节序改动都会让三方验签失败。
//
// 固定输入（两侧一致）：
//
//	deviceID   = 0x00..0x1F (32B)
//	nonce      = 0x20..0x3F (32B)
//	ed25519Pub = 0x40..0x5F (32B)
//	challengeID = 0x01020304
//	unix        = 1700000000
package relay

import (
	"encoding/hex"
	"testing"

	"desklink/vps/internal/proto"
)

// TestDeviceChallengeTranscript_E2EAlignment 验证 transcript 字节与 C# 完全一致。
func TestDeviceChallengeTranscript_E2EAlignment(t *testing.T) {
	var id [proto.DeviceIDSize]byte
	copy(id[:], seqBytes(0x00, 32))
	nonce := seqBytes(0x20, 32)
	var pub proto.PublicKey
	copy(pub[:], seqBytes(0x40, 32))

	tr, err := ChallengeTranscript(0x01020304, 1700000000, nonce, id, pub)
	if err != nil {
		t.Fatal(err)
	}
	const want = "6465736b6c696e6b2f6465766963652d6368616c6c656e67652f7631" + // "desklink/device-challenge/v1"
		"01020304" + // challengeId (u32 BE)
		"000000006553f100" + // unixSeconds (i64 BE, 1700000000)
		"202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f" + // nonce
		"000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f" + // deviceID
		"404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f" // ed25519 pub
	if got := hex.EncodeToString(tr); got != want {
		t.Fatalf("challenge transcript mismatch:\n got=%s\nwant=%s", got, want)
	}

	// 域分隔前缀必须与 C# / registryd 一致（改它等于换协议版本）。
	if proto.ChallengeTranscriptDomain != "desklink/device-challenge/v1" {
		t.Fatalf("domain changed: %s", proto.ChallengeTranscriptDomain)
	}
}

// seqBytes 生成 start, start+1, ... 的 n 字节序列（固定测试输入）。
func seqBytes(start byte, n int) []byte {
	out := make([]byte, n)
	for i := range out {
		out[i] = start + byte(i)
	}
	return out
}
