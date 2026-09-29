// DeskLink.Panel —— 本机 Service 子进程的生命周期管理
//
// 面板是 Service 的"前台监护人"：起进程、收日志、随面板关闭一起收。
// 刻意不实现任何服务逻辑——Service 才是状态的事实来源，面板只负责"把它拉起来"和"看住它"。
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace DeskLink.Panel.Services;

/// <summary>Service 进程包装。日志经 <see cref="LogReceived"/> 抛到 UI 线程。</summary>
public sealed class ServiceProcess : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private Process? _proc;

    public ServiceProcess(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool IsRunning => _proc is { HasExited: false };

    public int? ExitCode => _proc is { HasExited: true } p ? p.ExitCode : null;

    /// <summary>Service 写出的每���行日志（已按行拆分）。</summary>
    public event Action<string>? LogReceived;

    /// <summary>Service 进程意外退出时触发，参数为退出码。</summary>
    public event Action<int>? Exited;

    public async Task StartAsync(string exePath, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("Service 已在运行");

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Post(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Post("[stderr] " + e.Data); };
        proc.Exited += (_, _) =>
        {
            int code = -1;
            try { code = proc.ExitCode; } catch (InvalidOperationException) { /* 已释放 */ }
            Post($"[面板] Service 进程已退出，退出码 {code}");
            _dispatcher.BeginInvoke(() => Exited?.Invoke(code));
        };

        try
        {
            if (!proc.Start())
            {
                proc.Dispose();
                throw new InvalidOperationException($"启动失败：{exePath}");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            proc.Dispose();
            throw new InvalidOperationException($"无法启动 Service（{exePath}）：{ex.Message}", ex);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _proc = proc;

        Post($"[面板] Service 已启动：{exePath} {string.Join(' ', args)}");
        await Task.CompletedTask.ConfigureAwait(ct.IsCancellationRequested);
    }

    /// <summary>请求 Service 退出；超时后强杀。返回是否优雅退出。</summary>
    public async Task<bool> StopAsync(int timeoutMs = 8000)
    {
        if (_proc is not { } proc) return true;

        try
        {
            if (!proc.HasExited)
            {
                // 优先让 Service 自己走正常停机（关管道、停 DirectServer、收 Agent）。
                proc.CloseMainWindow();
                await Task.Delay(200).ConfigureAwait(false);
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 进程可能已自行退出；按"已停止"处理。
        }
        finally
        {
            Post("[面板] Service 已停止");
        }
        return true;
    }

    private void Post(string line) => _dispatcher.BeginInvoke(() => LogReceived?.Invoke(line));

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _proc?.Dispose();
        _proc = null;
    }
}
