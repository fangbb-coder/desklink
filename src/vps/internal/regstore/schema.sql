-- schema.sql —— embed.FS 入口，由 regstore.Migrate 在启动时执行。
-- 与 .team/vps-prep/03-sqlite-schema.md §7 DDL 严格对齐。
--
-- 注意：PRAGMA 语句不能在事务内执行（SQLite 限制），所以它们由 regstore.migrate
-- 在事务外显式执行（pragmasOutsideTx）；schema.sql 仅承载 DDL。

CREATE TABLE IF NOT EXISTS devices (
    device_id        BLOB(32) PRIMARY KEY,
    ed25519_pubkey   BLOB(32) NOT NULL UNIQUE,
    fingerprint      BLOB(32) NOT NULL,
    nickname_ct      BLOB,
    nickname_algo    INTEGER NOT NULL DEFAULT 0,
    platform         TEXT,
    agent_major      INTEGER NOT NULL DEFAULT 0,
    agent_minor      INTEGER NOT NULL DEFAULT 0,
    agent_patch      INTEGER NOT NULL DEFAULT 0,
    registered_at    INTEGER NOT NULL,
    last_seen_at     INTEGER NOT NULL,
    revoked          INTEGER NOT NULL DEFAULT 0,
    revoked_at       INTEGER,
    CHECK (length(device_id)        = 32),
    CHECK (length(ed25519_pubkey)   = 32),
    CHECK (length(fingerprint)      = 32),
    CHECK (length(platform)         <= 32),
    CHECK (agent_major BETWEEN 0 AND 255),
    CHECK (agent_minor BETWEEN 0 AND 255),
    CHECK (agent_patch BETWEEN 0 AND 255),
    CHECK (revoked IN (0,1))
);
CREATE INDEX IF NOT EXISTS idx_devices_last_seen ON devices(last_seen_at);
CREATE INDEX IF NOT EXISTS idx_devices_revoked    ON devices(revoked);

CREATE TABLE IF NOT EXISTS pairs (
    pair_id         BLOB(32) PRIMARY KEY,
    pair_uuid       TEXT    NOT NULL UNIQUE,
    device_a        BLOB(32) NOT NULL,
    device_b        BLOB(32) NOT NULL,
    fingerprint_a   BLOB(32) NOT NULL,
    fingerprint_b   BLOB(32) NOT NULL,
    created_at      INTEGER NOT NULL,
    revoked         INTEGER NOT NULL DEFAULT 0,
    revoked_at      INTEGER,
    revoked_by      BLOB(32),
    FOREIGN KEY (device_a) REFERENCES devices(device_id),
    FOREIGN KEY (device_b) REFERENCES devices(device_id),
    CHECK (device_a < device_b),
    CHECK (length(pair_id)        = 32),
    CHECK (length(device_a)       = 32),
    CHECK (length(device_b)       = 32),
    CHECK (length(fingerprint_a)  = 32),
    CHECK (length(fingerprint_b)  = 32),
    CHECK (revoked IN (0,1)),
    CHECK ((revoked = 0 AND revoked_at IS NULL AND revoked_by IS NULL)
           OR (revoked = 1 AND revoked_at IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_pairs_pair_uuid
    ON pairs(pair_uuid);
CREATE UNIQUE INDEX IF NOT EXISTS idx_pairs_pair_unique
    ON pairs(device_a, device_b) WHERE revoked = 0;
CREATE INDEX IF NOT EXISTS idx_pairs_device_a ON pairs(device_a) WHERE revoked = 0;
CREATE INDEX IF NOT EXISTS idx_pairs_device_b ON pairs(device_b) WHERE revoked = 0;

CREATE TABLE IF NOT EXISTS revocations (
    revocation_id   INTEGER PRIMARY KEY AUTOINCREMENT,
    pair_uuid       TEXT    NOT NULL,
    pair_id         BLOB(32) NOT NULL,
    device_a        BLOB(32) NOT NULL,
    device_b        BLOB(32) NOT NULL,
    revoked_at      INTEGER NOT NULL,
    revoked_by      BLOB(32),
    reason          TEXT NOT NULL,
    pushed_at       INTEGER,
    CHECK (length(pair_id)  = 32),
    CHECK (length(device_a) = 32),
    CHECK (length(device_b) = 32),
    CHECK (reason IN ('user_revoked', 'duplicate', 'key_rotation'))
);
CREATE INDEX IF NOT EXISTS idx_revocations_pair ON revocations(pair_uuid);
CREATE INDEX IF NOT EXISTS idx_revocations_unpushed
    ON revocations(revocation_id) WHERE pushed_at IS NULL;

CREATE TABLE IF NOT EXISTS pairing_codes (
    code_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id            BLOB(32) NOT NULL,
    code_hash            TEXT NOT NULL UNIQUE,
    created_at           INTEGER NOT NULL,
    expires_at           INTEGER NOT NULL,
    consumed_at          INTEGER,
    consumed_by_device   BLOB(32),
    FOREIGN KEY (device_id) REFERENCES devices(device_id),
    CHECK (length(device_id) = 32),
    CHECK (expires_at > created_at),
    CHECK (length(code_hash) = 60),
    CHECK ((consumed_at IS NULL AND consumed_by_device IS NULL)
           OR (consumed_at IS NOT NULL AND consumed_by_device IS NOT NULL
               AND length(consumed_by_device) = 32))
);
CREATE INDEX IF NOT EXISTS idx_pairing_codes_device ON pairing_codes(device_id);
CREATE INDEX IF NOT EXISTS idx_pairing_codes_active
    ON pairing_codes(created_at) WHERE consumed_at IS NULL;

CREATE TABLE IF NOT EXISTS device_key_history (
    change_id        INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id        BLOB(32) NOT NULL,
    old_pubkey       BLOB(32) NOT NULL,
    old_fingerprint  BLOB(32) NOT NULL,
    new_pubkey       BLOB(32) NOT NULL,
    new_fingerprint  BLOB(32) NOT NULL,
    changed_at       INTEGER NOT NULL,
    sig              BLOB(64) NOT NULL,
    FOREIGN KEY (device_id) REFERENCES devices(device_id),
    CHECK (length(device_id)         = 32),
    CHECK (length(old_pubkey)        = 32),
    CHECK (length(new_pubkey)        = 32),
    CHECK (length(old_fingerprint)   = 32),
    CHECK (length(new_fingerprint)   = 32),
    CHECK (length(sig)               = 64),
    CHECK (old_pubkey <> new_pubkey)
);
CREATE INDEX IF NOT EXISTS idx_dkh_device ON device_key_history(device_id, changed_at);
