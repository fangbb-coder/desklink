using DeskLink.DesktopAgent.Session;

namespace DeskLink.Agent.Tests;

/// <summary>
/// WTS 会话状态检测。
///
/// 这里能自动验证的是"健壮性"而不是"语义正确"：本机当前处于什么会话状态取决于
/// 跑测试那一刻的登录/锁屏情况，测试进程无法在不改变开发机状态的前提下控制它。
/// 所以断言写成"返回一个自洽的快照、绝不抛异常"，并把具体状态打印出来供人工核对。
///
/// 明确未自动验证的部分：Lock/Unlock 翻转后 SessionFlags 是否真的按预期变化——
/// 这需要真的锁屏再解锁，无法在无人值守的测试里做。交付说明里标注为"未自动验证"。
/// </summary>
public class SessionWatcherTests
{
    [Fact]
    public void GetState_ReturnsSaneSnapshot_AndNeverThrows()
    {
        var watcher = new SessionWatcher();

        var state = watcher.GetState();

        // 不抛异常本身就是核心断言（P/Invoke 失败必须降级为"未知"）。
        Assert.NotNull(state.Summary);
        Assert.False(string.IsNullOrWhiteSpace(state.Summary));

        // ConnectState 只能是有限枚举之一，绝不能是 null 或空串。
        Assert.False(string.IsNullOrWhiteSpace(state.ConnectState));
        Assert.Contains(state.ConnectState, new[]
        {
            "active", "connected", "connect-query", "shadow", "disconnected",
            "idle", "listen", "reset", "down", "init", "none", "unknown",
        });

        // 会话号与"是否有控制台会话"必须自洽：无会话时用哨兵值 -1。
        if (state.HasConsoleSession)
        {
            Assert.NotEqual(SessionWatcher.NoConsoleSession, state.ConsoleSessionId);
            Assert.True(state.ConsoleSessionId >= 0);
        }
        else
        {
            Assert.Equal(SessionWatcher.NoConsoleSession, state.ConsoleSessionId);
        }

        // 三态布尔：true / false / null（无法判定）都合法，但不能出现非法的组合。
        if (state.ConnectState == "none")
        {
            Assert.False(state.HasConsoleSession);
        }

        Console.WriteLine($"[SessionWatcher] {state}");
    }

    [Fact]
    public void CanCaptureDesktop_IsConsistentWithState()
    {
        var watcher = new SessionWatcher();

        bool canCapture = watcher.CanCaptureDesktop(out string reason);

        // 无论结论如何，都必须给出人类可读的理由，方便用户在 UI 上看到"为什么黑屏"。
        Assert.NotNull(reason);

        var state = watcher.GetState();
        if (!state.HasConsoleSession)
        {
            Assert.False(canCapture);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        // 锁屏状态下绝不能声称可以捕获——规格明确不提供 Secure Desktop 捕获。
        if (state.IsLocked == true)
        {
            Assert.False(canCapture);
        }

        Console.WriteLine($"[SessionWatcher] can_capture={canCapture} reason='{reason}'");
    }

    [Fact]
    public void NoConsoleSessionSentinel_MatchesWts()
    {
        // WTSGetActiveConsoleSessionId 用 0xFFFFFFFF 表示"没有活动控制台会话"。
        // 这个常量被 Program.cs 用来决定是否直接报告"等待本地登录"，写错会静默跑偏。
        Assert.Equal(unchecked((int)0xFFFFFFFF), SessionWatcher.NoConsoleSession);
    }
}
