// 进程执行抽象（P5.6）。
//
// 为什么要有这层：FirewallHelper 需要调用 `netsh advfirewall`，而
//   - 真实调用需要管理员权限，单测环境（普通用户/CI）跑不了；
//   - 我们想断言的是"命令怎么拼、幂等性如何、端口变更是否删旧加新"，
//     而不是"Windows 防火墙真的被改了"。
// 因此把"执行外部命令"抽成接口，单测注入 FakeProcessRunner 即可完整覆盖决策逻辑，
// 生产走 SystemProcessRunner。
using System.Diagnostics;

namespace DeskLink.Service.Security;

// 命名空间 DeskLink.Service.Process 与类型 System.Diagnostics.Process 同名，
// 直接用 `Process.Xxx` 会被解析到本程序集的命名空间（CS0234）。
// 用别名把 System.Diagnostics.Process 固定下来（与 AgentLauncher 同样的处理）。
using SysProcess = System.Diagnostics.Process;
using SysProcessStartInfo = System.Diagnostics.ProcessStartInfo;

/// <summary>一次外部命令的执行结果。</summary>
public readonly record struct ProcessRunResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>执行外部命令（可注入，便于单测）。</summary>
public interface IProcessRunner
{
    ProcessRunResult Run(string fileName, IReadOnlyList<string> args);
}

/// <summary>真实进程执行器（生产用）。</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <summary>单条命令的超时（netsh 正常在百毫秒级返回；卡住说明系统异常）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly TimeSpan _timeout;

    public SystemProcessRunner(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
    }

    public ProcessRunResult Run(string fileName, IReadOnlyList<string> args)
    {
        var psi = new SysProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = SysProcess.Start(psi);
        if (proc is null)
        {
            return new ProcessRunResult(-1, "", "Process.Start returned null");
        }

        // 先起异步读取，再等待退出：避免子进程输出写满管道缓冲导致互相死锁。
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)_timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return new ProcessRunResult(-1, "", $"timeout after {_timeout.TotalSeconds:0}s");
        }

        // 进程已退出 → 两个读取任务必然很快完成。
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return new ProcessRunResult(proc.ExitCode, stdout, stderr);
    }
}
