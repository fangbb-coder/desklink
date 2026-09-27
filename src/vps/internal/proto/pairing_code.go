package proto

import (
	"errors"
	"fmt"
)

// PairingCodeLength 是配对码长度（6 位十进制数字）。
const PairingCodeLength = 6

// ErrBadPairingCode 当码格式不符合 6 位十进制时返回。
var ErrBadPairingCode = errors.New("proto: bad pairing code")

// PairingCode 表示 6 位十进制数字配对码。
type PairingCode [PairingCodeLength]byte

// ParsePairingCode 接受任意字符串，校验是否为 6 位十进制。
// 校验通过返回 PairingCode。
func ParsePairingCode(s string) (PairingCode, error) {
	var c PairingCode
	if len(s) != PairingCodeLength {
		return c, fmt.Errorf("%w: got length %d", ErrBadPairingCode, len(s))
	}
	for i := 0; i < PairingCodeLength; i++ {
		ch := s[i]
		if ch < '0' || ch > '9' {
			return c, fmt.Errorf("%w: non-digit at %d", ErrBadPairingCode, i)
		}
		c[i] = ch
	}
	return c, nil
}

// String 返回 6 位字符串形式。
func (c PairingCode) String() string {
	return string(c[:])
}

// IsSentinel 报告码是否为保留 sentinel (000000)。
func (c PairingCode) IsSentinel() bool {
	for i := 0; i < PairingCodeLength; i++ {
		if c[i] != '0' {
			return false
		}
	}
	return true
}

// RandomPairingCode 返回随机生成的 6 位码；如生成到 sentinel (000000) 则重试一次。
// crypto/rand 错误向上层冒泡（不静默）。
//
// 调用方负责重试上限与限速控制。
func RandomPairingCode(rand func(n int) ([]byte, error)) (PairingCode, error) {
	for attempt := 0; attempt < 2; attempt++ {
		buf, err := rand(PairingCodeLength)
		if err != nil {
			return PairingCode{}, err
		}
		var c PairingCode
		for i := 0; i < PairingCodeLength; i++ {
			c[i] = '0' + (buf[i] % 10)
		}
		if !c.IsSentinel() {
			return c, nil
		}
	}
	return PairingCode{}, errors.New("proto: RandomPairingCode failed after retries")
}
