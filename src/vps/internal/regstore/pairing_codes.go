package regstore

import (
	"context"
	"crypto/rand"
	"database/sql"
	"errors"
	"fmt"
	"time"

	"golang.org/x/crypto/bcrypt"
	"modernc.org/sqlite"
	lib "modernc.org/sqlite/lib"

	"desklink/vps/internal/proto"
)

// PairingCodeRecord 是配对码行只读快照。
type PairingCodeRecord struct {
	CodeID           int64
	DeviceID         [proto.DeviceIDSize]byte
	CodeHash         string
	CreatedAt        int64
	ExpiresAt        int64
	ConsumedAt       sql.NullInt64
	ConsumedByDevice []byte
}

// PairingCodeTTL 默认 5 分钟（与 .team/vps-prep/01-proto-spec.md §3.3 一致）。
const PairingCodeTTL = 5 * time.Minute

// bcryptCost 固定为 10（约 60ms 一次哈希），与设计文档一致。
const bcryptCost = 10

// Errors returned by pairing-code operations.
var (
	ErrPairingCodeInvalid  = errors.New("regstore: pairing code invalid")
	ErrPairingCodeExpired  = errors.New("regstore: pairing code expired")
	ErrPairingCodeConsumed = errors.New("regstore: pairing code already consumed")
	// 重复配对的哨兵是 pairs.go 的 ErrPairAlreadyExists（同包共用）。
)

// isUniqueConstraintError 判断 err 链上是否为 SQLite UNIQUE 约束冲突。
//
// 对应 RedeemPairingCode 的 INSERT pairs：两个设备之间只允许一条
// revoked=0 的配对（schema.sql 的 idx_pairs_pair_unique 部分唯一索引）。
// 唯一索引冲突返回错误码 SQLITE_CONSTRAINT_UNIQUE(2067)。
func isUniqueConstraintError(err error) bool {
	var sqliteErr *sqlite.Error
	if !errors.As(err, &sqliteErr) {
		return false
	}
	return sqliteErr.Code() == lib.SQLITE_CONSTRAINT_UNIQUE
}

// IssuePairingCode 由被控端调用：生成随机码 + bcrypt 存库，返回明文码给被控端（不入库）。
//
// deviceID 必须已注册；返回明文码供被控端 UI 展示给用户。
func (s *Store) IssuePairingCode(ctx context.Context, deviceID [proto.DeviceIDSize]byte, ttl time.Duration) (proto.PairingCode, error) {
	if ttl <= 0 {
		ttl = PairingCodeTTL
	}
	code, err := proto.RandomPairingCode(func(n int) ([]byte, error) {
		buf := make([]byte, n)
		if _, err := rand.Read(buf); err != nil {
			return nil, err
		}
		return buf, nil
	})
	if err != nil {
		return proto.PairingCode{}, fmt.Errorf("regstore: random code: %w", err)
	}
	hash, err := bcrypt.GenerateFromPassword([]byte(code.String()), bcryptCost)
	if err != nil {
		return proto.PairingCode{}, fmt.Errorf("regstore: bcrypt: %w", err)
	}
	now := time.Now().Unix()
	ttlSec := int64(ttl / time.Second)
	if ttlSec <= 0 {
		ttlSec = int64(PairingCodeTTL / time.Second)
	}
	// CHECK 约束：expires_at > created_at
	expires := now + ttlSec
	if expires <= now {
		expires = now + 1
	}
	_, err = s.db.ExecContext(ctx, `
		INSERT INTO pairing_codes (device_id, code_hash, created_at, expires_at, consumed_at, consumed_by_device)
		VALUES (?, ?, ?, ?, NULL, NULL)
	`, deviceID[:], string(hash), now, expires)
	if err != nil {
		return proto.PairingCode{}, fmt.Errorf("regstore: insert code: %w", err)
	}
	return code, nil
}

// RedeemResult 是 RedeemPairingCode 成功时的返回值。
type RedeemResult struct {
	Pair           *PairRecord
	PairingCodeID  int64
	IssuerDeviceID [proto.DeviceIDSize]byte
	ConsumedAt     int64
}

// RedeemPairingCode 在单事务内原子领取配对码：
//
//   - SELECT issuer 近期（created_at > now - 3600）的全部码
//   - 逐条 bcrypt.CompareHashAndPassword 验真；命中后按状态分支：
//   - consumed_at IS NOT NULL → ErrPairingCodeConsumed
//   - expires_at <= now         → ErrPairingCodeExpired
//   - 活跃                     → UPDATE consumed_at + INSERT pairs
//   - UPDATE 行数 = 0 → ErrPairingCodeConsumed（防并发重放）
//   - COMMIT
//
// 由 caller 提供两个 device 的 pubkey 用于构造 pair 行（issuer = 出码端，redeemer = 领取端）。
// 设计依据：.team/vps-prep/01-proto-spec.md §3.4 原子领取流程；redeem API 知道 issuer_pubkey。
func (s *Store) RedeemPairingCode(ctx context.Context, code proto.PairingCode, issuerPK, redeemerPK proto.PublicKey) (*RedeemResult, error) {
	if code.IsSentinel() {
		return nil, ErrPairingCodeInvalid
	}
	issuerID := issuerPK.DeviceID()
	redeemerID := redeemerPK.DeviceID()
	if issuerID == redeemerID {
		return nil, fmt.Errorf("regstore: issuer and redeemer must differ")
	}
	var result RedeemResult
	err := s.WithImmediateTx(ctx, func(tx *sql.Tx) error {
		now := time.Now().Unix()
		// 1. 拉 issuer 近期全部码（含已消费/过期）便于区分错误码
		rows, err := tx.QueryContext(ctx, `
			SELECT code_id, code_hash, expires_at, consumed_at
			FROM pairing_codes
			WHERE device_id = ? AND created_at > ?
			ORDER BY code_id DESC LIMIT 200
		`, issuerID[:], now-3600)
		if err != nil {
			return fmt.Errorf("regstore: lookup codes: %w", err)
		}
		defer rows.Close()

		var matchedCodeID int64
		var matched bool
		var matchedConsumed, matchedExpired bool
		for rows.Next() {
			var codeID int64
			var codeHash string
			var expiresAt int64
			var consumedAt sql.NullInt64
			if err := rows.Scan(&codeID, &codeHash, &expiresAt, &consumedAt); err != nil {
				return fmt.Errorf("regstore: scan code: %w", err)
			}
			if err := bcrypt.CompareHashAndPassword([]byte(codeHash), []byte(code.String())); err != nil {
				continue
			}
			matchedCodeID = codeID
			matched = true
			if consumedAt.Valid {
				matchedConsumed = true
			}
			if expiresAt <= now {
				matchedExpired = true
			}
			break
		}
		if err := rows.Err(); err != nil {
			return fmt.Errorf("regstore: rows err: %w", err)
		}
		if !matched {
			return ErrPairingCodeInvalid
		}
		if matchedConsumed {
			return ErrPairingCodeConsumed
		}
		if matchedExpired {
			return ErrPairingCodeExpired
		}
		// 2. 翻 consumed_at（带 consumed_at IS NULL 守护防并发重放）
		res, err := tx.ExecContext(ctx, `
			UPDATE pairing_codes
			   SET consumed_at = ?, consumed_by_device = ?
			 WHERE code_id = ? AND consumed_at IS NULL
		`, now, redeemerID[:], matchedCodeID)
		if err != nil {
			return fmt.Errorf("regstore: update code: %w", err)
		}
		n, _ := res.RowsAffected()
		if n == 0 {
			return ErrPairingCodeConsumed
		}
		// 3. 写 pairs 行（按字典序）
		deviceA, deviceB := proto.EnsureOrder(issuerID, redeemerID)
		var fpA, fpB [proto.FingerprintSize]byte
		if deviceA == issuerID {
			fpA = issuerPK.Fingerprint()
			fpB = redeemerPK.Fingerprint()
		} else {
			fpA = redeemerPK.Fingerprint()
			fpB = issuerPK.Fingerprint()
		}
		nonce, err := proto.NewPairServerNonce()
		if err != nil {
			return fmt.Errorf("regstore: pair nonce: %w", err)
		}
		createdAtNs := now * 1_000_000_000
		pairID := proto.DerivePairID(deviceA, deviceB, createdAtNs, nonce)
		pairUUID := newPairUUIDForTest()
		_, err = tx.ExecContext(ctx, `
			INSERT INTO pairs (
				pair_id, pair_uuid, device_a, device_b,
				fingerprint_a, fingerprint_b,
				created_at, revoked, revoked_at, revoked_by
			) VALUES (?, ?, ?, ?, ?, ?, ?, 0, NULL, NULL)
		`, pairID[:], pairUUID, deviceA[:], deviceB[:], fpA[:], fpB[:], now)
		if err != nil {
			if isUniqueConstraintError(err) {
				// 两设备间已有活跃配对。返回哨兵错误触发 ROLLBACK：
				// 配对码保持未消费状态，用户撤销旧配对后可用同一码重领。
				return ErrPairAlreadyExists
			}
			return fmt.Errorf("regstore: insert pair: %w", err)
		}
		result.Pair = &PairRecord{
			PairID:       pairID,
			PairUUID:     pairUUID,
			DeviceA:      deviceA,
			DeviceB:      deviceB,
			FingerprintA: fpA,
			FingerprintB: fpB,
			CreatedAt:    now,
		}
		result.PairingCodeID = matchedCodeID
		result.IssuerDeviceID = issuerID
		result.ConsumedAt = now
		return nil
	})
	if err != nil {
		return nil, err
	}
	return &result, nil
}

// sliceFromDeviceID 把 [32]byte device_id 转 []byte 供 ExecContext 参数；
// 解决 "cannot slice unaddressable value" 编译错误（PublicKey.DeviceID() 返回值不可寻址）。
func sliceFromDeviceID(id [proto.DeviceIDSize]byte) []byte {
	return id[:]
}

// ListActivePairingCodes 返回所有未过期且未领取的配对码（限 1000 条）。
// 测试用：用于"以 issuer pk + 明文码"反查。
func (s *Store) ListActivePairingCodes(ctx context.Context, deviceID [proto.DeviceIDSize]byte) ([]*PairingCodeRecord, error) {
	now := time.Now().Unix()
	rows, err := s.db.QueryContext(ctx, `
		SELECT code_id, device_id, code_hash, created_at, expires_at, consumed_at, consumed_by_device
		FROM pairing_codes
		WHERE device_id = ? AND consumed_at IS NULL AND expires_at > ?
		ORDER BY created_at DESC LIMIT 1000
	`, deviceID[:], now)
	if err != nil {
		return nil, fmt.Errorf("regstore: list codes: %w", err)
	}
	defer rows.Close()
	var out []*PairingCodeRecord
	for rows.Next() {
		r := &PairingCodeRecord{}
		// database/sql 不支持直接 Scan 到 *[32]byte，需经 []byte 中转
		// （与 devices.go ListDevices 的扫描模式一致）。
		var deviceIDRaw []byte
		if err := rows.Scan(&r.CodeID, &deviceIDRaw, &r.CodeHash, &r.CreatedAt, &r.ExpiresAt, &r.ConsumedAt, &r.ConsumedByDevice); err != nil {
			return nil, fmt.Errorf("regstore: scan code: %w", err)
		}
		if len(deviceIDRaw) != proto.DeviceIDSize {
			return nil, fmt.Errorf("regstore: bad device_id length %d in pairing code", len(deviceIDRaw))
		}
		copy(r.DeviceID[:], deviceIDRaw)
		out = append(out, r)
	}
	return out, rows.Err()
}

// VerifyAndRedeemPairingCode 是更直接的领取实现：按 device_id 拉活跃码，bcrypt 验真后领取。
// 适用于"issuer 已固定（被控端）"的典型场景：控制端在 redeem 时不知道 issuer device_id 需靠码反查，
// 但**业务上**控制端输入码后由 relay 携带 issuer device_id 转发，本函数主要用于测试与被控端主动
// 携带 issuer 信息的高层封装。
func (s *Store) VerifyAndRedeemPairingCode(ctx context.Context, code proto.PairingCode, issuerPK, redeemerPK proto.PublicKey) (*RedeemResult, error) {
	if code.IsSentinel() {
		return nil, ErrPairingCodeInvalid
	}
	issuerID := issuerPK.DeviceID()
	codes, err := s.ListActivePairingCodes(ctx, issuerID)
	if err != nil {
		return nil, err
	}
	var matchedCodeID int64
	var matched bool
	for _, c := range codes {
		if err := bcrypt.CompareHashAndPassword([]byte(c.CodeHash), []byte(code.String())); err == nil {
			matchedCodeID = c.CodeID
			matched = true
			break
		}
	}
	if !matched {
		return nil, ErrPairingCodeInvalid
	}
	_ = matchedCodeID
	// 已确定 issuer；调用 RedeemPairingCode 同事务版本
	now := time.Now().Unix()
	var result RedeemResult
	err = s.WithImmediateTx(ctx, func(tx *sql.Tx) error {
		// SELECT + UPDATE 同事务原子化
		var expiresAt int64
		var consumedAt sql.NullInt64
		err := tx.QueryRowContext(ctx, `
			SELECT expires_at, consumed_at FROM pairing_codes
			WHERE device_id = ? AND code_hash IN (
				SELECT code_hash FROM pairing_codes WHERE code_id = ?
			)
		`, issuerID[:], matchedCodeID).Scan(&expiresAt, &consumedAt)
		if err != nil {
			return fmt.Errorf("regstore: lookup code: %w", err)
		}
		if consumedAt.Valid {
			return ErrPairingCodeConsumed
		}
		if expiresAt <= now {
			return ErrPairingCodeExpired
		}
		res, err := tx.ExecContext(ctx, `
			UPDATE pairing_codes SET consumed_at = ?, consumed_by_device = ?
			WHERE device_id = ? AND code_hash IN (SELECT code_hash FROM pairing_codes WHERE code_id = ?)
			  AND consumed_at IS NULL
		`, now, sliceFromDeviceID(redeemerPK.DeviceID()), issuerID[:], matchedCodeID)
		if err != nil {
			return fmt.Errorf("regstore: update code: %w", err)
		}
		n, _ := res.RowsAffected()
		if n == 0 {
			return ErrPairingCodeConsumed
		}
		deviceA, deviceB := proto.EnsureOrder(issuerID, redeemerPK.DeviceID())
		var fpA, fpB [proto.FingerprintSize]byte
		if deviceA == issuerID {
			fpA = issuerPK.Fingerprint()
			fpB = redeemerPK.Fingerprint()
		} else {
			fpA = redeemerPK.Fingerprint()
			fpB = issuerPK.Fingerprint()
		}
		nonce, err := proto.NewPairServerNonce()
		if err != nil {
			return err
		}
		createdAtNs := now * 1_000_000_000
		pairID := proto.DerivePairID(deviceA, deviceB, createdAtNs, nonce)
		pairUUID := newPairUUIDForTest()
		_, err = tx.ExecContext(ctx, `
			INSERT INTO pairs (pair_id, pair_uuid, device_a, device_b, fingerprint_a, fingerprint_b,
				created_at, revoked, revoked_at, revoked_by)
			VALUES (?, ?, ?, ?, ?, ?, ?, 0, NULL, NULL)
		`, pairID[:], pairUUID, deviceA[:], deviceB[:], fpA[:], fpB[:], now)
		if err != nil {
			if isUniqueConstraintError(err) {
				return ErrPairAlreadyExists
			}
			return err
		}
		result.Pair = &PairRecord{PairID: pairID, PairUUID: pairUUID, DeviceA: deviceA, DeviceB: deviceB, FingerprintA: fpA, FingerprintB: fpB, CreatedAt: now}
		result.PairingCodeID = matchedCodeID
		result.IssuerDeviceID = issuerID
		result.ConsumedAt = now
		return nil
	})
	if err != nil {
		return nil, err
	}
	return &result, nil
}

// CleanupExpiredPairingCodes 删除过期或已领取超过 24h 的配对码；定期清理。
func (s *Store) CleanupExpiredPairingCodes(ctx context.Context) (int64, error) {
	now := time.Now().Unix()
	res, err := s.db.ExecContext(ctx, `
		DELETE FROM pairing_codes
		WHERE expires_at < ? OR (consumed_at IS NOT NULL AND consumed_at < ?)
	`, now, now-86400)
	if err != nil {
		return 0, fmt.Errorf("regstore: cleanup codes: %w", err)
	}
	n, _ := res.RowsAffected()
	return n, nil
}
