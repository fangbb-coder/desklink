// Package relay 实现 relayd 的中继核心：双传输监听、设备挑战鉴权、内存接线表、
// 宽限窗、密文泵送、撤销踢线。
//
// 本文件只放"挑战签名 transcript"的构造与校验——它必须与 registryd 的
// internal/regapi 完全一致，否则两端验签结果不同。为消除漂移风险，两处共用
// proto.ChallengeTranscriptDomain 常量与同一套构造顺序。
package relay

import (
	"crypto/ed25519"
	"encoding/binary"
	"errors"
	"fmt"
	"sync"
	"time"

	"desklink/vps/internal/proto"
)

// ChallengeTranscript 构造设备挑战签名的确定性命名字节串。
//
// 布局（与 regapi.transcript 严格一致）：
//
//	[ "desklink/device-challenge/v1" ]
//	[ u32 challengeID BE ]
//	[ u64 unixSeconds BE ]
//	[ 32B nonce ]
//	[ 32B device_id ]
//	[ 32B ed25519_pubkey ]
//
// relay 在收到 Hello 时会先用 device_id 从 registry 取回该设备的 pubkey 与
// device_id，再用同样的顺序重建 transcript 并验签。
func ChallengeTranscript(challengeID uint32, unixSeconds int64, nonce []byte, deviceID [proto.DeviceIDSize]byte, pubKey proto.PublicKey) ([]byte, error) {
	if len(nonce) != ChallengeNonceLen {
		return nil, fmt.Errorf("relay: challenge nonce must be %d bytes, got %d", ChallengeNonceLen, len(nonce))
	}
	buf := make([]byte, 0, len(proto.ChallengeTranscriptDomain)+4+8+ChallengeNonceLen+2*proto.DeviceIDSize)
	buf = append(buf, proto.ChallengeTranscriptDomain...)
	var num [8]byte
	binary.BigEndian.PutUint32(num[:4], challengeID)
	buf = append(buf, num[:4]...)
	binary.BigEndian.PutUint64(num[:], uint64(unixSeconds))
	buf = append(buf, num[:]...)
	buf = append(buf, nonce...)
	buf = append(buf, deviceID[:]...)
	buf = append(buf, pubKey[:]...)
	return buf, nil
}

// ChallengeNonceLen 是挑战 nonce 长度（32 字节，与 C# 客户端一致）。
const ChallengeNonceLen = 32

// ChallengeTimestampSkew 是挑战时间戳允许的最大偏移。relay 与客户端可能时钟
// 不齐（自用 VPS 通常 NTP 同步）；给 120s 容差。
const ChallengeTimestampSkew = 120 * time.Second

// VerifyDeviceChallenge 用 pubKey 验证 sig 对挑战 transcript 的 Ed25519 签名，
// 并校验时间戳落在容差窗内。
//
// 返回 nil 表示通过；否则返回带原因的 error（relay 会回 ReasonAuthFailed）。
//
// 注意：本函数只验证"签名是否合法"，不验证"签名是否第一次出现"——
// 重放防护由 <see>ChallengeReplayGuard</see> 承担（必须在验签通过后调用）。
func VerifyDeviceChallenge(challengeID uint32, unixSeconds int64, nonce []byte, deviceID [proto.DeviceIDSize]byte, pubKey proto.PublicKey, sig []byte) error {
	if len(sig) != ed25519.SignatureSize {
		return fmt.Errorf("relay: signature must be %d bytes, got %d", ed25519.SignatureSize, len(sig))
	}
	now := time.Now().Unix()
	delta := now - unixSeconds
	if delta < 0 {
		delta = -delta
	}
	if time.Duration(delta)*time.Second > ChallengeTimestampSkew {
		return fmt.Errorf("relay: challenge timestamp outside %s skew window (delta=%ds)", ChallengeTimestampSkew, delta)
	}
	tr, err := ChallengeTranscript(challengeID, unixSeconds, nonce, deviceID, pubKey)
	if err != nil {
		return err
	}
	if !ed25519.Verify(ed25519.PublicKey(pubKey[:]), tr, sig) {
		return errors.New("relay: ed25519 challenge verify failed")
	}
	return nil
}

// challengeReplayTTL 是重放记录的保留时长。
//
// 推导：时间戳校验窗是 ±ChallengeTimestampSkew。一条签名首次被接受时（最不利
// 情况：时间戳恰好是 now-120s），距离它彻底失效（now' > ts+120s）最长还有
// 2×skew = 240s。TTL 取 240s 保证"窗口关闭前签名必然仍命中记录"。
const challengeReplayTTL = 2 * ChallengeTimestampSkew

// ChallengeReplayGuard 防止同一挑战在时间窗内重放（内存去重）。
//
// 威胁模型：VerifyDeviceChallenge 只校验签名与时间戳容差，不校验 nonce
// 一次性——同一条 Hello 的签名在窗口内重放会再次通过，攻击者抓到一条
// Hello（如链路劫持）即可在最长 240s 内冒充该设备重连。
//
// 实现约束（自用单实例 VPS）：
//   - 内存去重，不跨实例共享、不持久化；
//   - 进程重启记录清空，但重启也断开所有已接线会话，重放面随连接销毁，
//     风险可接受。
type ChallengeReplayGuard struct {
	mu        sync.Mutex
	seen      map[string]time.Time
	lastSweep time.Time
	now       func() time.Time
}

// NewChallengeReplayGuard 构造去重器。
func NewChallengeReplayGuard() *ChallengeReplayGuard {
	return &ChallengeReplayGuard{
		seen: make(map[string]time.Time),
		now:  time.Now,
	}
}

// CheckAndRecord 原子地"查重 + 登记"一条挑战。返回 true 表示首次出现（放行），
// false 表示重放（拒绝）。
//
// 键 = challengeID || unixSeconds || nonce || deviceID 的原始字节串，
// 即 transcript 中除 pubkey 外的全部可变字段（pubkey 由 deviceID 唯一决定）。
func (g *ChallengeReplayGuard) CheckAndRecord(challengeID uint32, unixSeconds int64, nonce []byte, deviceID [proto.DeviceIDSize]byte) bool {
	var num [8]byte
	key := make([]byte, 0, 4+8+len(nonce)+len(deviceID))
	binary.BigEndian.PutUint32(num[:4], challengeID)
	key = append(key, num[:4]...)
	binary.BigEndian.PutUint64(num[:], uint64(unixSeconds))
	key = append(key, num[:]...)
	key = append(key, nonce...)
	key = append(key, deviceID[:]...)

	g.mu.Lock()
	defer g.mu.Unlock()
	now := g.now()
	if now.Sub(g.lastSweep) >= time.Minute {
		for k, at := range g.seen {
			if now.Sub(at) >= challengeReplayTTL {
				delete(g.seen, k)
			}
		}
		g.lastSweep = now
	}
	if _, dup := g.seen[string(key)]; dup {
		return false
	}
	g.seen[string(key)] = now
	return true
}

// SetClock 注入测试时钟。
func (g *ChallengeReplayGuard) SetClock(now func() time.Time) {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.now = now
}
