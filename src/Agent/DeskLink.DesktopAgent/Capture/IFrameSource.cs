namespace DeskLink.DesktopAgent.Capture;

/// <summary>
/// 帧源抽象。
///
/// 为什么需要它：实时管线用 DXGI Desktop Duplication 抓真桌面，而自检/回环测试必须能在
/// 无桌面、无 GPU 的环境里跑完整条"源→转换→编码→解码→校验"链路。把"帧从哪来"抽象掉之后，
/// 管线代码只有一份，测试图源与真实捕获可以互换，P7 的验收点（本地回环像素校验）
/// 因此不依赖任何 Windows 图形栈。
/// </summary>
public interface IFrameSource : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>人类可读的来源描述，用于状态 JSON。</summary>
    string Description { get; }

    /// <summary>
    /// 取一帧。
    /// </summary>
    /// <param name="timeoutMs">等待新帧的超时（毫秒）。</param>
    /// <param name="frame">成功时给出 BGRA 帧。</param>
    /// <param name="accessLost">
    /// true 表示底层捕获已失效（典型是 DXGI_ERROR_ACCESS_LOST），
    /// 调用方必须重建整条管线，而**不是**把它当作错误退出。
    /// </param>
    /// <param name="error">失败原因（无新帧时为 null）。</param>
    /// <returns>拿到新帧返回 true。</returns>
    bool TryGetFrame(int timeoutMs, out BgraFrame? frame, out bool accessLost, out string? error);
}
