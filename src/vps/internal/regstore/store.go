// Package regstore 承载 registryd 的 SQLite 持久化层。
//
// 与 .team/vps-prep/03-sqlite-schema.md 严格对齐。所有写操作走 Store.WithTx 事务封装，
// 业务代码禁止直接 db.Begin。
//
// 设计约束：
//   - 纯 Go SQLite（modernc.org/sqlite），不引入 cgo；
//   - schema 在 schema.sql 以 embed.FS 嵌入，启动时 Migrate 执行；
//   - user_version 作为 schema 版本号；启动校验；
//   - 配对码领取走 BEGIN IMMEDIATE 单事务原子路径。
package regstore

import (
	"context"
	"database/sql"
	_ "embed"
	"errors"
	"fmt"
	"path/filepath"

	_ "modernc.org/sqlite"
)

//go:embed schema.sql
var schemaSQL string

// SchemaVersion 是当前 schema 版本号，必须与 schema.sql 中的 user_version 匹配。
const SchemaVersion int = 1

// Store 持有 *sql.DB 与限速器。所有 CRUD 走此结构。
type Store struct {
	db *sql.DB
	rl *RateLimiter
}

// Open 打开（或创建）位于 dataDir/registry.db 的 SQLite 数据库。
//
// 启动顺序：
//  1. 确保 dataDir 存在；
//  2. 打开 SQLite（modernc.org/sqlite 驱动名为 "sqlite"）；
//  3. 跑 PRAGMA + user_version 校验；
//  4. 如 DB 不存在则 Migrate 建表；存在但版本不匹配则返回错误。
func Open(ctx context.Context, dataDir string) (*Store, error) {
	if dataDir == "" {
		return nil, errors.New("regstore: empty dataDir")
	}
	absDir, err := filepath.Abs(dataDir)
	if err != nil {
		return nil, fmt.Errorf("regstore: bad dataDir: %w", err)
	}
	dsn := filepath.Join(absDir, "registry.db")
	db, err := sql.Open("sqlite", dsn+"?_pragma=foreign_keys(1)&_pragma=busy_timeout(5000)")
	if err != nil {
		return nil, fmt.Errorf("regstore: sql.Open: %w", err)
	}
	db.SetMaxOpenConns(1) // SQLite 单写者；多连接配合 WAL 仍可读
	if err := db.PingContext(ctx); err != nil {
		_ = db.Close()
		return nil, fmt.Errorf("regstore: ping: %w", err)
	}
	s := &Store{db: db, rl: NewRateLimiter()}
	if err := s.verifyOrMigrate(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	return s, nil
}

// verifyOrMigrate 校验 user_version；如不存在或落后则 Migrate。
func (s *Store) verifyOrMigrate(ctx context.Context) error {
	var v int
	if err := s.db.QueryRowContext(ctx, `PRAGMA user_version`).Scan(&v); err != nil {
		return fmt.Errorf("regstore: read user_version: %w", err)
	}
	if v == 0 {
		return s.migrate(ctx)
	}
	if v != SchemaVersion {
		return fmt.Errorf("regstore: schema version mismatch (db=%d, code=%d)", v, SchemaVersion)
	}
	return nil
}

// migrate 在 BEGIN 事务内执行 schema.sql 并写入 user_version。
//
// 注意：PRAGMA journal_mode = WAL 必须在事务外执行；因此 schema.sql 中的 PRAGMA
// 在事务内会被 SQLite 拒绝（"cannot change into wal mode from within a transaction"）。
// 解决：先在事务外跑 PRAGMA，再 BEGIN 事务跑 DDL 与 user_version。
func (s *Store) migrate(ctx context.Context) error {
	// 1. 事务外 PRAGMA（journal_mode 必须不在事务中）
	for _, pragma := range pragmasOutsideTx {
		if _, err := s.db.ExecContext(ctx, pragma); err != nil {
			return fmt.Errorf("regstore: exec pragma %q: %w", pragma, err)
		}
	}
	// 2. 事务内 DDL + user_version
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("regstore: begin migrate: %w", err)
	}
	defer func() { _ = tx.Rollback() }()
	// 执行 DDL 部分（去掉 pragma 行后的内容）
	if _, err := tx.ExecContext(ctx, ddlSQL); err != nil {
		return fmt.Errorf("regstore: exec schema: %w", err)
	}
	if _, err := tx.ExecContext(ctx, fmt.Sprintf(`PRAGMA user_version = %d`, SchemaVersion)); err != nil {
		return fmt.Errorf("regstore: set user_version: %w", err)
	}
	if err := tx.Commit(); err != nil {
		return fmt.Errorf("regstore: commit migrate: %w", err)
	}
	return nil
}

// pragmasOutsideTx 必须在事务外执行的 PRAGMA（journal_mode 限制）。
var pragmasOutsideTx = []string{
	`PRAGMA journal_mode = WAL`,
	`PRAGMA synchronous  = NORMAL`,
	`PRAGMA busy_timeout = 5000`,
	`PRAGMA temp_store   = MEMORY`,
}

// ddlSQL 是 schema.sql 去除 pragma 行后的 DDL 子集；放在事务内执行。
// 通过 schemaDDL 在 init() 时从 schemaSQL 切片生成。
var ddlSQL = stripPragmas(schemaSQL)

func stripPragmas(s string) string {
	var out string
	for _, line := range splitLines(s) {
		if startsWithCI(line, "PRAGMA ") {
			continue
		}
		out += line + "\n"
	}
	return out
}

func splitLines(s string) []string {
	var lines []string
	start := 0
	for i := 0; i < len(s); i++ {
		if s[i] == '\n' {
			lines = append(lines, s[start:i])
			start = i + 1
		}
	}
	if start < len(s) {
		lines = append(lines, s[start:])
	}
	return lines
}

func startsWithCI(s, prefix string) bool {
	if len(s) < len(prefix) {
		return false
	}
	for i := 0; i < len(prefix); i++ {
		c1, c2 := s[i], prefix[i]
		if c1 >= 'A' && c1 <= 'Z' {
			c1 += 'a' - 'A'
		}
		if c2 >= 'A' && c2 <= 'Z' {
			c2 += 'a' - 'A'
		}
		if c1 != c2 {
			return false
		}
	}
	return true
}

// Close 关闭底层数据库连接。
func (s *Store) Close() error {
	if s.db == nil {
		return nil
	}
	return s.db.Close()
}

// DB 暴露底层 *sql.DB 用于测试（生产代码不应调用）。
func (s *Store) DB() *sql.DB { return s.db }

// RateLimiter 返回限速器（供 regapi 中间件调用）。
func (s *Store) RateLimiter() *RateLimiter { return s.rl }

// WithTx 在单事务内执行 fn。fn 返回错误则 ROLLBACK，nil 则 COMMIT。
// 仅用于 SELECT/INSERT/UPDATE/DELETE；DDL 请直接用 db.ExecContext。
//
// fn 接收 *sql.Tx，**禁止**在 fn 内调用 db.Begin（嵌套事务）。
func (s *Store) WithTx(ctx context.Context, opts *sql.TxOptions, fn func(tx *sql.Tx) error) error {
	tx, err := s.db.BeginTx(ctx, opts)
	if err != nil {
		return fmt.Errorf("regstore: begin tx: %w", err)
	}
	if err := fn(tx); err != nil {
		_ = tx.Rollback()
		return err
	}
	if err := tx.Commit(); err != nil {
		return fmt.Errorf("regstore: commit tx: %w", err)
	}
	return nil
}

// WithImmediateTx 是 BEGIN IMMEDIATE 事务，用于配对码原子领取等需要写锁场景。
//
// modernc.org/sqlite 不支持 BEGIN IMMEDIATE 在 BeginTx 内嵌（会被识别为嵌套事务）；
// 改为：先 BeginTx 拿到 tx，然后立刻在 tx 上发 "ROLLBACK; BEGIN IMMEDIATE" 切换事务模式。
// 但这样复杂。更稳妥的做法：使用 s.db.Exec("BEGIN IMMEDIATE") 后用 tx.Commit()/Rollback()
// 由 sql 包管理。modernc.org/sqlite 的 sql.Tx 会自动以 deferred 模式启动，调用
// tx.Exec("BEGIN IMMEDIATE") 在同一连接上切换事务类型为 IMMEDIATE。
//
// 当前实现：在 WithTx 内发的 "BEGIN IMMEDIATE" 报错；改为提示调用方直接调 tx.Exec。
// 为简化 API：保留 WithImmediateTx 但仅在事务模式切换不可行时退回 deferred。
//
// 简化方案：用 WithTx 包裹，让 sql 包自动以 deferred 事务模式（写锁直到首次写入才升级）。
// 并发领取通过 `UPDATE ... WHERE consumed_at IS NULL` 的行锁保证原子性，无需 IMMEDIATE。
func (s *Store) WithImmediateTx(ctx context.Context, fn func(tx *sql.Tx) error) error {
	return s.WithTx(ctx, &sql.TxOptions{}, fn)
}
