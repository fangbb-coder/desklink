using DeskLink.Panel;
using Xunit;

namespace DeskLink.Panel.Tests;

/// <summary>面板设置的持久化与实例名推导。实例名算错 → 管道名算错 → 状态永远显示"服务未运行"。</summary>
public class PanelSettingsTests
{
    [Fact]
    public void 实例名取路径末段并小写()
    {
        var s = new PanelSettings { DataDir = @"C:\Users\me\DL-CTRL" };
        Assert.Equal("dl-ctrl", s.InstanceId);
    }

    [Fact]
    public void 末尾带斜杠不影响实例名()
    {
        var s = new PanelSettings { DataDir = @"C:\dl-ctrl\" };
        Assert.Equal("dl-ctrl", s.InstanceId);
    }

    [Fact]
    public void 末段为盘符根时退化为default()
    {
        var s = new PanelSettings { DataDir = @"C:\" };
        Assert.Equal("default", s.InstanceId);
    }

    [Fact]
    public void 管道名与Service的命名规则一致()
    {
        var s = new PanelSettings { DataDir = @"C:\dl-ctrl" };
        Assert.Equal("DeskLink.Client.dl-ctrl", s.ClientPipeName);
    }

    [Fact]
    public void 默认实例名为default()
    {
        var s = new PanelSettings { DataDir = "" };
        Assert.Equal("default", s.InstanceId);
    }

    [Fact]
    public void 保存后可原样读回()
    {
        var path = Path.Combine(Path.GetTempPath(), "dl-panel-" + Guid.NewGuid().ToString("N"), "panel.json");
        try
        {
            var s = new PanelSettings
            {
                DataDir = @"C:\dl-targ",
                DirectPort = 47500,
                EnableDirect = true,
                InjectAgent = true,
                FileScopeRoots = new List<string> { @"D:\Share", @"E:\Pub" },
                RelayUrl = "quic://vps:9443",
                Role = PanelRole.Controller,
            };
            Assert.True(s.Save(path));

            var back = PanelSettings.Load(path);
            Assert.Equal(@"C:\dl-targ", back.DataDir);
            Assert.Equal(47500, back.DirectPort);
            Assert.True(back.EnableDirect);
            Assert.True(back.InjectAgent);
            Assert.Equal(new[] { @"D:\Share", @"E:\Pub" }, back.FileScopeRoots);
            Assert.Equal("quic://vps:9443", back.RelayUrl);
            Assert.Equal(PanelRole.Controller, back.Role);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 文件不存在时返回默认值而不是抛异常()
    {
        var s = PanelSettings.Load(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(PanelSettings.DefaultDataDir(), s.DataDir);
        Assert.Equal(47200, s.DirectPort);
    }

    [Fact]
    public void 文件损坏时返回默认值而不是抛异常()
    {
        // 面板必须永远能打开：配置坏了就退回默认，让用户重设一次。
        var path = Path.Combine(Path.GetTempPath(), "dl-panel-" + Guid.NewGuid().ToString("N"), "panel.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "{ this is not json ");
            var s = PanelSettings.Load(path);
            Assert.Equal(47200, s.DirectPort);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public void 被控端角色默认开直连与注入代理()
    {
        var s = new PanelSettings();
        Assert.True(s.ApplyRoleDefaults(PanelRole.Controlled));
        Assert.True(s.EnableDirect);
        Assert.True(s.InjectAgent);
        Assert.Equal(PanelRole.Controlled, s.Role);
    }

    [Fact]
    public void 控制端角色默认关直连与注入代理()
    {
        var s = new PanelSettings { EnableDirect = true, InjectAgent = true };
        Assert.True(s.ApplyRoleDefaults(PanelRole.Controller));
        Assert.False(s.EnableDirect);
        Assert.False(s.InjectAgent);
    }

    [Fact]
    public void 重复应用同一角色报告无改动()
    {
        var s = new PanelSettings();
        s.ApplyRoleDefaults(PanelRole.Controlled);
        Assert.False(s.ApplyRoleDefaults(PanelRole.Controlled));
    }

    [Fact]
    public void Clone是深拷贝()
    {
        var s = new PanelSettings { FileScopeRoots = { @"D:\A" } };
        var c = s.Clone();
        c.FileScopeRoots.Add(@"E:\B");
        Assert.Single(s.FileScopeRoots);
    }

    [Fact]
    public void Clone带得走显示器索引()
    {
        var s = new PanelSettings { MonitorIndex = 3 };
        Assert.Equal(3, s.Clone().MonitorIndex);
    }

    [Fact]
    public void 显示器索引默认是0即主显示器()
    {
        Assert.Equal(0, new PanelSettings().MonitorIndex);
    }

    [Fact]
    public void 显示器索引能落盘并读回()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dl-panel-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var s = new PanelSettings { MonitorIndex = 2 };
            Assert.True(s.Save(path));
            Assert.Equal(2, PanelSettings.Load(path).MonitorIndex);
        }
        finally
        {
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch (System.IO.IOException) { }
        }
    }

    [Fact]
    public void 切角色不动显示器索引()
    {
        // 一键准备只该改直连/代理两个开关。多屏用户设好的索引不该被"一键准备"抹掉。
        var s = new PanelSettings { MonitorIndex = 2 };
        s.ApplyRoleDefaults(PanelRole.Controller);
        Assert.Equal(2, s.MonitorIndex);
        s.ApplyRoleDefaults(PanelRole.Controlled);
        Assert.Equal(2, s.MonitorIndex);
    }
}
