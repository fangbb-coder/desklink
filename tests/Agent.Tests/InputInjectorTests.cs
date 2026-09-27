using DeskLink.DesktopAgent.Input;

namespace DeskLink.Agent.Tests;

/// <summary>
/// 输入注入的 --no-inject 行为。
///
/// 注意：这里**故意不测试真实注入路径**——在测试进程里真的 SendInput 会把合成事件
/// 打进当前桌面，污染开发机并且无法断言。真实注入只能在受控的手工联调里验证，
/// 这一点在交付说明里明确标注为"未自动验证"。
/// </summary>
public class InputInjectorTests
{
    [Fact]
    public void NoInject_AllOperationsReportSuccess_AndPerformNoInjection()
    {
        var injector = new InputInjector(noInject: true);

        Assert.True(injector.NoInject);

        Assert.Equal(InjectionStatus.Success, injector.MoveMouse(100, 200, 1920, 1080));
        Assert.Equal(InjectionStatus.Success, injector.MouseButton(MouseButtonKind.Left, down: true));
        Assert.Equal(InjectionStatus.Success, injector.MouseButton(MouseButtonKind.Left, down: false));
        Assert.Equal(InjectionStatus.Success, injector.MouseButton(MouseButtonKind.Right, down: true));
        Assert.Equal(InjectionStatus.Success, injector.MouseButton(MouseButtonKind.Middle, down: false));
        Assert.Equal(InjectionStatus.Success, injector.MouseButton(MouseButtonKind.X1, down: true));
        Assert.Equal(InjectionStatus.Success, injector.Wheel(120));
        Assert.Equal(InjectionStatus.Success, injector.Wheel(-120, horizontal: true));
        Assert.Equal(InjectionStatus.Success, injector.KeyScan(0x1E, down: true, extended: false)); // 'A'
        Assert.Equal(InjectionStatus.Success, injector.KeyScan(0x1E, down: false, extended: false));
        Assert.Equal(InjectionStatus.Success, injector.KeyScan(0x4B, down: true, extended: true));  // Left arrow
        Assert.Equal(InjectionStatus.Success, injector.KeyVirtual(0x41, down: true, extended: false));

        // 关键断言：no-inject 模式下绝不能真的调用过 SendInput。
        // 只看返回值无法区分"真注入成功"和"空操作假装成功"，所以要看计数。
        Assert.Equal(0, injector.InjectedCallCount);
        Assert.Equal(0, injector.DeniedCount);
        Assert.Equal(InjectionStatus.Success, injector.LastStatus);
        Assert.Null(injector.LastError);
    }

    [Fact]
    public void NoInject_MoveMouseWithInvalidDesktopSize_StillFails()
    {
        // 参数校验先于 no-inject 短路：无效参数应报告失败而不是假装成功，
        // 否则联调时会把"坐标算错"掩盖成"注入成功"。
        var injector = new InputInjector(noInject: true);
        Assert.Equal(InjectionStatus.Failed, injector.MoveMouse(1, 1, 0, 0));
        Assert.NotNull(injector.LastError);
        Assert.Equal(0, injector.InjectedCallCount);
    }

    [Fact]
    public void InjectMode_IsReported()
    {
        Assert.False(new InputInjector(noInject: false).NoInject);
        Assert.True(new InputInjector(noInject: true).NoInject);
    }
}
