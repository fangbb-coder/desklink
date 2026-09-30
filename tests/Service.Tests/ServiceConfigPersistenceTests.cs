// 缺陷③「--file-scope 不持久化」的回归测试。
//
// ## 原来的行为
//
// `--file-scope`（以及捕获显示器索引）只活在进程的命令行里。直接跑 Service 的人，
// 下次不带这个参数重启，授权目录就静默变成"一律拒绝"——文件功能无声失效。
//
// ## 现在的约定
//
//   1. 这两项落盘到 `<data-dir>\service.json`，跨重启保留。
//   2. `CommandLineParser` 两遍扫描解决"配置在 data-dir 里、而 data-dir 本身由命令行决定"的鸡生蛋。
//   3. **命令行永远优先于落盘值**。落盘只是默认值，不是覆盖。
//   4. 坏掉的 service.json 绝不能阻止 Service 启动。
using System.IO;
using System.Text.Json;
using DeskLink.Service.Configuration;
using Xunit;

namespace DeskLink.Service.Tests;

public class ServiceConfigPersistenceTests : IDisposable
{
    private readonly string _dir;

    public ServiceConfigPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dl-cfg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // —— 落盘格式 ——

    [Fact]
    public void 落盘位置是dataDir下的serviceJson()
    {
        Assert.Equal(Path.Combine(_dir, "service.json"), ServiceConfig.PathFor(_dir));
    }

    [Fact]
    public void 写入后能原样读回()
    {
        var cfg = new ServiceConfig
        {
            FileScopeRoots = { Path.Combine(_dir, "Share") },
            CaptureMonitorIndex = 2,
        };

        Assert.True(cfg.Save(_dir));
        var back = ServiceConfig.TryLoad(_dir);

        Assert.NotNull(back);
        Assert.Equal(2, back!.CaptureMonitorIndex);
        Assert.Equal(new[] { Path.Combine(_dir, "Share") }, back.FileScopeRoots);
    }

    [Fact]
    public void 落盘用snake_case字段名()
    {
        new ServiceConfig { FileScopeRoots = { @"D:\s" }, CaptureMonitorIndex = 1 }.Save(_dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(ServiceConfig.PathFor(_dir)));
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("file_scope_roots", out _));
        Assert.True(root.TryGetProperty("capture_monitor_index", out _));
    }

    [Fact]
    public void 目录不存在时写入也能成功()
    {
        var fresh = Path.Combine(_dir, "not", "created", "yet");
        Assert.True(new ServiceConfig { FileScopeRoots = { @"D:\s" } }.Save(fresh));
        Assert.True(File.Exists(ServiceConfig.PathFor(fresh)));
    }

    // —— 容错 ——

    [Fact]
    public void 文件不存在返回null而不是抛异常()
    {
        Assert.Null(ServiceConfig.TryLoad(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void 文件损坏返回null而不是抛异常()
    {
        File.WriteAllText(ServiceConfig.PathFor(_dir), "{ this is not json");
        Assert.Null(ServiceConfig.TryLoad(_dir));
    }

    [Fact]
    public void 负数显示器索引被夹回0()
    {
        File.WriteAllText(ServiceConfig.PathFor(_dir), """{"capture_monitor_index": -5}""");
        var cfg = ServiceConfig.TryLoad(_dir);
        Assert.NotNull(cfg);
        Assert.Equal(0, cfg!.CaptureMonitorIndex);
    }

    [Fact]
    public void 空白路径被丢弃_重复路径去重()
    {
        File.WriteAllText(
            ServiceConfig.PathFor(_dir),
            $$"""{"file_scope_roots": ["", "   ", "{{_dir.Replace("\\", "\\\\")}}\\a", "{{_dir.Replace("\\", "\\\\")}}\\A"]}""");
        var cfg = ServiceConfig.TryLoad(_dir);
        Assert.NotNull(cfg);
        Assert.Single(cfg!.FileScopeRoots);
    }

    // —— 解析器两遍扫描 + 优先级 ——

    [Fact]
    public void 不带参数时_自动读出dataDir里的持久化配置()
    {
        new ServiceConfig
        {
            FileScopeRoots = { Path.Combine(_dir, "Share") },
            CaptureMonitorIndex = 3,
        }.Save(_dir);

        var r = CommandLineParser.Parse(new[] { "--data-dir", _dir });

        Assert.True(r.Ok);
        Assert.Equal(3, r.Options!.CaptureMonitorIndex);
        Assert.Equal(new[] { Path.Combine(_dir, "Share") }, r.Options.FileScopeRoots);
    }

    [Fact]
    public void 命令行给了fileScope_覆盖落盘值()
    {
        new ServiceConfig { FileScopeRoots = { Path.Combine(_dir, "Persisted") } }.Save(_dir);

        var r = CommandLineParser.Parse(new[] { "--data-dir", _dir, "--file-scope", Path.Combine(_dir, "FromCli") });

        Assert.True(r.Ok);
        Assert.Equal(new[] { Path.Combine(_dir, "FromCli") }, r.Options!.FileScopeRoots);
    }

    [Fact]
    public void 命令行给了monitor_覆盖落盘值()
    {
        new ServiceConfig { CaptureMonitorIndex = 3 }.Save(_dir);

        var r = CommandLineParser.Parse(new[] { "--data-dir", _dir, "--monitor", "1" });

        Assert.True(r.Ok);
        Assert.Equal(1, r.Options!.CaptureMonitorIndex);
    }

    [Fact]
    public void 空目录里没有serviceJson_安静按没有默认值处理()
    {
        // 以前这两个用例不传 --data-dir，于是落到了**真实机器**的
        // %ProgramData%\DeskLink\service.json。装过 Service 的开发机上那个文件是存在的，
        // 测试就会随机红；而且它当时根本没在测"默认值"，测的是"我这台机器碰巧存了什么"。
        // hermetic 的前提是：测试只能看到测试自己造出来的状态。
        var r = CommandLineParser.Parse(new[] { "--data-dir", _dir });
        Assert.True(r.Ok);
        Assert.Empty(r.Options!.FileScopeRoots);
        Assert.Equal(0, r.Options.CaptureMonitorIndex);
    }

    // —— --monitor 本身的取值校验（缺陷②的服务端一半）——

    [Fact]
    public void monitor默认是0即主显示器()
    {
        var r = CommandLineParser.Parse(new[] { "--data-dir", _dir });
        Assert.True(r.Ok);
        Assert.Equal(0, r.Options!.CaptureMonitorIndex);
    }

    [Fact]
    public void monitor默认0必须盖掉落盘的非零值_否则界面复位不回去()
    {
        // 面板把显示器改回主显示器时会显式传 `--monitor 0`。
        // 这里守着相反的一侧：万一哪天把"总是传"改回"大于 0 才传"，
        // 用户在界面上把 2 改回 0，落盘的 2 就会复活，界面和实际捕获的屏幕对不上。
        new ServiceConfig { CaptureMonitorIndex = 2 }.Save(_dir);

        var viaCli = CommandLineParser.Parse(new[] { "--data-dir", _dir, "--monitor", "0" });
        Assert.True(viaCli.Ok);
        Assert.Equal(0, viaCli.Options!.CaptureMonitorIndex);

        // 不给 --monitor 时才回落落盘值——这才是"持久化"该有的样子。
        var viaPersisted = CommandLineParser.Parse(new[] { "--data-dir", _dir });
        Assert.Equal(2, viaPersisted.Options!.CaptureMonitorIndex);
    }

    [Fact]
    public void monitor接受非零索引()
    {
        var r = CommandLineParser.Parse(new[] { "--monitor", "2" });
        Assert.True(r.Ok);
        Assert.Equal(2, r.Options!.CaptureMonitorIndex);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void monitor非法取值被拒绝且给出退出码2(string bad)
    {
        var r = CommandLineParser.Parse(new[] { "--monitor", bad });
        Assert.False(r.Ok);
        Assert.Equal(2, r.ExitCode);
    }

    [Fact]
    public void monitor缺值被拒绝()
    {
        var r = CommandLineParser.Parse(new[] { "--monitor" });
        Assert.False(r.Ok);
        Assert.Equal(2, r.ExitCode);
    }

    [Fact]
    public void monitor出现在帮助文本里()
    {
        Assert.Contains("--monitor", CommandLineParser.HelpText);
    }
}
