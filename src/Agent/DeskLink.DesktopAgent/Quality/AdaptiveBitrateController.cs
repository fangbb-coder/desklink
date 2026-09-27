// 自适应码率控制器（P9）。
//
// 设计依据（DESIGN.md「性能和降级」）：
//   「网络变差时先降低帧率和码率，再降低分辨率，以保持输入控制响应。」
//
// 因此降级是**有顺序**的：先把码率压下来，再把帧率压下来，最后才动分辨率。
// 恢复时按相反顺序（先补回分辨率，再补帧率，最后补码率）——反过来会在网络刚
// 好转时立刻把带宽吃满，马上又变差，形成抖动。
//
// 实现要点（都是为了避免"抖动"这个自适应码率最典型的失败模式）：
//   1. 阶梯化而不是连续调节：离散档位让行为可预测、可单测，也避免编码器频繁重配。
//   2. 迟滞（hysteresis）：降级阈值（RttHigh/LossHigh）与恢复阈值（RttLow/LossLow）
//      之间留一条"中性带"，在中性带内既不升也不降。
//   3. 冷却时间（MinChangeInterval）：两次调节之间至少间隔这么久，防止一帧一调。
//   4. 恢复需要连续多次"良好"样本（RecoverSamples）：偶发一次好样本不足以升档。
//   5. 输入待处理（InputPending）也算压力信号：DESIGN 明确"以保持输入控制响应"，
//      所以输入积压时宁可牺牲画质。
//
// 本类**不碰编码器**，只产出"应该用什么参数"；重配编码器由管线负责
// （H.264 编码器重配涉及 MFT 类型协商，不能每帧做）。
namespace DeskLink.DesktopAgent.Quality;

/// <summary>一次质量反馈样本（由管线按固定节奏采集）。</summary>
public readonly record struct QualityFeedback(
    int RttMs,
    double LossPercent,
    double EncodeMsPerFrame,
    int QueueDepth,
    bool InputPending);

/// <summary>当前应使用的编码参数。</summary>
public readonly record struct QualitySettings(int BitrateBps, int Fps, double Scale)
{
    /// <summary>供状态条显示的简短描述。</summary>
    public string Label => $"{BitrateBps / 1000}kbps/{Fps}fps/{(int)Math.Round(Scale * 100)}%";
}

/// <summary>
/// 自适应码率控制器。
///
/// 阶梯是**相对比例**（相对于配置的上限），乘以 Options 里的上限得到实际值，
/// 这样同一套逻辑可以适配 720p 软编与 1080p 硬编两种配置。
/// </summary>
public sealed class AdaptiveBitrateController
{
    /// <summary>调节参数（全部可注入，便于单测精确控制阈值）。</summary>
    public sealed record Options
    {
        public int MaxBitrateBps { get; init; } = 8_000_000;
        public int MaxFps { get; init; } = 30;
        public int MinFps { get; init; } = 10;
        public int MinBitrateBps { get; init; } = 500_000;
        public double MinScale { get; init; } = 0.5;

        /// <summary>超过该 RTT 视为网络变差。</summary>
        public int RttHighMs { get; init; } = 250;

        /// <summary>低于该 RTT 才认为"良好"（与 RttHighMs 之间是中性带）。</summary>
        public int RttLowMs { get; init; } = 80;

        public double LossHighPercent { get; init; } = 3.0;
        public double LossLowPercent { get; init; } = 0.5;

        /// <summary>单帧编码耗时预算上限；超过说明编码跟不上（软编常见）。</summary>
        public double EncodeBudgetMs { get; init; } = 20.0;

        public int QueueHighDepth { get; init; } = 6;
        public int QueueLowDepth { get; init; } = 1;

        /// <summary>两次调节之间的最小间隔（冷却）。</summary>
        public TimeSpan MinChangeInterval { get; init; } = TimeSpan.FromSeconds(1);

        /// <summary>恢复需要的连续良好样本数。</summary>
        public int RecoverSamples { get; init; } = 8;
    }

    /// <summary>
    /// 质量阶梯（相对比例：码率、帧率、缩放）。
    ///
    /// 顺序即降级顺序：码率 100%→75%→50% → 帧率 → 分辨率。
    /// 单测会断言三条不变式（单调不增、分辨率最后才动、档位 0 为满画质），
    /// 因此调整这张表时若有违规会立刻变红。
    /// </summary>
    public static readonly (double Bitrate, double Fps, double Scale)[] Ladder =
    {
        (1.00, 1.00, 1.00),
        (0.75, 1.00, 1.00),
        (0.50, 1.00, 1.00),
        (0.50, 0.75, 1.00),
        (0.35, 0.60, 1.00),
        (0.35, 0.60, 0.75),
        (0.25, 0.50, 0.50),
    };

    private readonly Options _options;
    private readonly TimeSpan _minChangeInterval;
    // Update 会被两个线程并发调用（读循环的 MediaFlow 反馈 + 编码循环的统计
    // 节拍），而 Level/Current/_goodStreak/_sinceLastChange 是无锁的读-改-写
    // 状态机；QualitySettings 是 struct，并发读写还会撕裂。统一用一把锁串行化。
    private readonly object _gate = new();
    private TimeSpan _sinceLastChange;
    private int _goodStreak;
    private int _level;
    private QualitySettings _current;

    public AdaptiveBitrateController(Options? options = null)
    {
        _options = options ?? new Options();
        _minChangeInterval = _options.MinChangeInterval;
        _level = 0;
        _current = ForLevel(0, _options);
    }

    /// <summary>当前档位（0 = 满画质，越大越省）。</summary>
    public int Level { get { lock (_gate) return _level; } }

    /// <summary>当前应使用的编码参数。</summary>
    public QualitySettings Current { get { lock (_gate) return _current; } }

    /// <summary>是否处于"降级"状态（供状态条显示）。</summary>
    public bool Degraded => Level > 0;

    /// <summary>最高档位下标。</summary>
    public static int MaxLevel => Ladder.Length - 1;

    /// <summary>
    /// 计算某档位的实际参数（纯函数，便于单测）。
    /// 结果会夹紧到 Options 的下限（MinBitrateBps / MinFps / MinScale）。
    /// </summary>
    public static QualitySettings ForLevel(int level, Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var clamped = Math.Clamp(level, 0, MaxLevel);
        var (b, f, s) = Ladder[clamped];

        var bitrate = (int)Math.Max(options.MinBitrateBps, Math.Round(options.MaxBitrateBps * b));
        var fps = Math.Max(options.MinFps, (int)Math.Round(options.MaxFps * f));
        var scale = Math.Max(options.MinScale, s);
        return new QualitySettings(bitrate, fps, scale);
    }

    /// <summary>
    /// 喂入一个反馈样本，返回（可能更新后的）参数。
    /// </summary>
    /// <param name="feedback">本次采样。</param>
    /// <param name="elapsed">距上次调用的时间（用于冷却计时）。</param>
    public QualitySettings Update(QualityFeedback feedback, TimeSpan elapsed)
    {
        lock (_gate)
        {
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            _sinceLastChange += elapsed;

            var bad = IsUnderPressure(feedback);
            var good = IsGood(feedback);

            if (bad)
            {
                // 压力下立刻清空恢复计数：一旦变差就不该继续累计"可以升档"的证据。
                _goodStreak = 0;
            }
            else if (good)
            {
                _goodStreak++;
            }
            // 中性样本（既不坏也不好）保持 _goodStreak 不变 —— 这就是迟滞带的作用：
            // 网络在中间区间轻微波动时不会导致升档/降档来回跳。

            if (bad && _sinceLastChange >= _minChangeInterval && _level < MaxLevel)
            {
                _level++;
                _sinceLastChange = TimeSpan.Zero;
                _current = ForLevel(_level, _options);
            }
            else if (!bad && _goodStreak >= _options.RecoverSamples
                     && _sinceLastChange >= _minChangeInterval && _level > 0)
            {
                _level--;
                _sinceLastChange = TimeSpan.Zero;
                _goodStreak = 0;
                _current = ForLevel(_level, _options);
            }

            return _current;
        }
    }

    /// <summary>是否处于压力状态（会触发降级）。</summary>
    public bool IsUnderPressure(QualityFeedback fb)
        => fb.RttMs > _options.RttHighMs
           || fb.LossPercent > _options.LossHighPercent
           || fb.EncodeMsPerFrame > _options.EncodeBudgetMs
           || fb.QueueDepth > _options.QueueHighDepth
           // 输入积压：DESIGN 要求优先保输入响应，宁可降画质。
           || fb.InputPending;

    /// <summary>是否处于"良好"状态（累计够样本才升档）。</summary>
    public bool IsGood(QualityFeedback fb)
        => fb.RttMs <= _options.RttLowMs
           && fb.LossPercent <= _options.LossLowPercent
           && fb.EncodeMsPerFrame <= _options.EncodeBudgetMs * 0.6
           && fb.QueueDepth <= _options.QueueLowDepth
           && !fb.InputPending;

    /// <summary>回到满画质（会话重新建立时用；不保留历史档位）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _level = 0;
            _goodStreak = 0;
            _sinceLastChange = TimeSpan.Zero;
            _current = ForLevel(0, _options);
        }
    }
}
