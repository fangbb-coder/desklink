package regstore

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"time"

	"desklink/vps/internal/proto"
)

// DeviceRecord 是 regstore 暴露的设备只读快照。
type DeviceRecord struct {
	DeviceID     [proto.DeviceIDSize]byte
	PubKey       proto.PublicKey
	Fingerprint  [proto.FingerprintSize]byte
	NicknameCT   []byte
	NicknameAlgo int32
	Platform     string
	AgentMajor   uint8
	AgentMinor   uint8
	AgentPatch   uint8
	RegisteredAt int64
	LastSeenAt   int64
	Revoked      bool
	RevokedAt    sql.NullInt64
}

// Errors returned by device CRUD.
var (
	ErrDeviceNotFound = errors.New("regstore: device not found")
	ErrDeviceExists   = errors.New("regstore: device already exists")
)

// RegisterDevice 首次注册设备。若 device_id 已存在返回 ErrDeviceExists。
func (s *Store) RegisterDevice(ctx context.Context, rec *DeviceRecord) error {
	if rec.RegisteredAt == 0 {
		rec.RegisteredAt = time.Now().Unix()
	}
	if rec.LastSeenAt == 0 {
		rec.LastSeenAt = rec.RegisteredAt
	}
	if rec.Revoked {
		return errors.New("regstore: cannot register a revoked device")
	}
	deviceID := rec.PubKey.DeviceID()
	copy(rec.DeviceID[:], deviceID[:])
	fp := rec.PubKey.Fingerprint()
	copy(rec.Fingerprint[:], fp[:])
	return s.WithTx(ctx, nil, func(tx *sql.Tx) error {
		_, err := tx.ExecContext(ctx, `
			INSERT INTO devices (
				device_id, ed25519_pubkey, fingerprint,
				nickname_ct, nickname_algo,
				platform, agent_major, agent_minor, agent_patch,
				registered_at, last_seen_at, revoked, revoked_at
			) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, NULL)
			ON CONFLICT(device_id) DO NOTHING
		`, rec.DeviceID[:], rec.PubKey[:], rec.Fingerprint[:],
			rec.NicknameCT, rec.NicknameAlgo,
			rec.Platform, rec.AgentMajor, rec.AgentMinor, rec.AgentPatch,
			rec.RegisteredAt, rec.LastSeenAt)
		if err != nil {
			return fmt.Errorf("regstore: insert device: %w", err)
		}
		// ON CONFLICT DO NOTHING 不返回错误但也不会写；用 RowsAffected 探测。
		return nil
	})
}

// GetDevice 按 device_id（hex 或 32B）查询；返回 ErrDeviceNotFound。
func (s *Store) GetDevice(ctx context.Context, deviceID [proto.DeviceIDSize]byte) (*DeviceRecord, error) {
	row := s.db.QueryRowContext(ctx, `
		SELECT device_id, ed25519_pubkey, fingerprint,
		       nickname_ct, nickname_algo,
		       platform, agent_major, agent_minor, agent_patch,
		       registered_at, last_seen_at, revoked, revoked_at
		FROM devices WHERE device_id = ?
	`, deviceID[:])
	return scanDevice(row)
}

// GetDeviceByPubKey 按公钥查（客户端无需知道 device_id）。
func (s *Store) GetDeviceByPubKey(ctx context.Context, pk proto.PublicKey) (*DeviceRecord, error) {
	row := s.db.QueryRowContext(ctx, `
		SELECT device_id, ed25519_pubkey, fingerprint,
		       nickname_ct, nickname_algo,
		       platform, agent_major, agent_minor, agent_patch,
		       registered_at, last_seen_at, revoked, revoked_at
		FROM devices WHERE ed25519_pubkey = ?
	`, pk[:])
	return scanDevice(row)
}

func scanDevice(row *sql.Row) (*DeviceRecord, error) {
	var rec DeviceRecord
	var deviceIDRaw []byte
	var pubKeyRaw []byte
	var fingerprintRaw []byte
	var nicknameCT []byte
	var nicknameAlgo int32
	var platform sql.NullString
	var revokedInt int
	var revokedAt sql.NullInt64
	if err := row.Scan(
		&deviceIDRaw, &pubKeyRaw, &fingerprintRaw,
		&nicknameCT, &nicknameAlgo,
		&platform, &rec.AgentMajor, &rec.AgentMinor, &rec.AgentPatch,
		&rec.RegisteredAt, &rec.LastSeenAt, &revokedInt, &revokedAt,
	); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, ErrDeviceNotFound
		}
		return nil, fmt.Errorf("regstore: scan device: %w", err)
	}
	copy(rec.DeviceID[:], deviceIDRaw)
	copy(rec.PubKey[:], pubKeyRaw)
	copy(rec.Fingerprint[:], fingerprintRaw)
	rec.Revoked = revokedInt == 1
	rec.RevokedAt = revokedAt
	rec.NicknameCT = nicknameCT
	if platform.Valid {
		rec.Platform = platform.String
	}
	rec.NicknameAlgo = nicknameAlgo
	return &rec, nil
}

// Heartbeat 更新 last_seen_at。
func (s *Store) Heartbeat(ctx context.Context, deviceID [proto.DeviceIDSize]byte, ts int64) error {
	if ts == 0 {
		ts = time.Now().Unix()
	}
	res, err := s.db.ExecContext(ctx, `UPDATE devices SET last_seen_at = ? WHERE device_id = ?`, ts, deviceID[:])
	if err != nil {
		return fmt.Errorf("regstore: heartbeat: %w", err)
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return ErrDeviceNotFound
	}
	return nil
}

// UpdateDeviceMeta 修订 nickname_ct / platform / agent 版本。
func (s *Store) UpdateDeviceMeta(ctx context.Context, deviceID [proto.DeviceIDSize]byte, nicknameCT []byte, nicknameAlgo *int32, platform *string, agent *AgentVersion) error {
	updates := ""
	args := []any{}
	if nicknameCT != nil {
		updates += "nickname_ct = ?, nickname_algo = COALESCE(?, nickname_algo), "
		args = append(args, nicknameCT, nicknameAlgo)
	}
	if platform != nil {
		updates += "platform = ?, "
		args = append(args, *platform)
	}
	if agent != nil {
		updates += "agent_major = ?, agent_minor = ?, agent_patch = ?, "
		args = append(args, agent.Major, agent.Minor, agent.Patch)
	}
	if updates == "" {
		return nil
	}
	updates = updates[:len(updates)-2]
	args = append(args, deviceID[:])
	res, err := s.db.ExecContext(ctx, `UPDATE devices SET `+updates+` WHERE device_id = ?`, args...)
	if err != nil {
		return fmt.Errorf("regstore: update meta: %w", err)
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return ErrDeviceNotFound
	}
	return nil
}

// AgentVersion 表示客户端 agent 版本。
type AgentVersion struct {
	Major uint8
	Minor uint8
	Patch uint8
}

// RotateKey 写入 device_key_history 一行，并更新 devices 表的 ed25519_pubkey/fingerprint。
// newPubkeySig 是新私钥对 (oldPubkey || deviceID) 的 Ed25519 签名（64B）。
func (s *Store) RotateKey(ctx context.Context, deviceID [proto.DeviceIDSize]byte, oldPubKey, newPubKey proto.PublicKey, sig []byte, changedAt int64) error {
	if changedAt == 0 {
		changedAt = time.Now().Unix()
	}
	if len(sig) != 64 {
		return fmt.Errorf("regstore: rotate-key sig must be 64 bytes")
	}
	newFP := newPubKey.Fingerprint()
	oldFP := oldPubKey.Fingerprint()
	return s.WithTx(ctx, nil, func(tx *sql.Tx) error {
		// 1. 查设备当前 pubkey 必须等于 oldPubKey
		var storedPubKey []byte
		if err := tx.QueryRowContext(ctx, `SELECT ed25519_pubkey FROM devices WHERE device_id = ?`, deviceID[:]).Scan(&storedPubKey); err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				return ErrDeviceNotFound
			}
			return fmt.Errorf("regstore: rotate-key lookup: %w", err)
		}
		var stored proto.PublicKey
		copy(stored[:], storedPubKey)
		if stored != oldPubKey {
			return fmt.Errorf("regstore: rotate-key old pubkey mismatch")
		}
		// 2. 写 device_key_history
		if _, err := tx.ExecContext(ctx, `
			INSERT INTO device_key_history (
				device_id, old_pubkey, old_fingerprint,
				new_pubkey, new_fingerprint,
				changed_at, sig
			) VALUES (?, ?, ?, ?, ?, ?, ?)
		`, deviceID[:], oldPubKey[:], oldFP[:],
			newPubKey[:], newFP[:],
			changedAt, sig); err != nil {
			return fmt.Errorf("regstore: insert key history: %w", err)
		}
		// 3. 更新 devices
		if _, err := tx.ExecContext(ctx, `
			UPDATE devices
			   SET ed25519_pubkey = ?, fingerprint = ?
			 WHERE device_id = ?
		`, newPubKey[:], newFP[:], deviceID[:]); err != nil {
			return fmt.Errorf("regstore: update device pubkey: %w", err)
		}
		return nil
	})
}

// ListDevices 返回全部已注册设备（受配对/撤销状态过滤可选）。
func (s *Store) ListDevices(ctx context.Context) ([]*DeviceRecord, error) {
	rows, err := s.db.QueryContext(ctx, `
		SELECT device_id, ed25519_pubkey, fingerprint,
		       nickname_ct, nickname_algo,
		       platform, agent_major, agent_minor, agent_patch,
		       registered_at, last_seen_at, revoked, revoked_at
		FROM devices ORDER BY registered_at DESC
	`)
	if err != nil {
		return nil, fmt.Errorf("regstore: list devices: %w", err)
	}
	defer rows.Close()
	var out []*DeviceRecord
	for rows.Next() {
		rec := &DeviceRecord{}
		var deviceIDRaw, pubKeyRaw, fingerprintRaw []byte
		var nicknameCT []byte
		var nicknameAlgo int32
		var platform sql.NullString
		var revokedInt int
		var revokedAt sql.NullInt64
		if err := rows.Scan(
			&deviceIDRaw, &pubKeyRaw, &fingerprintRaw,
			&nicknameCT, &nicknameAlgo,
			&platform, &rec.AgentMajor, &rec.AgentMinor, &rec.AgentPatch,
			&rec.RegisteredAt, &rec.LastSeenAt, &revokedInt, &revokedAt,
		); err != nil {
			return nil, fmt.Errorf("regstore: scan device row: %w", err)
		}
		copy(rec.DeviceID[:], deviceIDRaw)
		copy(rec.PubKey[:], pubKeyRaw)
		copy(rec.Fingerprint[:], fingerprintRaw)
		rec.NicknameCT = nicknameCT
		rec.NicknameAlgo = nicknameAlgo
		if platform.Valid {
			rec.Platform = platform.String
		}
		rec.Revoked = revokedInt == 1
		rec.RevokedAt = revokedAt
		out = append(out, rec)
	}
	return out, rows.Err()
}
