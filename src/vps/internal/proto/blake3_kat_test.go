package proto

import (
	"encoding/hex"
	"testing"

	"lukechampine.com/blake3"
)

// TestBlake3KnownVectors 验证 Go 端 lukechampine.com/blake3 与 BLAKE3 官方测试向量 bit-equal。
//
// 官方向量来源：https://github.com/BLAKE3-team/BLAKE3/blob/master/test_vectors/test_vectors.json
// 与 C# P1 Blake3.Managed 必须输出相同 hex（tests/Protocol.Tests/Blake3Tests.cs 使用同一组向量）。
//
// 官方向量的输入规则（不是"随机字节"，务必按规则构造）：
//   - input_len 0            → 空输入
//   - 其余 input_len         → 重复序列 0,1,2,...,250,0,1,... 截取前 input_len 字节
//   - 每项提供多组 output_len；这里只取 32 字节（与项目 Blake3.OutLen 一致）
//
// 这些向量作为设备派生（device_id / pair_id）的 KAT 兜底；任何 bit 偏差
// 都会导致 C# ↔ Go 互通失败。
func TestBlake3KnownVectors(t *testing.T) {
	cases := []struct {
		name    string
		input   []byte
		wantHex string
	}{
		{
			name:    "empty",
			input:   []byte{},
			wantHex: "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262",
		},
		{
			name:    "len3",
			input:   officialInput(3),
			wantHex: "e1be4d7a8ab5560aa4199eea339849ba8e293d55ca0a81006726d184519e647f",
		},
		{
			name:    "len64",
			input:   officialInput(64),
			wantHex: "4eed7141ea4a5cd4b788606bd23f46e212af9cacebacdc7d1f4c6dc7f2511b98",
		},
		{
			// 跨 chunk 边界（1024 = CHUNK_LEN）
			name:    "len1024",
			input:   officialInput(1024),
			wantHex: "42214739f095a406f3fc83deb889744ac00df831c10daa55189b5d121c855af7",
		},
		{
			// 跨 chunk 边界 +1（触发父节点合并）
			name:    "len1025",
			input:   officialInput(1025),
			wantHex: "d00278ae47eb27b34faecf67b4fe263f82d5412916c1ffd97c8cb7fb814b8444",
		},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			sum := blake3.Sum256(c.input)
			got := hex.EncodeToString(sum[:])
			if got != c.wantHex {
				t.Fatalf("BLAKE3 KAT mismatch\n  input len: %d\n  got hex:   %s\n  want hex:  %s",
					len(c.input), got, c.wantHex)
			}
		})
	}
}

// officialInput 按 BLAKE3 官方测试向量的输入规则构造输入：
// 重复序列 0,1,2,...,250,0,1,... 截取前 n 字节。
func officialInput(n int) []byte {
	buf := make([]byte, n)
	for i := 0; i < n; i++ {
		buf[i] = byte(i % 251)
	}
	return buf
}

// TestBlake3AbcVector 覆盖 BLAKE3 的 "abc" 常用向量。
//
// 注意：该向量不属于官方 test_vectors.json（官方用 0..250 重复序列），
// 但被广泛引用（BLAKE3 官网 / b3sum 文档 / 多个语言实现），
// 作为实现是否符合标准的快速交叉验证。
func TestBlake3AbcVector(t *testing.T) {
	const wantHex = "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85"
	sum := blake3.Sum256([]byte("abc"))
	got := hex.EncodeToString(sum[:])
	if got != wantHex {
		t.Fatalf("BLAKE3(\"abc\") mismatch\n  got:  %s\n  want: %s", got, wantHex)
	}
}

// TestBlake3DomainSeparatedDeviceID 验证 device_id 派生 = BLAKE3-256(domain || input)
// 的域分离行为与直接 BLAKE3 一致；这是 P1 C# 端必须 match 的关键路径。
//
// 断言的是"域分离确实生效且可复现"（Go 内部自洽），而非硬编码某个派生值：
// 硬编码派生值会随 domain 字符串演进失效，且无法暴露实现本身的错误。
func TestBlake3DomainSeparatedDeviceID(t *testing.T) {
	pubHex := "1111111111111111111111111111111111111111111111111111111111111111"
	pk, err := ParsePublicKeyHex(pubHex)
	if err != nil {
		t.Fatal(err)
	}
	id := pk.DeviceID()
	idHex := hex.EncodeToString(id[:])
	t.Logf("device_id hex for pubkey %s: %s", pubHex, idHex)

	// 1) DeviceID() 必须等于按 domain 前缀手工计算的 BLAKE3-256。
	//    注意：输入是 domain 的 ASCII 字节 || 公钥的**原始 32 字节**，
	//    不是 domain || 公钥的 hex 字符串（这正是本用例早先误报的根因）。
	buf := make([]byte, 0, len(domainDevice)+len(pk))
	buf = append(buf, domainDevice...)
	buf = append(buf, pk[:]...)
	wantSum := blake3.Sum256(buf)
	wantHex := hex.EncodeToString(wantSum[:])
	if idHex != wantHex {
		t.Fatalf("device_id domain separation mismatch\n  got:  %s\n  want: %s", idHex, wantHex)
	}

	// 2) 域分离必须生效：去掉 domain 前缀（仅哈希原始公钥字节）结果必须不同
	noDomain := blake3.Sum256(pk[:])
	if hex.EncodeToString(noDomain[:]) == idHex {
		t.Fatalf("domain separation ineffective: hashing pubkey without domain gave same device_id")
	}
}
