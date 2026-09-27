using DeskLink.DesktopAgent.Capture;
using DeskLink.DesktopAgent.Quality;

namespace DeskLink.Agent.Tests;

/// <summary>
/// P9：自适应码率控制器。
///
/// 这组用例的价值在于把"降级顺序"和"不抖动"这两件事钉死：
///   - DESIGN 要求"先降帧率与码率，再降分辨率"，顺序错了会先糊画面；
///   - 自适应码率最典型的失败模式是抖动（刚升档立刻又降），因此迟滞带、
///     冷却时间、连续良好样本数都必须有明确断言。
/// </summary>
public class AdaptiveBitrateTests
{
    private static AdaptiveBitrateController.Options FastOptions() => new()
    {
        MaxBitrateBps = 8_000_000,
        MaxFps = 30,
        MinFps = 10,
        MinBitrateBps = 500_000,
        MinScale = 0.5,
        MinChangeInterval = TimeSpan.FromMilliseconds(100),
        RecoverSamples = 3,
    };

    private static QualityFeedback Bad() => new(RttMs: 900, LossPercent: 20, EncodeMsPerFrame: 50, QueueDepth: 30, InputPending: false);
    private static QualityFeedback Neutral() => new(RttMs: 150, LossPercent: 1.5, EncodeMsPerFrame: 15, QueueDepth: 3, InputPending: false);
    private static QualityFeedback Good() => new(RttMs: 10, LossPercent: 0, EncodeMsPerFrame: 2, QueueDepth: 0, InputPending: false);

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    // ── 阶梯不变式 ──────────────────────────────────────────────────────────

    [Fact]
    public void Ladder_IsMonotoneNonIncreasing_OnAllThreeAxes()
    {
        for (var i = 1; i < AdaptiveBitrateController.Ladder.Length; i++)
        {
            var prev = AdaptiveBitrateController.Ladder[i - 1];
            var cur = AdaptiveBitrateController.Ladder[i];
            Assert.True(cur.Bitrate <= prev.Bitrate, $"档位 {i} 的码率不应高于上一档");
            Assert.True(cur.Fps <= prev.Fps, $"档位 {i} 的帧率不应高于上一档");
            Assert.True(cur.Scale <= prev.Scale, $"档位 {i} 的缩放不应高于上一档");
        }
    }

    [Fact]
    public void Ladder_LevelZeroIsFullQuality()
    {
        var l0 = AdaptiveBitrateController.Ladder[0];
        Assert.Equal(1.0, l0.Bitrate);
        Assert.Equal(1.0, l0.Fps);
        Assert.Equal(1.0, l0.Scale);
    }

    /// <summary>
    /// DESIGN 的核心约束：**分辨率必须最后才降**。
    /// 断言"任何缩放小于 1 的档位，其码率与帧率都已经低于满档"。
    /// </summary>
    [Fact]
    public void Ladder_DropsResolutionOnlyAfterBitrateAndFps()
    {
        var full = AdaptiveBitrateController.Ladder[0];
        for (var i = 1; i < AdaptiveBitrateController.Ladder.Length; i++)
        {
            var cur = AdaptiveBitrateController.Ladder[i];
            if (cur.Scale >= 1.0) continue;

            Assert.True(cur.Bitrate < full.Bitrate,
                $"档位 {i} 降了分辨率，但码率仍是满档 —— 违反“先码率后分辨率”");
            Assert.True(cur.Fps < full.Fps,
                $"档位 {i} 降了分辨率，但帧率仍是满档 —— 违反“先帧率后分辨率”");
        }
    }

    [Fact]
    public void ForLevel_ClampsOutOfRangeLevels()
    {
        var opt = FastOptions();
        var below = AdaptiveBitrateController.ForLevel(-5, opt);
        var above = AdaptiveBitrateController.ForLevel(999, opt);
        Assert.Equal(AdaptiveBitrateController.ForLevel(0, opt), below);
        Assert.Equal(AdaptiveBitrateController.ForLevel(AdaptiveBitrateController.MaxLevel, opt), above);
    }

    [Fact]
    public void ForLevel_RespectsConfiguredFloors()
    {
        var opt = FastOptions() with { MinBitrateBps = 2_000_000, MinFps = 25, MinScale = 0.9 };
        var worst = AdaptiveBitrateController.ForLevel(AdaptiveBitrateController.MaxLevel, opt);
        Assert.True(worst.BitrateBps >= 2_000_000, $"码率不得低于下限，实际 {worst.BitrateBps}");
        Assert.True(worst.Fps >= 25, $"帧率不得低于下限，实际 {worst.Fps}");
        Assert.True(worst.Scale >= 0.9, $"缩放不得低于下限，实际 {worst.Scale}");
    }

    [Fact]
    public void ForLevel_LevelZeroEqualsConfiguredMaximums()
    {
        var opt = FastOptions();
        var best = AdaptiveBitrateController.ForLevel(0, opt);
        Assert.Equal(8_000_000, best.BitrateBps);
        Assert.Equal(30, best.Fps);
        Assert.Equal(1.0, best.Scale);
    }

    // ── 控制器行为 ──────────────────────────────────────────────────────────

    [Fact]
    public void NewController_StartsAtFullQuality()
    {
        var c = new AdaptiveBitrateController(FastOptions());
        Assert.Equal(0, c.Level);
        Assert.False(c.Degraded);
        Assert.Equal(8_000_000, c.Current.BitrateBps);
    }

    [Fact]
    public void Pressure_DoesNotDegradeBeforeCooldownElapsed()
    {
        var c = new AdaptiveBitrateController(FastOptions());
        // 冷却 100ms；只过 50ms 不该降档。
        c.Update(Bad(), TimeSpan.FromMilliseconds(50));
        Assert.Equal(0, c.Level);
    }

    [Fact]
    public void Pressure_DegradesOneStepPerCooldown()
    {
        var c = new AdaptiveBitrateController(FastOptions());
        c.Update(Bad(), Tick);
        Assert.Equal(1, c.Level);

        // 立刻再来一次（未过冷却）→ 不动
        c.Update(Bad(), TimeSpan.FromMilliseconds(10));
        Assert.Equal(1, c.Level);

        c.Update(Bad(), Tick);
        Assert.Equal(2, c.Level);
    }

    [Fact]
    public void Pressure_DegradesAtMostToMaxLevel()
    {
        var c = new AdaptiveBitrateController(FastOptions());
        for (var i = 0; i < AdaptiveBitrateController.MaxLevel + 5; i++)
        {
            c.Update(Bad(), Tick);
        }
        Assert.Equal(AdaptiveBitrateController.MaxLevel, c.Level);
        Assert.True(c.Degraded);

        // 再压也不会越界
        c.Update(Bad(), Tick);
        Assert.Equal(AdaptiveBitrateController.MaxLevel, c.Level);
    }

    [Fact]
    public void InputPending_IsTreatedAsPressure()
    {
        var c = new AdaptiveBitrateController(FastOptions());
        var withInput = new QualityFeedback(RttMs: 5, LossPercent: 0, EncodeMsPerFrame: 1, QueueDepth: 0, InputPending: true);
        c.Update(withInput, Tick);
        Assert.Equal(1, c.Level);
    }

    [Fact]
    public void EncodeOverBudget_IsTreatedAsPressure()
    {
        var c = new AdaptiveBitrateController(FastOptions());
        var slowEncoder = new QualityFeedback(RttMs: 5, LossPercent: 0, EncodeMsPerFrame: 25, QueueDepth: 0, InputPending: false);
        c.Update(slowEncoder, Tick);
        Assert.Equal(1, c.Level);
    }

    [Fact]
    public void Recovery_RequiresConsecutiveGoodSamples()
    {
        var opt = FastOptions(); // RecoverSamples = 3
        var c = new AdaptiveBitrateController(opt);
        c.Update(Bad(), Tick);   // level 1
        Assert.Equal(1, c.Level);

        // 连续 2 次良好 → 还差一个样本，不该升档
        c.Update(Good(), Tick);
        c.Update(Good(), Tick);
        Assert.Equal(1, c.Level);

        c.Update(Good(), Tick);
        Assert.Equal(0, c.Level);
    }

    [Fact]
    public void Recovery_GoodStreakIsResetByPressure()
    {
        var c = new AdaptiveBitrateController(FastOptions());
        c.Update(Bad(), Tick);
        c.Update(Bad(), Tick);
        Assert.Equal(2, c.Level);

        // 攒两个良好样本后突然变差 → 之前的进度必须清零
        c.Update(Good(), Tick);
        c.Update(Good(), Tick);
        c.Update(Bad(), Tick); // 压力：清零 + 过冷却 → 降到 3
        Assert.Equal(3, c.Level);

        // 只给 2 个良好样本（不足 3）→ 不升档
        c.Update(Good(), Tick);
        c.Update(Good(), Tick);
        Assert.Equal(3, c.Level);
    }

    /// <summary>
    /// 迟滞带：既不坏也不好的样本不应把"可以升档"的连续计数清零，
    /// 否则网络在中间区间轻微波动时就永远升不回去。
    /// </summary>
    [Fact]
    public void NeutralSamples_DoNotResetGoodStreak()
    {
        var c = new AdaptiveBitrateController(FastOptions());
        c.Update(Bad(), Tick);          // level 1
        c.Update(Good(), Tick);         // streak 1
        c.Update(Neutral(), Tick);      // 中性：streak 保持 1
        c.Update(Good(), Tick);         // streak 2
        c.Update(Good(), Tick);         // streak 3 → 升档
        Assert.Equal(0, c.Level);
    }

    [Fact]
    public void NeutralSamples_DoNotTriggerDegrade()
    {
        var c = new AdaptiveBitrateController(FastOptions());
        for (var i = 0; i < 10; i++) c.Update(Neutral(), Tick);
        Assert.Equal(0, c.Level);
    }

    [Fact]
    public void DegradeThenRecover_IsSymmetric()
    {
        var c = new AdaptiveBitrateController(FastOptions());
        for (var i = 0; i < 3; i++) c.Update(Bad(), Tick);
        Assert.Equal(3, c.Level);

        for (var i = 0; i < 3; i++) c.Update(Good(), Tick);  // 升 1
        Assert.Equal(2, c.Level);
        for (var i = 0; i < 3; i++) c.Update(Good(), Tick);  // 升 1
        Assert.Equal(1, c.Level);
    }

    [Fact]
    public void Reset_ReturnsToFullQuality()
    {
        var c = new AdaptiveBitrateController(FastOptions());
        for (var i = 0; i < 3; i++) c.Update(Bad(), Tick);
        Assert.True(c.Level > 0);

        c.Reset();
        Assert.Equal(0, c.Level);
        Assert.False(c.Degraded);
        Assert.Equal(8_000_000, c.Current.BitrateBps);
    }

    [Fact]
    public void Current_AlwaysMatchesLevel()
    {
        var opt = FastOptions();
        var c = new AdaptiveBitrateController(opt);
        for (var i = 0; i < AdaptiveBitrateController.MaxLevel + 2; i++)
        {
            c.Update(Bad(), Tick);
            Assert.Equal(AdaptiveBitrateController.ForLevel(c.Level, opt), c.Current);
        }
    }

    [Fact]
    public void NegativeElapsed_IsTreatedAsZero()
    {
        var c = new AdaptiveBitrateController(FastOptions());
        c.Update(Bad(), TimeSpan.FromMilliseconds(-500));
        Assert.Equal(0, c.Level);
    }
}
