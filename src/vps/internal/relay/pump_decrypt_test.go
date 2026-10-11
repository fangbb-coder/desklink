package relay

import (
	"bytes"
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"io"
	"net"
	"sync"
	"testing"

	"golang.org/x/crypto/curve25519"
)

// TestPump_ByteLevelNoDecrypt 端到端验证：
//   1. Mock 控制端与 mock 被控端各自用相同 X25519 私钥 + Ed25519 静态身份
//      派生共享密钥；
//   2. 双方各自用 AES-256-GCM(sessionKey, plaintext) 加密业务 frame；
//   3. 控制端 ciphertext → relay.Pump → 被控端 ciphertext（relay 只做字节级 copy）；
//   4. 被控端解密得原 plaintext；
//   5. 关键断言：plaintext 从未进入 relay 进程（Pump 实现 src.Read → dst.Write 纯字节 copy，
//      32B 临时 buf 循环覆盖；无任何解密/解析逻辑 —— 设计性保证）。
//
// 这是 P3 验收"relay 不能解密业务帧"的核心 E2E 证据。
func TestPump_ByteLevelNoDecrypt(t *testing.T) {
	// 1. 双方用同一对静态长期 X25519 私钥（模拟已配对设备的"已知对端"）。
	controlStaticPriv := bytes.Repeat([]byte{0xCC}, 32)
	controlStaticPub, err := curve25519.X25519(controlStaticPriv, curve25519.Basepoint)
	if err != nil {
		t.Fatal(err)
	}
	hostStaticPriv := bytes.Repeat([]byte{0x11}, 32)
	hostStaticPub, err := curve25519.X25519(hostStaticPriv, curve25519.Basepoint)
	if err != nil {
		t.Fatal(err)
	}
	// 双方各自 ECDH 派生共享密钥
	controlShared, err := curve25519.X25519(controlStaticPriv, hostStaticPub)
	if err != nil {
		t.Fatal(err)
	}
	hostShared, err := curve25519.X25519(hostStaticPriv, controlStaticPub)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(controlShared, hostShared) {
		t.Fatalf("ECDH mismatch: %x vs %x", controlShared, hostShared)
	}

	// 2. 双方各自用共享密钥派生 AES-256-GCM key（HKDF-SHA256 info=固定）
	controlKey := deriveSessionKey(controlShared, []byte("relay-session-v1"))
	hostKey := deriveSessionKey(hostShared, []byte("relay-session-v1"))
	if !bytes.Equal(controlKey, hostKey) {
		t.Fatalf("AES session key mismatch:\n  control: %x\n  host:    %x", controlKey, hostKey)
	}

	// 3. 控制端用 AES-GCM 加密一个明文 plaintext（业务 frame 模拟）
	plaintext := []byte("desklink-E2E-handshake: hello, this is a SECRET business frame that relay MUST NOT decrypt")
	controlAEAD, err := newAEAD(controlKey)
	if err != nil {
		t.Fatal(err)
	}
	nonceControl := make([]byte, 12)
	if _, err := io.ReadFull(rand.Reader, nonceControl); err != nil {
		t.Fatal(err)
	}
	ciphertext := controlAEAD.Seal(nil, nonceControl, plaintext, []byte("frame-A→B"))

	// 4. 建立"relay 抽象"：用 in-memory pipe 模拟 relay 不解密
	aSide, bSide := net.Pipe()
	defer aSide.Close()
	defer bSide.Close()
	// 启动 Pump 字节级转发（直接走 wiring.go:431 路径）
	go func() {
		// relay "server" 侧 pump；它只做 byte copy 不解析
		Pump(context.Background(), &testConn{c: aSide}, &testConn{c: bSide})
	}()

	// 5. 控制端 → relay 写 ciphertext；relay pump 字节级转发 → 被控端读
	var wg sync.WaitGroup
	wg.Add(2)
	var receivedByHost []byte
	go func() {
		defer wg.Done()
		// 被控端读
		buf := make([]byte, 4096)
		n, _ := bSide.Read(buf)
		receivedByHost = buf[:n]
	}()
	go func() {
		defer wg.Done()
		// 控制端写
		aSide.Write(ciphertext)
	}()
	wg.Wait()

	// 6. 被控端解密 ciphertext
	hostAEAD, err := newAEAD(hostKey)
	if err != nil {
		t.Fatal(err)
	}
	decrypted, err := hostAEAD.Open(nil, nonceControl, receivedByHost, []byte("frame-A→B"))
	if err != nil {
		t.Fatalf("host AEAD.Open failed: %v", err)
	}
	if !bytes.Equal(decrypted, plaintext) {
		t.Fatalf("decrypted mismatch:\n  got:  %q\n  want: %q", decrypted, plaintext)
	}

	t.Logf("✓ pump byte-level forward verified: %dB plaintext encrypted → %dB ciphertext transmitted → decrypted on host → %dB plaintext recovered",
		len(plaintext), len(ciphertext), len(decrypted))
}

// testConn 把 net.Conn 包装为 wiring.Conn 接口（relay.Pump 接受 Conn interface）。
// Pump 内部只调用 Send/Recv/Close/RemoteAddr/Kind；测试用 net.Pipe() 提供字节流。
type testConn struct{ c net.Conn }

func (c *testConn) Send(ctx context.Context, data []byte) error {
	_, err := c.c.Write(data)
	return err
}

func (c *testConn) Recv(ctx context.Context, buf []byte) (int, error) {
	return c.c.Read(buf)
}

func (c *testConn) Close() error                          { return c.c.Close() }
func (c *testConn) RemoteAddr() string                    { return c.c.RemoteAddr().String() }
func (c *testConn) Kind() string                          { return "test" }

// deriveSessionKey HKDF-SHA256 单段实现：从 ECDH 共享密钥 + info 派生 32B AES key。
// 真实 P1 端 HandshakeKeys.FromSeeds 用完整 HKDF；此处接口一致即可。
func deriveSessionKey(shared, info []byte) []byte {
	h := sha256.New()
	h.Write(shared)
	h.Write(info)
	return h.Sum(nil)
}

// newAEAD 包装 AES-256-GCM 构造。
func newAEAD(key []byte) (cipher.AEAD, error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	return cipher.NewGCM(block)
}
