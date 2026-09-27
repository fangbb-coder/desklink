using DeskLink.DesktopAgent.Capture;
using DeskLink.DesktopAgent.Session;

namespace DeskLink.Agent.Tests;

/// <summary>
/// DXGI Desktop Duplication 真实抓屏。
///
/// 这些用例只在"本机存在交互式控制台会话"时运行；没有桌面（无头 CI / 服务会话）时
/// 会以明确原因 Skip，而不是 Fail —— 那种环境本来就不该有可抓的桌面。
///
/// 但**不允许**把"捕获坏了"也 Skip 掉：只要存在控制台会话，
/// TryCreate 就必须成功，否则视为缺陷直接 Fail。唯一例外是
/// DXGI_ERROR_NOT_CURRENTLY_AVAILABLE（输出已被别的进程独占），
/// 那属于环境冲突而非代码问题。
///
/// 注意一个实测到的尺寸陷阱：ListMonitors 报告的是 1493x933，
/// 而 Duplication 实际返回 2240x1400（该机 150% 缩放，逻辑像素 vs 物理像素）。
/// 因此这里**不**断言两者相等，只断言"拿到帧之后 capture.Width 与帧宽一致"，
/// 这正是 EnsureStaging 依据实际纹理重建尺寸的行为。
/// </summary>
public class DuplicationCaptureTests
{
    private const string NotCurrentlyAvailable = "0x887A0022";

    private static void RequireConsoleSession()
    {
        var state = new SessionWatcher().GetState();
        Skip.If(!state.HasConsoleSession,
            $"本机没有活动的控制台会话（connect_state={state.ConnectState}），没有可抓的桌面，跳过");
    }

    [SkippableFact]
    public void ListMonitors_ReturnsAtLeastOneMonitorWithSaneGeometry()
    {
        RequireConsoleSession();

        var monitors = DuplicationCapture.ListMonitors();

        Skip.If(monitors.Count == 0, "本机没有枚举到任何 DXGI 输出（无显卡/无桌面），跳过");

        foreach (var m in monitors)
        {
            Assert.True(m.Width > 0, $"显示器 {m.Index} 宽度必须为正，实际 {m.Width}");
            Assert.True(m.Height > 0, $"显示器 {m.Index} 高度必须为正，实际 {m.Height}");
            Assert.False(string.IsNullOrWhiteSpace(m.DeviceName));
        }

        Assert.Equal(0, monitors[0].Index);
        Assert.True(monitors[0].IsPrimary);

        Console.WriteLine($"[DXGI] 枚举到 {monitors.Count} 块显示器：" +
            string.Join("; ", monitors.Select(m => $"{m.Index}:{m.DeviceName} {m.Width}x{m.Height} @({m.Left},{m.Top}) {m.AdapterName}")));
    }

    [SkippableFact]
    public void Capture_InitializesAndAcquiresRealFrame()
    {
        RequireConsoleSession();

        using var capture = DuplicationCapture.TryCreate(0, out var error);

        // 输出被其它进程独占是可接受的环境冲突；其余失败一律算缺陷。
        bool outputBusy = capture is null && error is not null &&
                          error.Contains(NotCurrentlyAvailable, StringComparison.OrdinalIgnoreCase);
        Skip.If(outputBusy, $"该 DXGI 输出已被其它进程独占，跳过：{error}");

        Assert.NotNull(capture);

        // 第一帧理论上必定可用（AcquireNextFrame 首次总会返回当前桌面图像）；
        // 后续帧若桌面静止会走 DXGI_ERROR_WAIT_TIMEOUT —— 那也是正确行为，不是错误。
        // 因此这里最多尝试 3 次，要求至少拿到一帧，并单独校验"超时不算错误"。
        BgraFrame? frame = null;
        bool accessLost = false;
        string? lastError = null;
        int attempts = 0;
        for (; attempts < 3; attempts++)
        {
            if (capture!.TryGetFrame(1000, out frame, out accessLost, out lastError) && frame is not null)
            {
                break;
            }

            // 失效信号或"无新帧"都不允许伴随 error 文本被当成失败退出。
            Assert.True(accessLost || lastError is null,
                $"TryGetFrame 既未拿到帧、也非超时/失效，却给出了错误：{lastError}");
        }

        Assert.NotNull(frame);
        Assert.True(frame!.Width > 0 && frame.Height > 0);
        Assert.True(frame.Stride >= frame.Width * 4, $"stride {frame.Stride} 小于 width*4 = {frame.Width * 4}");
        Assert.Equal(frame.Stride * frame.Height, frame.Pixels.Length);

        // EnsureStaging 会用实际纹理尺寸刷新 capture 的宽高，二者必须一致。
        Assert.Equal(frame.Width, capture!.Width);
        Assert.Equal(frame.Height, capture.Height);

        Console.WriteLine($"[DXGI] 第 {attempts + 1} 次尝试拿到帧：{frame.Width}x{frame.Height} stride={frame.Stride} bytes={frame.Pixels.Length} accessLost={accessLost}");
    }

    [SkippableFact]
    public void Capture_DisposeIsIdempotent()
    {
        RequireConsoleSession();

        var capture = DuplicationCapture.TryCreate(0, out var error);
        Skip.If(capture is null && error is not null &&
                error.Contains(NotCurrentlyAvailable, StringComparison.OrdinalIgnoreCase),
                $"该 DXGI 输出已被其它进程独占，跳过：{error}");
        Assert.NotNull(capture);

        capture!.Dispose();
        capture.Dispose(); // 必须可重入，否则 ACCESS_LOST 重建路径会二次释放崩溃。

        // 释放后再取帧必须干净失败，而不是抛异常。
        Assert.False(capture.TryGetFrame(0, out var frame, out _, out var err));
        Assert.Null(frame);
        Assert.NotNull(err);
    }
}
