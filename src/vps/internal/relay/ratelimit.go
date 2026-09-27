// Package relay 子文件 ratelimit：relay 侧按 IP 的限速。
//
// 与 registry 的 regstore.RateLimiter 职责不同：registry 限的是"配对码领取/挑战
// 转发"，relay 限的是"连接建立尝试"，两者在网络路径的不同位置，互不替代。
//
// 三类桶：
//   - conn：每 IP 并发连接上限（长连接，用计数而非速率）。
//   - challenge：每 IP 每分钟的 Hello 尝试（防暴力枚举 device_id）。
package relay

import (
	"sync"
	"time"
)

// IPLimiter 是 relay 侧 IP 限速器。
type IPLimiter struct {
	mu        sync.Mutex
	maxConn   int
	connCount map[string]int
	challenge map[string]*window
	lastSweep time.Time
	now       func() time.Time
}

// window 是固定窗口计数器。
type window struct {
	start time.Time
	count int
}

// challengePerMinute 是每 IP 每分钟允许的 Hello 尝试数。
const challengePerMinute = 60

// NewIPLimiter 构造限速器；maxConn 为每 IP 并发连接上限。
func NewIPLimiter(maxConn int) *IPLimiter {
	if maxConn <= 0 {
		maxConn = 64
	}
	return &IPLimiter{
		maxConn:   maxConn,
		connCount: make(map[string]int),
		challenge: make(map[string]*window),
		now:       time.Now,
	}
}

// Allow 尝试占用一个连接名额；返回 false 表示超限（调用方应拒绝并关闭）。
func (l *IPLimiter) Allow(ip string) bool {
	if ip == "" {
		ip = "unknown"
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.connCount[ip] >= l.maxConn {
		return false
	}
	l.connCount[ip]++
	return true
}

// Release 归还一个连接名额。
func (l *IPLimiter) Release(ip string) {
	if ip == "" {
		ip = "unknown"
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	if n, ok := l.connCount[ip]; ok {
		if n <= 1 {
			delete(l.connCount, ip)
		} else {
			l.connCount[ip] = n - 1
		}
	}
}

// AllowChallenge 报告该 IP 是否还允许发起一次挑战尝试。
func (l *IPLimiter) AllowChallenge(ip string) bool {
	if ip == "" {
		ip = "unknown"
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.now()
	l.sweepLocked(now)
	w, ok := l.challenge[ip]
	if !ok || now.Sub(w.start) >= time.Minute {
		l.challenge[ip] = &window{start: now, count: 1}
		return true
	}
	if w.count >= challengePerMinute {
		return false
	}
	w.count++
	return true
}

// sweepLocked 每分钟最多一次删除已过期的挑战窗口。
//
// 为什么要清理：challenge 条目只在"同一 IP 再次出现"时被重建，
// 只出现过一次的 IP 的条目会永久驻留——面对持续扫描的公网地址会
// 缓慢吃内存。connCount 不需要清理（Release 归还到 0 时即删除）。
func (l *IPLimiter) sweepLocked(now time.Time) {
	if now.Sub(l.lastSweep) < time.Minute {
		return
	}
	l.lastSweep = now
	for ip, w := range l.challenge {
		if now.Sub(w.start) >= time.Minute {
			delete(l.challenge, ip)
		}
	}
}

// SetClock 注入测试时钟。
func (l *IPLimiter) SetClock(now func() time.Time) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.now = now
}

// ConnCount 返回当前某 IP 的占用数（测试/指标）。
func (l *IPLimiter) ConnCount(ip string) int {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.connCount[ip]
}
