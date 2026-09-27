using DeskLink.DesktopAgent.Capture;
using DeskLink.DesktopAgent.Codec;

namespace DeskLink.Agent.Tests;

/// <summary>
/// ACCESS_LOST 后的整管线重建语义。
///
/// 这段逻辑是整个 P7 最容易出 bug 的地方（重复释放、状态不同步、重建失败即断会话），
/// 所以刻意用工厂委托把它做成可单测的：不需要真的触发 DXGI_ERROR_ACCESS_LOST，
/// 也不需要真实 GPU/桌面。
///
/// 需要构造真实 CapturePipeline 的用例会依赖 MF H.264 编码器，
/// 本机没有编码器时按约定 Skip（原因写清楚），绝不假装通过。
/// </summary>
public class AccessLostRecoveryTests
{
    /// <summary>只用于验证"被正确释放"的假帧源；不产生任何帧。</summary>
    private sealed class FakeFrameSource : IFrameSource
    {
        public int Width => 320;
        public int Height => 240;
        public string Description => "fake";
        public bool Disposed { get; private set; }

        public bool TryGetFrame(int timeoutMs, out BgraFrame? frame, out bool accessLost, out string? error)
        {
            frame = null;
            accessLost = false;
            error = null;
            return false;
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void TryRebuild_WhenFactoryFails_ReportsRetryStateAndKeepsCurrentNull()
    {
        var messages = new List<string>();
        using var recovery = new AccessLostRecovery(() => null, messages.Add);

        Assert.False(recovery.TryRebuild(out var error));

        Assert.NotNull(error);
        Assert.Null(recovery.Current);
        // 重建失败不能停留在"正在恢复"——否则状态条会永远转圈。
        Assert.False(recovery.IsRecovering);
        // 失败的尝试不计入重建次数。
        Assert.Equal(0, recovery.RebuildCount);
        Assert.Equal(new[] { "正在恢复画面", "画面恢复失败，等待重试" }, messages);
    }

    [Fact]
    public void EnsurePipeline_WhenFactoryFails_ReturnsFalseWithError()
    {
        using var recovery = new AccessLostRecovery(() => null);

        Assert.False(recovery.EnsurePipeline(out var error));
        Assert.NotNull(error);
        Assert.Null(recovery.Current);
    }

    [Fact]
    public void RebuildAndEnsure_AfterDispose_ThrowObjectDisposed()
    {
        var recovery = new AccessLostRecovery(() => null);
        recovery.Dispose();

        Assert.Throws<ObjectDisposedException>(() => recovery.TryRebuild(out _));
        Assert.Throws<ObjectDisposedException>(() => recovery.EnsurePipeline(out _));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var recovery = new AccessLostRecovery(() => null);
        recovery.Dispose();
        recovery.Dispose();
    }

    [SkippableFact]
    public void TryRebuild_WhenFactorySucceeds_DisposesOldPipelineAndNotifiesRecovered()
    {
        var sources = new List<FakeFrameSource>();
        var messages = new List<string>();

        using var recovery = new AccessLostRecovery(() =>
        {
            var encoder = H264Encoder.TryCreate(H264EncoderSettings.Default(320, 240, preferHardware: false), out _);
            if (encoder is null) return null;
            var source = new FakeFrameSource();
            sources.Add(source);
            return new CapturePipeline(source, encoder, gpuConverter: null);
        }, messages.Add);

        bool built = recovery.EnsurePipeline(out var firstError);
        Skip.If(!built, $"本机没有可用的 MF H.264 编码器，无法构造测试管线，跳过（原因：{firstError}）");

        var first = sources[0];
        Assert.Same(first, recovery.Current!.Source);
        Assert.False(first.Disposed);
        Assert.Equal(0, recovery.RebuildCount);

        Assert.True(recovery.TryRebuild(out var rebuildError));
        Assert.Null(rebuildError);

        // 旧管线的帧源必须被释放（捕获设备/转换器/编码器是绑定在一起的生命周期）。
        Assert.True(first.Disposed);
        Assert.Equal(2, sources.Count);
        Assert.False(sources[1].Disposed);
        Assert.Same(sources[1], recovery.Current!.Source);
        Assert.Equal(1, recovery.RebuildCount);
        Assert.False(recovery.IsRecovering);
        Assert.Equal(new[] { "正在恢复画面", "画面已恢复" }, messages);

        // Dispose 必须释放重建后的当前管线。
        var current = sources[1];
        recovery.Dispose();
        Assert.True(current.Disposed);
    }

    [SkippableFact]
    public void EnsurePipeline_WhenPipelineAlreadyExists_IsIdempotent()
    {
        int factoryCalls = 0;
        using var recovery = new AccessLostRecovery(() =>
        {
            factoryCalls++;
            var encoder = H264Encoder.TryCreate(H264EncoderSettings.Default(320, 240, preferHardware: false), out _);
            if (encoder is null) return null;
            return new CapturePipeline(new FakeFrameSource(), encoder, gpuConverter: null);
        });

        bool built = recovery.EnsurePipeline(out var error);
        Skip.If(!built, $"本机没有可用的 MF H.264 编码器，无法构造测试管线，跳过（原因：{error}）");

        Assert.True(recovery.EnsurePipeline(out _));
        // 已存在管线时不得重复建（重复建会重复占用 DXGI 输出）。
        Assert.Equal(1, factoryCalls);
    }
}
