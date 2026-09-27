package regstore

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"
	"time"

	"github.com/google/uuid"

	"desklink/vps/internal/proto"
)

// PairRecord 是 regstore 暴露的配对只读快照。
type PairRecord struct {
	PairID       [proto.PairIDSize]byte
	PairUUID     string
	DeviceA      [proto.DeviceIDSize]byte
	DeviceB      [proto.DeviceIDSize]byte
	FingerprintA [proto.FingerprintSize]byte
	FingerprintB [proto.FingerprintSize]byte
	CreatedAt    int64
	Revoked      bool
	RevokedAt    sql.NullInt64
	RevokedBy    []byte
}

// Errors returned by pair CRUD.
var (
	ErrPairNotFound       = errors.New("regstore: pair not found")
	ErrPairAlreadyExists  = errors.New("regstore: pair already exists")
	ErrPairAlreadyRevoked = errors.New("regstore: pair already revoked")
)

// CreatePair 在事务内创建配对行。
// 调用方负责：device_a/b 必须字典序 a <= b；fingerprint 与 pubkey 一致。
// 本函数不锁 device 行（设备必须已存在），但 FK 约束保证 device_a/b 存在。
func (s *Store) CreatePair(ctx context.Context, p *PairRecord) error {
	if p.PairUUID == "" {
		p.PairUUID = uuid.NewString()
	}
	if p.CreatedAt == 0 {
		p.CreatedAt = time.Now().Unix()
	}
	if proto.CompareDeviceID(p.DeviceA, p.DeviceB) > 0 {
		return fmt.Errorf("regstore: device_a must be <= device_b (got a=%x b=%x)", p.DeviceA, p.DeviceB)
	}
	nonce, err := proto.NewPairServerNonce()
	if err != nil {
		return fmt.Errorf("regstore: pair nonce: %w", err)
	}
	createdAtNs := p.CreatedAt * 1_000_000_000
	p.PairID = proto.DerivePairID(p.DeviceA, p.DeviceB, createdAtNs, nonce)
	return s.WithTx(ctx, nil, func(tx *sql.Tx) error {
		_, err := tx.ExecContext(ctx, `
			INSERT INTO pairs (
				pair_id, pair_uuid,
				device_a, device_b,
				fingerprint_a, fingerprint_b,
				created_at, revoked, revoked_at, revoked_by
			) VALUES (?, ?, ?, ?, ?, ?, ?, 0, NULL, NULL)
		`,
			p.PairID[:], p.PairUUID,
			p.DeviceA[:], p.DeviceB[:],
			p.FingerprintA[:], p.FingerprintB[:],
			p.CreatedAt)
		if err != nil {
			if isUniqueConstraintError(err) {
				return ErrPairAlreadyExists
			}
			return fmt.Errorf("regstore: insert pair: %w", err)
		}
		return nil
	})
}

// GetPairByUUID 按 pair_uuid 查询。
func (s *Store) GetPairByUUID(ctx context.Context, pairUUID string) (*PairRecord, error) {
	row := s.db.QueryRowContext(ctx, `
		SELECT pair_id, pair_uuid, device_a, device_b, fingerprint_a, fingerprint_b,
		       created_at, revoked, revoked_at, revoked_by
		FROM pairs WHERE pair_uuid = ?
	`, pairUUID)
	return scanPair(row)
}

// GetActivePairByDevices 按 (device_a, device_b) 字典序查未撤销配对。
func (s *Store) GetActivePairByDevices(ctx context.Context, deviceA, deviceB [proto.DeviceIDSize]byte) (*PairRecord, error) {
	a, b := proto.EnsureOrder(deviceA, deviceB)
	row := s.db.QueryRowContext(ctx, `
		SELECT pair_id, pair_uuid, device_a, device_b, fingerprint_a, fingerprint_b,
		       created_at, revoked, revoked_at, revoked_by
		FROM pairs WHERE device_a = ? AND device_b = ? AND revoked = 0
	`, a[:], b[:])
	return scanPair(row)
}

// ListPairsByDevice 返回某设备参与的所有未撤销配对。
func (s *Store) ListPairsByDevice(ctx context.Context, deviceID [proto.DeviceIDSize]byte) ([]*PairRecord, error) {
	rows, err := s.db.QueryContext(ctx, `
		SELECT pair_id, pair_uuid, device_a, device_b, fingerprint_a, fingerprint_b,
		       created_at, revoked, revoked_at, revoked_by
		FROM pairs WHERE (device_a = ? OR device_b = ?) AND revoked = 0
		ORDER BY created_at DESC
	`, deviceID[:], deviceID[:])
	if err != nil {
		return nil, fmt.Errorf("regstore: list pairs: %w", err)
	}
	defer rows.Close()
	var out []*PairRecord
	for rows.Next() {
		p, err := scanPairRows(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, p)
	}
	return out, rows.Err()
}

func scanPair(row *sql.Row) (*PairRecord, error) {
	var p PairRecord
	var pairIDRaw, deviceARaw, deviceBRaw, fpARaw, fpBRaw []byte
	var revokedInt int
	var revokedBy []byte
	if err := row.Scan(
		&pairIDRaw, &p.PairUUID,
		&deviceARaw, &deviceBRaw, &fpARaw, &fpBRaw,
		&p.CreatedAt, &revokedInt, &p.RevokedAt, &revokedBy,
	); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, ErrPairNotFound
		}
		return nil, fmt.Errorf("regstore: scan pair: %w", err)
	}
	copy(p.PairID[:], pairIDRaw)
	copy(p.DeviceA[:], deviceARaw)
	copy(p.DeviceB[:], deviceBRaw)
	copy(p.FingerprintA[:], fpARaw)
	copy(p.FingerprintB[:], fpBRaw)
	p.Revoked = revokedInt == 1
	p.RevokedBy = revokedBy
	return &p, nil
}

func scanPairRows(rows *sql.Rows) (*PairRecord, error) {
	var p PairRecord
	var pairIDRaw, deviceARaw, deviceBRaw, fpARaw, fpBRaw []byte
	var revokedInt int
	var revokedBy []byte
	if err := rows.Scan(
		&pairIDRaw, &p.PairUUID,
		&deviceARaw, &deviceBRaw, &fpARaw, &fpBRaw,
		&p.CreatedAt, &revokedInt, &p.RevokedAt, &revokedBy,
	); err != nil {
		return nil, fmt.Errorf("regstore: scan pair row: %w", err)
	}
	copy(p.PairID[:], pairIDRaw)
	copy(p.DeviceA[:], deviceARaw)
	copy(p.DeviceB[:], deviceBRaw)
	copy(p.FingerprintA[:], fpARaw)
	copy(p.FingerprintB[:], fpBRaw)
	p.Revoked = revokedInt == 1
	p.RevokedBy = revokedBy
	return &p, nil
}

// RevokeReason 撤销原因枚举。
type RevokeReason string

const (
	RevokeReasonUserRevoked RevokeReason = "user_revoked"
	RevokeReasonDuplicate   RevokeReason = "duplicate"
	RevokeReasonKeyRotation RevokeReason = "key_rotation"
)

// RevokePairByUUID 撤销指定 pair_uuid 的配对；并发撤销通过 WHERE revoked=0 + rowsAffected 防护。
// 返回 (revoked_record, already_revoked_flag, error)。
func (s *Store) RevokePairByUUID(ctx context.Context, pairUUID string, by [proto.DeviceIDSize]byte, reason RevokeReason) (*PairRecord, bool, error) {
	if reason == "" {
		reason = RevokeReasonUserRevoked
	}
	var rec *PairRecord
	var already bool
	err := s.WithTx(ctx, nil, func(tx *sql.Tx) error {
		// 1. SELECT 当前行（含 pair_id/device_a/b for revocations 写入）
		row := tx.QueryRowContext(ctx, `
			SELECT pair_id, pair_uuid, device_a, device_b, fingerprint_a, fingerprint_b,
			       created_at, revoked, revoked_at, revoked_by
			FROM pairs WHERE pair_uuid = ?
		`, pairUUID)
		p, err := scanPair(row)
		if err != nil {
			return err
		}
		if p.Revoked {
			rec = p
			already = true
			return nil
		}
		// 2. UPDATE 翻 revoked=1
		now := time.Now().Unix()
		res, err := tx.ExecContext(ctx, `
			UPDATE pairs SET revoked = 1, revoked_at = ?, revoked_by = ?
			WHERE pair_uuid = ? AND revoked = 0
		`, now, by[:], pairUUID)
		if err != nil {
			return fmt.Errorf("regstore: revoke update: %w", err)
		}
		n, _ := res.RowsAffected()
		if n == 0 {
			// 并发撤销：当前事务看到 revoked=1
			already = true
			return nil
		}
		// 3. INSERT revocations
		if _, err := tx.ExecContext(ctx, `
			INSERT INTO revocations (
				pair_uuid, pair_id, device_a, device_b,
				revoked_at, revoked_by, reason, pushed_at
			) VALUES (?, ?, ?, ?, ?, ?, ?, NULL)
		`, p.PairUUID, p.PairID[:], p.DeviceA[:], p.DeviceB[:], now, by[:], string(reason)); err != nil {
			return fmt.Errorf("regstore: insert revocation: %w", err)
		}
		// 4. 标记 pushed_at 在 regnotify 推送成功后由 Publisher 写回
		p.Revoked = true
		p.RevokedAt = sql.NullInt64{Int64: now, Valid: true}
		if len(by[:]) > 0 {
			p.RevokedBy = by[:]
		}
		rec = p
		return nil
	})
	if err != nil {
		return nil, false, err
	}
	return rec, already, nil
}

// MarkRevocationPushed 把指定 revocation_id 的 pushed_at 写为当前时间。
// 由 regnotify.Publisher 在 socket ACK 后调用。
func (s *Store) MarkRevocationPushed(ctx context.Context, revocationID int64) error {
	now := time.Now().Unix()
	res, err := s.db.ExecContext(ctx, `UPDATE revocations SET pushed_at = ? WHERE revocation_id = ? AND pushed_at IS NULL`, now, revocationID)
	if err != nil {
		return fmt.Errorf("regstore: mark pushed: %w", err)
	}
	_, _ = res.RowsAffected()
	return nil
}

// GetPairUUIDHex 返回 pair_uuid 的 hex 形式（便于日志/调试）。
func (p *PairRecord) GetPairUUIDHex() string {
	return hex.EncodeToString([]byte(p.PairUUID))
}

// randomUUIDTestHook 仅供测试替换随机源。
var randomUUIDTestHook = func() string { return uuid.NewString() }

// _ 引用以避免未使用。
var _ = randomUUIDTestHook

// _ 引用 rand 包，避免 unused import。
var _ = rand.Reader
