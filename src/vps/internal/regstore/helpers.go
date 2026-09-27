package regstore

import (
	"github.com/google/uuid"
)

// newPairUUIDForTest 是 pair_uuid 的工厂函数；当前实现直接走 uuid.NewString()。
// 拆为独立函数便于后续在测试中替换为确定性 seed。
var newPairUUIDForTest = func() string {
	return uuid.NewString()
}
