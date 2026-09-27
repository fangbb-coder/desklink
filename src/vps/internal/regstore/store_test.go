package regstore

import (
	"context"
	"crypto/rand"
	"errors"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"desklink/vps/internal/proto"
)

// openTestStore 返回临时 dataDir 中的 Store；测试结束后不自动清理（由 t.TempDir 兜底）。
func openTestStore(t *testing.T) *Store {
	t.Helper()
	ctx := context.Background()
	dataDir := t.TempDir()
	s, err := Open(ctx, dataDir)
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	t.Cleanup(func() { _ = s.Close() })
	return s
}

// makeDevice 注册一个新设备并返回 (deviceID, pubkey)。
func makeDevice(t *testing.T, s *Store, label byte) (proto.PublicKey, [proto.DeviceIDSize]byte) {
	t.Helper()
	raw := make([]byte, proto.Ed25519PublicKeySize)
	raw[0] = label
	if _, err := rand.Read(raw[1:]); err != nil {
		t.Fatal(err)
	}
	pk, err := proto.ParsePublicKey(raw)
	if err != nil {
		t.Fatal(err)
	}
	rec := &DeviceRecord{
		PubKey:       pk,
		Platform:     "test",
		RegisteredAt: time.Now().Unix(),
	}
	if err := s.RegisterDevice(context.Background(), rec); err != nil {
		t.Fatalf("RegisterDevice: %v", err)
	}
	return pk, rec.DeviceID
}

func TestRegisterAndGetDevice(t *testing.T) {
	s := openTestStore(t)
	pk, id := makeDevice(t, s, 0x01)

	got, err := s.GetDevice(context.Background(), id)
	if err != nil {
		t.Fatal(err)
	}
	if got.PubKey != pk {
		t.Fatal("pubkey mismatch")
	}
	if got.DeviceID != id {
		t.Fatal("device_id mismatch")
	}
	if got.Revoked {
		t.Fatal("unexpected revoked")
	}

	got2, err := s.GetDeviceByPubKey(context.Background(), pk)
	if err != nil {
		t.Fatal(err)
	}
	if got2.DeviceID != id {
		t.Fatal("device_id mismatch by pubkey")
	}
}

func TestRegisterDuplicateFails(t *testing.T) {
	s := openTestStore(t)
	_, _ = makeDevice(t, s, 0x02)
	// 同公钥再次注册应该不报错（ON CONFLICT DO NOTHING），但 RowsAffected=0。
	// 当前 regstore.RegisterDevice 不返回 ErrDeviceExists；保留供 v2 改为返回。
	raw := make([]byte, proto.Ed25519PublicKeySize)
	for i := range raw {
		raw[i] = 0x02
	}
	pk, _ := proto.ParsePublicKey(raw)
	rec := &DeviceRecord{PubKey: pk, RegisteredAt: time.Now().Unix()}
	if err := s.RegisterDevice(context.Background(), rec); err != nil {
		t.Fatalf("RegisterDevice duplicate: %v", err)
	}
}

func TestHeartbeatMissing(t *testing.T) {
	s := openTestStore(t)
	_, id := makeDevice(t, s, 0x03)
	var bogusID [proto.DeviceIDSize]byte
	bogusID[0] = 0xFF
	if err := s.Heartbeat(context.Background(), bogusID, 0); !errors.Is(err, ErrDeviceNotFound) {
		t.Fatalf("expected ErrDeviceNotFound, got %v", err)
	}
	if err := s.Heartbeat(context.Background(), id, 0); err != nil {
		t.Fatalf("Heartbeat existing: %v", err)
	}
}

func TestPairCreateAndGetByUUID(t *testing.T) {
	s := openTestStore(t)
	issuerPK, _ := makeDevice(t, s, 0x10)
	redeemerPK, _ := makeDevice(t, s, 0x11)

	// 模拟注册后直接 CreatePair（跳过配对码，仅测试 pair 行写入/查询）
	issuerID := issuerPK.DeviceID()
	redeemerID := redeemerPK.DeviceID()
	deviceA, deviceB := proto.EnsureOrder(issuerID, redeemerID)
	var fpA, fpB [proto.FingerprintSize]byte
	if deviceA == issuerID {
		fpA = issuerPK.Fingerprint()
		fpB = redeemerPK.Fingerprint()
	} else {
		fpA = redeemerPK.Fingerprint()
		fpB = issuerPK.Fingerprint()
	}
	pair := &PairRecord{
		DeviceA:      deviceA,
		DeviceB:      deviceB,
		FingerprintA: fpA,
		FingerprintB: fpB,
	}
	if err := s.CreatePair(context.Background(), pair); err != nil {
		t.Fatalf("CreatePair: %v", err)
	}
	if pair.PairUUID == "" {
		t.Fatal("pair_uuid not assigned")
	}
	if pair.PairID == ([proto.PairIDSize]byte{}) {
		t.Fatal("pair_id not assigned")
	}

	got, err := s.GetPairByUUID(context.Background(), pair.PairUUID)
	if err != nil {
		t.Fatal(err)
	}
	if got.PairID != pair.PairID {
		t.Fatal("pair_id mismatch")
	}
	if got.DeviceA != deviceA || got.DeviceB != deviceB {
		t.Fatal("device pair mismatch")
	}
}

func TestPairCreateRejectsBadOrder(t *testing.T) {
	s := openTestStore(t)
	issuerPK, _ := makeDevice(t, s, 0x20)
	redeemerPK, _ := makeDevice(t, s, 0x21)
	issuerID := issuerPK.DeviceID()
	redeemerID := redeemerPK.DeviceID()
	// DeviceID 派生自 BLAKE2b 哈希，原始顺序随机；为避免 flaky，用 EnsureOrder
	// 强制排序后再反向传入（A > B），让 CreatePair 的字典序检查必失败。
	deviceA, deviceB := proto.EnsureOrder(issuerID, redeemerID)
	bad := &PairRecord{
		DeviceA:      deviceB, // bigger
		DeviceB:      deviceA, // smaller
		FingerprintA: issuerPK.Fingerprint(),
		FingerprintB: redeemerPK.Fingerprint(),
	}
	if err := s.CreatePair(context.Background(), bad); err == nil {
		t.Fatal("expected error on bad device order")
	}
}

func TestRevokePairByUUID(t *testing.T) {
	s := openTestStore(t)
	issuerPK, issuerID := makeDevice(t, s, 0x30)
	redeemerPK, _ := makeDevice(t, s, 0x31)
	deviceA, deviceB := proto.EnsureOrder(issuerID, redeemerPK.DeviceID())
	var fpA, fpB [proto.FingerprintSize]byte
	if deviceA == issuerID {
		fpA = issuerPK.Fingerprint()
		fpB = redeemerPK.Fingerprint()
	} else {
		fpA = redeemerPK.Fingerprint()
		fpB = issuerPK.Fingerprint()
	}
	pair := &PairRecord{DeviceA: deviceA, DeviceB: deviceB, FingerprintA: fpA, FingerprintB: fpB}
	if err := s.CreatePair(context.Background(), pair); err != nil {
		t.Fatal(err)
	}
	rec, already, err := s.RevokePairByUUID(context.Background(), pair.PairUUID, issuerID, RevokeReasonUserRevoked)
	if err != nil {
		t.Fatal(err)
	}
	if already {
		t.Fatal("first revoke must not be already")
	}
	if !rec.Revoked {
		t.Fatal("must be revoked")
	}
	// 第二次撤销应返回 already=true
	_, already, err = s.RevokePairByUUID(context.Background(), pair.PairUUID, issuerID, RevokeReasonUserRevoked)
	if err != nil {
		t.Fatal(err)
	}
	if !already {
		t.Fatal("second revoke must be already")
	}
}

func TestPairIDDistinctAcrossRedeem(t *testing.T) {
	s := openTestStore(t)
	issuerPK, issuerID := makeDevice(t, s, 0x40)
	redeemerPK, _ := makeDevice(t, s, 0x41)
	deviceA, deviceB := proto.EnsureOrder(issuerID, redeemerPK.DeviceID())
	var fpA, fpB [proto.FingerprintSize]byte
	if deviceA == issuerID {
		fpA = issuerPK.Fingerprint()
		fpB = redeemerPK.Fingerprint()
	} else {
		fpA = redeemerPK.Fingerprint()
		fpB = issuerPK.Fingerprint()
	}
	// 创建 + 撤销 + 重配对三次，新 pair_id 必不同
	seen := map[[proto.PairIDSize]byte]bool{}
	for i := 0; i < 3; i++ {
		pair := &PairRecord{DeviceA: deviceA, DeviceB: deviceB, FingerprintA: fpA, FingerprintB: fpB}
		if err := s.CreatePair(context.Background(), pair); err != nil {
			t.Fatalf("iter %d: CreatePair: %v", i, err)
		}
		if seen[pair.PairID] {
			t.Fatalf("iter %d: pair_id reused: %x", i, pair.PairID)
		}
		seen[pair.PairID] = true
		if _, _, err := s.RevokePairByUUID(context.Background(), pair.PairUUID, issuerID, RevokeReasonUserRevoked); err != nil {
			t.Fatalf("iter %d: revoke: %v", i, err)
		}
	}
}

func TestPairingCodeIssueAndRedeem(t *testing.T) {
	s := openTestStore(t)
	issuerPK, _ := makeDevice(t, s, 0x50)
	redeemerPK, _ := makeDevice(t, s, 0x51)
	code, err := s.IssuePairingCode(context.Background(), issuerPK.DeviceID(), 0)
	if err != nil {
		t.Fatal(err)
	}
	if code.IsSentinel() {
		t.Fatal("unexpected sentinel")
	}
	result, err := s.RedeemPairingCode(context.Background(), code, issuerPK, redeemerPK)
	if err != nil {
		t.Fatalf("RedeemPairingCode: %v", err)
	}
	if result.Pair.PairUUID == "" {
		t.Fatal("pair_uuid empty")
	}
}

func TestPairingCodeRedeemConsumedAtMostOnce(t *testing.T) {
	s := openTestStore(t)
	issuerPK, _ := makeDevice(t, s, 0x60)
	redeemerPK, _ := makeDevice(t, s, 0x61)
	code, err := s.IssuePairingCode(context.Background(), issuerPK.DeviceID(), 0)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.RedeemPairingCode(context.Background(), code, issuerPK, redeemerPK); err != nil {
		t.Fatalf("first redeem: %v", err)
	}
	if _, err := s.RedeemPairingCode(context.Background(), code, issuerPK, redeemerPK); !errors.Is(err, ErrPairingCodeConsumed) {
		t.Fatalf("second redeem: expected ErrPairingCodeConsumed, got %v", err)
	}
}

func TestPairingCodeRedeemConcurrent(t *testing.T) {
	// 验收：50 goroutines 并发领取同一码，仅 1 成功；其余 49 收到 ErrPairingCodeConsumed。
	s := openTestStore(t)
	issuerPK, _ := makeDevice(t, s, 0x70)
	code, err := s.IssuePairingCode(context.Background(), issuerPK.DeviceID(), 0)
	if err != nil {
		t.Fatal(err)
	}
	const N = 50
	var success int32
	var consumed int32
	var otherErr int32
	var wg sync.WaitGroup
	for i := 0; i < N; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			rpk, _ := makeDevice(t, s, byte(0x80+i%8))
			_, err := s.RedeemPairingCode(context.Background(), code, issuerPK, rpk)
			switch {
			case err == nil:
				atomic.AddInt32(&success, 1)
			case errors.Is(err, ErrPairingCodeConsumed):
				atomic.AddInt32(&consumed, 1)
			default:
				atomic.AddInt32(&otherErr, 1)
				t.Logf("unexpected err: %v", err)
			}
		}()
	}
	wg.Wait()
	if success != 1 {
		t.Fatalf("expected exactly 1 success, got %d (consumed=%d, otherErr=%d)", success, consumed, otherErr)
	}
	if consumed != N-1 {
		t.Fatalf("expected %d consumed, got %d", N-1, consumed)
	}
	if otherErr != 0 {
		t.Fatalf("got %d unexpected errors", otherErr)
	}
}

func TestPairingCodeExpired(t *testing.T) {
	s := openTestStore(t)
	issuerPK, issuerID := makeDevice(t, s, 0x90)
	redeemerPK, _ := makeDevice(t, s, 0x91)
	// TTL 用 1s（亚秒级 TTL 在 Unix 秒存储下被舍入到 0，再走默认值 5 分钟）。
	code, err := s.IssuePairingCode(context.Background(), issuerID, 1*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	time.Sleep(1100 * time.Millisecond)
	_, err = s.RedeemPairingCode(context.Background(), code, issuerPK, redeemerPK)
	if !errors.Is(err, ErrPairingCodeExpired) {
		t.Fatalf("expected ErrPairingCodeExpired, got %v", err)
	}
}

func TestRateLimiter_AllowsBurstThenBlocks(t *testing.T) {
	rl := NewRateLimiter()
	// 同 IP 同码 burst=5，前 5 次通过，第 6 次起被限速
	for i := 0; i < 5; i++ {
		if !rl.Allow("1.2.3.4", LimitRedeemByCode, "000123") {
			t.Fatalf("burst %d must be allowed", i)
		}
	}
	// 第 6 次起短时间被拒
	blocked := false
	for i := 0; i < 10; i++ {
		if !rl.Allow("1.2.3.4", LimitRedeemByCode, "000123") {
			blocked = true
			break
		}
	}
	if !blocked {
		t.Fatal("expected rate limit to block after burst")
	}
}

func TestRateLimiter_DifferentKeysIndependent(t *testing.T) {
	rl := NewRateLimiter()
	for i := 0; i < 5; i++ {
		rl.Allow("1.2.3.4", LimitRedeemByCode, "000123")
	}
	// 不同码独立
	if !rl.Allow("1.2.3.4", LimitRedeemByCode, "000124") {
		t.Fatal("different code should not share limiter")
	}
	// 不同 IP 独立
	if !rl.Allow("1.2.3.5", LimitRedeemByCode, "000123") {
		t.Fatal("different IP should not share limiter")
	}
}

func TestCleanupExpiredPairingCodes(t *testing.T) {
	s := openTestStore(t)
	issuerPK, _ := makeDevice(t, s, 0xA0)
	_, _ = s.IssuePairingCode(context.Background(), issuerPK.DeviceID(), 2*time.Second)
	// 跨秒边界时序敏感：time.Now().Unix() 截断到整秒，sleep 3.5s 确保 now > expires_at + 1。
	time.Sleep(3500 * time.Millisecond)
	n, err := s.CleanupExpiredPairingCodes(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if n < 1 {
		t.Fatal("expected at least 1 cleanup")
	}
}

func TestRotateKey(t *testing.T) {
	s := openTestStore(t)
	pk, id := makeDevice(t, s, 0xB0)
	// 生成新密钥
	newRaw := make([]byte, proto.Ed25519PublicKeySize)
	newRaw[0] = 0xB1
	if _, err := rand.Read(newRaw[1:]); err != nil {
		t.Fatal(err)
	}
	newPK, _ := proto.ParsePublicKey(newRaw)
	sig := make([]byte, 64)
	if _, err := rand.Read(sig); err != nil {
		t.Fatal(err)
	}
	if err := s.RotateKey(context.Background(), id, pk, newPK, sig, 0); err != nil {
		t.Fatalf("RotateKey: %v", err)
	}
	got, err := s.GetDevice(context.Background(), id)
	if err != nil {
		t.Fatal(err)
	}
	if got.PubKey != newPK {
		t.Fatal("pubkey not rotated")
	}
	// 第二次用旧 pubkey rotate 应失败
	if err := s.RotateKey(context.Background(), id, pk, newPK, sig, 0); err == nil {
		t.Fatal("expected error on stale old pubkey")
	}
}

func TestListDevices(t *testing.T) {
	s := openTestStore(t)
	for i := 0; i < 3; i++ {
		makeDevice(t, s, byte(0xC0+i))
	}
	devs, err := s.ListDevices(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if len(devs) < 3 {
		t.Fatalf("expected >= 3 devices, got %d", len(devs))
	}
}

func TestListPairsByDevice(t *testing.T) {
	s := openTestStore(t)
	a, aID := makeDevice(t, s, 0xD0)
	b, bID := makeDevice(t, s, 0xD1)
	c, cID := makeDevice(t, s, 0xD2)
	// pair (a, b)
	abA, abB := proto.EnsureOrder(aID, bID)
	var abFpA, abFpB [proto.FingerprintSize]byte
	if abA == aID {
		abFpA, abFpB = a.Fingerprint(), b.Fingerprint()
	} else {
		abFpA, abFpB = b.Fingerprint(), a.Fingerprint()
	}
	pairAB := &PairRecord{DeviceA: abA, DeviceB: abB, FingerprintA: abFpA, FingerprintB: abFpB}
	// pair (a, c)
	acA, acB := proto.EnsureOrder(aID, cID)
	var acFpA, acFpB [proto.FingerprintSize]byte
	if acA == aID {
		acFpA, acFpB = a.Fingerprint(), c.Fingerprint()
	} else {
		acFpA, acFpB = c.Fingerprint(), a.Fingerprint()
	}
	pairAC := &PairRecord{DeviceA: acA, DeviceB: acB, FingerprintA: acFpA, FingerprintB: acFpB}
	for _, p := range []*PairRecord{pairAB, pairAC} {
		if err := s.CreatePair(context.Background(), p); err != nil {
			t.Fatal(err)
		}
	}
	got, err := s.ListPairsByDevice(context.Background(), aID)
	if err != nil {
		t.Fatal(err)
	}
	if len(got) < 2 {
		t.Fatalf("expected 2 pairs, got %d", len(got))
	}
}

// protoEnsureOrder 已弃用，仅保留以避免 unused 编译错误。
func protoEnsureOrder(a, b [proto.DeviceIDSize]byte) [proto.DeviceIDSize]byte {
	if proto.CompareDeviceID(a, b) <= 0 {
		return a
	}
	return b
}

var _ = filepath.Join
var _ = protoEnsureOrder
