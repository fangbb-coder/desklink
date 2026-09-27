package proto

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"testing"
)

func TestPublicKeyRoundTrip(t *testing.T) {
	raw := make([]byte, Ed25519PublicKeySize)
	if _, err := rand.Read(raw); err != nil {
		t.Fatal(err)
	}
	pk, err := ParsePublicKey(raw)
	if err != nil {
		t.Fatalf("ParsePublicKey: %v", err)
	}
	if !bytes.Equal(pk[:], raw) {
		t.Fatal("bytes mismatch after roundtrip")
	}
	if got := pk.Hex(); got != hex.EncodeToString(raw) {
		t.Fatalf("Hex mismatch: %s vs %s", got, hex.EncodeToString(raw))
	}
	if _, err := ParsePublicKeyHex(pk.Hex()); err != nil {
		t.Fatalf("ParsePublicKeyHex: %v", err)
	}
	if _, err := ParsePublicKeyHex("zz"); err == nil {
		t.Fatal("expected error on bad hex")
	}
}

func TestPublicKeyBadSize(t *testing.T) {
	_, errNil := ParsePublicKey(nil)
	if errNil == nil {
		t.Fatal("expected error on nil")
	}
	if !errors.Is(errNil, ErrBadPublicKeySize) {
		t.Fatalf("expected ErrBadPublicKeySize on nil, got %v", errNil)
	}
	if _, err := ParsePublicKey(make([]byte, 16)); err == nil {
		t.Fatal("expected error on short")
	}
	_, err16 := ParsePublicKey(make([]byte, 16))
	if !errors.Is(err16, ErrBadPublicKeySize) {
		t.Fatalf("expected ErrBadPublicKeySize, got %v", err16)
	}
	_, errLong := ParsePublicKey(make([]byte, 64))
	if !errors.Is(errLong, ErrBadPublicKeySize) {
		t.Fatalf("expected ErrBadPublicKeySize, got %v", errLong)
	}
}

func TestFingerprintDeterministic(t *testing.T) {
	raw := bytes.Repeat([]byte{0xAB}, Ed25519PublicKeySize)
	pk, _ := ParsePublicKey(raw)
	a := pk.Fingerprint()
	b := pk.Fingerprint()
	if a != b {
		t.Fatal("fingerprint not deterministic")
	}
	if pk.ShortFingerprint() != hex.EncodeToString(a[:16]) {
		t.Fatal("short fingerprint mismatch")
	}
}

func TestDeviceIDDeterministicAndDistinct(t *testing.T) {
	raw1 := bytes.Repeat([]byte{0x01}, Ed25519PublicKeySize)
	raw2 := bytes.Repeat([]byte{0x02}, Ed25519PublicKeySize)
	pk1, _ := ParsePublicKey(raw1)
	pk2, _ := ParsePublicKey(raw2)
	id1a := pk1.DeviceID()
	id1b := pk1.DeviceID()
	id2 := pk2.DeviceID()
	if id1a != id1b {
		t.Fatal("device_id not deterministic for same key")
	}
	if id1a == id2 {
		t.Fatal("distinct keys produced same device_id")
	}
}

func TestPairIDDistinctAcrossRedeem(t *testing.T) {
	// 模拟"同一对设备撤销→重配对"：用不同的 nonce 派生 pair_id 必须不同。
	da := bytes.Repeat([]byte{0x11}, DeviceIDSize)
	db := bytes.Repeat([]byte{0x22}, DeviceIDSize)
	var a, b [DeviceIDSize]byte
	copy(a[:], da)
	copy(b[:], db)
	n1, _ := NewPairServerNonce()
	n2, _ := NewPairServerNonce()
	p1 := DerivePairID(a, b, 1735000000000000000, n1)
	p2 := DerivePairID(a, b, 1735000000000000000, n2)
	if p1 == p2 {
		t.Fatal("distinct nonces must produce distinct pair_ids")
	}
}

func TestPairIDDistinctByNonceByte(t *testing.T) {
	var da, db [DeviceIDSize]byte
	for i := range da {
		da[i] = 0x11
	}
	for i := range db {
		db[i] = 0x22
	}
	n1 := PairServerNonce{}
	n2 := PairServerNonce{}
	n2[0] = 1
	p1 := DerivePairID(da, db, 1, n1)
	p2 := DerivePairID(da, db, 1, n2)
	if p1 == p2 {
		t.Fatal("nonce byte change must change pair_id")
	}
}

func TestEnsureOrder(t *testing.T) {
	a := [DeviceIDSize]byte{}
	b := [DeviceIDSize]byte{}
	b[0] = 1
	min, max := EnsureOrder(a, b)
	if min != a || max != b {
		t.Fatalf("EnsureOrder(a,b) failed when a<b")
	}
	min, max = EnsureOrder(b, a)
	if min != a || max != b {
		t.Fatalf("EnsureOrder(b,a) must swap")
	}
	if c := CompareDeviceID(a, b); c != -1 {
		t.Fatalf("CompareDeviceID(a,b) = %d, want -1", c)
	}
}

func TestPairingCodeParse(t *testing.T) {
	if _, err := ParsePairingCode("12345"); err == nil {
		t.Fatal("expected error on length 5")
	}
	if _, err := ParsePairingCode("12345a"); err == nil {
		t.Fatal("expected error on non-digit")
	}
	c, err := ParsePairingCode("123456")
	if err != nil {
		t.Fatalf("unexpected err: %v", err)
	}
	if c.String() != "123456" {
		t.Fatalf("got %s", c.String())
	}
	if !c.IsSentinel() == false {
		t.Fatal("123456 must not be sentinel")
	}
	sentinel, _ := ParsePairingCode("000000")
	if !sentinel.IsSentinel() {
		t.Fatal("000000 must be sentinel")
	}
}

func TestRandomPairingCodeNoSentinel(t *testing.T) {
	for i := 0; i < 100; i++ {
		c, err := RandomPairingCode(randBytes)
		if err != nil {
			t.Fatal(err)
		}
		if c.IsSentinel() {
			t.Fatalf("iteration %d: produced sentinel", i)
		}
		if _, err := ParsePairingCode(c.String()); err != nil {
			t.Fatalf("iteration %d: produced invalid code %q", i, c.String())
		}
	}
}

func randBytes(n int) ([]byte, error) { return randBytesImpl(n) }

// indirection to allow future deterministic test seeding if needed
var randBytesImpl = func(n int) ([]byte, error) {
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		return nil, err
	}
	return b, nil
}

func TestSocketFrameRoundTrip(t *testing.T) {
	payload := []byte(`{"v":1,"kind":"pair_added"}`)
	frame, err := EncodeSocketFrame(payload)
	if err != nil {
		t.Fatal(err)
	}
	if len(frame) != 4+len(payload) {
		t.Fatalf("frame length %d != %d", len(frame), 4+len(payload))
	}
	got, n, err := DecodeSocketFrame(frame)
	if err != nil {
		t.Fatal(err)
	}
	if n != len(frame) {
		t.Fatalf("consumed %d != %d", n, len(frame))
	}
	if !bytes.Equal(got, payload) {
		t.Fatalf("payload mismatch: %q vs %q", got, payload)
	}
}

func TestSocketFrameTooLarge(t *testing.T) {
	huge := make([]byte, SocketFrameMaxBytes+1)
	if _, err := EncodeSocketFrame(huge); !errors.Is(err, ErrFrameTooLarge) {
		t.Fatalf("EncodeSocketFrame huge: err=%v", err)
	}
	declared := make([]byte, 4)
	declared[0] = 0x00
	declared[1] = 0x01
	declared[2] = 0x00
	declared[3] = 0x01 // 0x00010001 = 65537, > SocketFrameMaxBytes=65536
	if _, _, err := DecodeSocketFrame(declared); !errors.Is(err, ErrFrameTooLarge) {
		t.Fatalf("DecodeSocketFrame huge: err=%v", err)
	}
}

func TestSocketFrameTruncated(t *testing.T) {
	if _, _, err := DecodeSocketFrame([]byte{0, 0, 0, 5, 'a'}); !errors.Is(err, ErrTruncated) {
		t.Fatalf("truncated: err=%v", err)
	}
	if _, _, err := DecodeSocketFrame([]byte{0, 0}); !errors.Is(err, ErrTruncated) {
		t.Fatalf("short header: err=%v", err)
	}
}
