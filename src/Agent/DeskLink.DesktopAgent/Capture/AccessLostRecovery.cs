namespace DeskLink.DesktopAgent.Capture;

/// <summary>
/// ACCESS_LOST 后的整管线重建（捕获 → 转换 → 编码）。
///
/// 设计要点（DESIGN：ACCESS_LOST 时整管线重建，期间状态条显示"正在恢复画面"，不丢控制权）：
///   - 重建**不能**由捕获层自己偷偷做：那样上层会话状态无从得知，用户会以为掉线。
///     因此这里把重建做成显式动作，并在开始/结束各发一次状态回调。
///   - 重建失败不是致命错误：保留旧的（已失效的）管线引用为空，等待下一次重试，
///     期间会话保持"正在恢复画面"。这样短暂的模式切换不会导致会话中断。
///   - 用工厂委托而不是直接 new，是为了让重建逻辑可被单元测试驱动（注入假管线），
///     否则这段"最容易出 bug 的恢复逻辑"只能靠真机 ACCESS_LOST 才能验证。
/// </summary>
public sealed class AccessLostRecovery : IDisposable
{
    private readonly Func<CapturePipeline?> _factory;
    private readonly Action<string>? _onStateChanged;
    private bool _disposed;

    public AccessLostRecovery(Func<CapturePipeline?> factory, Action<string>? onStateChanged = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _onStateChanged = onStateChanged;
    }

    public CapturePipeline? Current { get; private set; }

    /// <summary>已完成的重建次数（用于状态展示与测试断言）。</summary>
    public int RebuildCount { get; private set; }

    /// <summary>当前是否处于"正在恢复画面"状态。</summary>
    public bool IsRecovering { get; private set; }

    /// <summary>确保管线存在；不存在则尝试建立。</summary>
    public bool EnsurePipeline(out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        error = null;
        if (Current is not null) return true;

        Current = _factory();
        if (Current is null)
        {
            error = "无法建立捕获管线";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 丢弃当前管线并重建整条管线。
    /// 返回 true 表示重建成功；false 时 <see cref="Current"/> 为 null，等待下一次重试。
    /// </summary>
    public bool TryRebuild(out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        error = null;

        IsRecovering = true;
        _onStateChanged?.Invoke("正在恢复画面");

        try
        {
            DisposeCurrent();

            var rebuilt = _factory();
            if (rebuilt is null)
            {
                error = "管线重建失败";
                return false;
            }

            Current = rebuilt;
            RebuildCount++;
            return true;
        }
        finally
        {
            IsRecovering = false;
            _onStateChanged?.Invoke(Current is null ? "画面恢复失败，等待重试" : "画面已恢复");
        }
    }

    private void DisposeCurrent()
    {
        var current = Current;
        Current = null;
        try { current?.Dispose(); } catch { /* 已失效的管线，释放异常忽略 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCurrent();
    }
}
