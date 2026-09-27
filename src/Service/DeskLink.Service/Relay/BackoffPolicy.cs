// 指数退避策略（DESIGN.md 决策"重连退避"）。
//
// 行为：
//   - base 起步；每次失败指数翻倍，直到 maxMs。
//   - 加 ±20% 抖动，避免雪崩重连。
//   - 成功调用 Reset() 重置为 base。
//
// P4 验证点：
//   - BackoffPolicyTests：序列 [1s, 2s, 4s, 8s, 16s, 30s, 30s, ...] 落入 [0.8x, 1.2x] 抖动范围。
using System.Security.Cryptography;

namespace DeskLink.Service.Relay;

public sealed class BackoffPolicy
{
    private readonly int _baseMs;
    private readonly int _maxMs;
    private int _attempt;

    public BackoffPolicy(int baseMs = 1_000, int maxMs = 30_000)
    {
        if (baseMs <= 0) throw new ArgumentOutOfRangeException(nameof(baseMs));
        if (maxMs < baseMs) throw new ArgumentOutOfRangeException(nameof(maxMs));
        _baseMs = baseMs;
        _maxMs = maxMs;
    }

    public int Attempt => _attempt;

    /// <summary>
    /// 下一段等待时长（毫秒）。调用后 attempt 递增。
    /// </summary>
    public int NextDelayMs()
    {
        // 2^(attempt) * base；clamp 到 maxMs
        long shift = _attempt;
        if (shift > 20) shift = 20; // 防溢出
        var exp = (long)_baseMs << (int)shift;
        var raw = exp > _maxMs ? _maxMs : exp;

        // ±20% 抖动
        var jitter = (int)(raw * 0.2);
        var delta = jitter == 0 ? 0 : RandomNumberGenerator.GetInt32(-jitter, jitter + 1);
        var delay = raw + delta;
        if (delay < 0) delay = 0;
        if (delay > _maxMs) delay = _maxMs;
        _attempt++;
        return (int)delay;
    }

    public void Reset()
    {
        _attempt = 0;
    }
}
