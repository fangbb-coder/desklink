package regstore

import (
	"sync"
	"time"

	"golang.org/x/time/rate"
)

// RateLimiter 内存限速器，按 (clientAddr, kind) 二元组分桶。
// 与 .team/vps-prep/01-proto-spec.md §7 对齐：
//   - /v1/pairing-codes/redeem：同 IP 同码 5 次/分钟；同 IP 任何码 30 次/分钟；
//   - /v1/devices/verify-challenge：同 client 30 次/分钟。
//
// 实现：使用 golang.org/x/time/rate.Limiter；桶 key = clientAddr|kind|code_or_empty。
type RateLimiter struct {
	mu       sync.Mutex
	buckets  map[string]*bucketEntry
	limiters map[string]*rate.Limiter
	now      func() time.Time
}

type bucketEntry struct {
	limiter  *rate.Limiter
	lastSeen time.Time
}

// LimitKind 限速维度。
type LimitKind string

const (
	LimitRedeemByCode LimitKind = "redeem-by-code" // 同 IP 同码
	LimitRedeemByIP   LimitKind = "redeem-by-ip"   // 同 IP 任何码
	LimitChallenge    LimitKind = "challenge"      // 设备挑战
)

// NewRateLimiter 构造默认配置的限速器。
func NewRateLimiter() *RateLimiter {
	return &RateLimiter{
		buckets:  make(map[string]*bucketEntry),
		limiters: make(map[string]*rate.Limiter),
		now:      time.Now,
	}
}

// limitFor 按 kind 返回 r/s 与 burst：
//   - 同码 5/min ≈ 1/12 r/s burst=5；
//   - 同 IP 30/min ≈ 0.5 r/s burst=30；
//   - 挑战 30/min ≈ 0.5 r/s burst=30。
func limiterFor(kind LimitKind, clientAddr, code string) (key string, r rate.Limit, burst int) {
	switch kind {
	case LimitRedeemByCode:
		return clientAddr + "|" + string(kind) + "|" + code, rate.Every(12 * time.Second), 5
	case LimitRedeemByIP:
		return clientAddr + "|" + string(kind) + "|", rate.Every(2 * time.Second), 30
	case LimitChallenge:
		return clientAddr + "|" + string(kind) + "|", rate.Every(2 * time.Second), 30
	default:
		return clientAddr + "|" + string(kind), rate.Every(time.Second), 1
	}
}

// Allow 报告是否允许通过限速；false 表示触发限速。
func (r *RateLimiter) Allow(clientAddr string, kind LimitKind, code string) bool {
	if r == nil {
		return true
	}
	key, lim, burst := limiterFor(kind, clientAddr, code)
	r.mu.Lock()
	defer r.mu.Unlock()
	l, ok := r.limiters[key]
	if !ok {
		l = rate.NewLimiter(lim, burst)
		r.limiters[key] = l
	}
	now := r.now()
	if e, ok := r.buckets[key]; ok {
		e.lastSeen = now
	} else {
		r.buckets[key] = &bucketEntry{limiter: l, lastSeen: now}
	}
	// 周期性清理早于 5 分钟的桶
	if len(r.buckets) > 1024 {
		r.evictLocked(now.Add(-5 * time.Minute))
	}
	return l.AllowN(now, 1)
}

// Cleanup 主动清理过期桶（建议由 ticker 调用）。
func (r *RateLimiter) Cleanup() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.evictLocked(r.now().Add(-5 * time.Minute))
}

func (r *RateLimiter) evictLocked(threshold time.Time) int {
	n := 0
	for k, e := range r.buckets {
		if e.lastSeen.Before(threshold) {
			delete(r.buckets, k)
			delete(r.limiters, k)
			n++
		}
	}
	return n
}

// Snapshot 仅用于测试：返回当前桶数量。
func (r *RateLimiter) Snapshot() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.buckets)
}
