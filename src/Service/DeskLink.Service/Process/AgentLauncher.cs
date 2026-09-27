// DeskLink.DesktopAgent 启动器。
//
// 强制约束（DESIGN.md 风险回顾 #4 / 私有工作约定）：
//   - Service 启动 Agent 注入分支时，必须由调用方在 params 中显式传 noInject=true。
//   - 若 inject=true 且 noInject=false → AgentInjectSafetyException（hard-fail）。
//   - 该规则防止 SendInput 自环：P7 启动真实 SendInput 时，未传 --no-inject 等价于
//     "用户没意识到这是联调自控自"，必须由本类阻断并清晰报错。
//
// P4 范围：
//   - 参数强制 + 进程启动框架
//   - stub_mode：P7 之前 DeskLink.DesktopAgent 未实现，启动时只记录参数并返回桩 PID（0）
//     真实启动路径在 P7 接入 Vortice.Windows + Media Foundation 后启用
using System.Diagnostics;
using System.Globalization;

namespace DeskLink.Service.Process;

// 命名空间 DeskLink.Service.Process 与类型 System.Diagnostics.Process 同名，
// 直接用 `Process.Xxx` 会被解析到本命名空间（CS0234）。
// 这里用别名把 System.Diagnostics.Process 固定下来，避免后续文件再踩同一个坑。
using SysProcess = System.Diagnostics.Process;
using SysProcessStartInfo = System.Diagnostics.ProcessStartInfo;

public sealed class AgentLauncher
{
    private readonly Action<string>? _log;
    private readonly string _agentExePath;
    private readonly string? _logDir;
    private int _currentPid;
    private bool _currentIsStub;
    // 持有 Process 对象而非只记 PID：PID 会被系统复用，仅凭 _currentPid 做
    // HasExited/GetProcessById 判定，可能在 Agent 崩溃后误判"还在运行"，
    // Stop 时甚至可能杀掉复用了该 PID 的**无关进程**。Process 句柄与
    // 具体进程实例绑定，HasExited 判定可靠且不误伤。
    private SysProcess? _currentProc;

    /// <param name="logDir">
    /// 代理的 stdout/stderr 重定向目录（null 表示丢弃）。
    ///
    /// **必须重定向**：常驻代理会持续往 stderr 写日志。若把子进程输出接到管道却不读，
    /// 管道缓冲（4–64KB）写满后子进程会被**阻塞在写日志上**，表现为"画面莫名卡住"。
    /// 重定向到文件既避免了这个陷阱，又保留了排障所需的日志。
    /// </param>
    public AgentLauncher(string agentExePath, string? logDir = null, Action<string>? log = null)
    {
        _agentExePath = agentExePath;
        _logDir = logDir;
        _log = log;
    }

    public bool IsRunning => _currentPid != 0 && AgentProcessAlive();

    /// <summary>记的 Agent 进程是否还活着（记了 PID 但进程已退出时为 false）。</summary>
    private bool AgentProcessAlive()
    {
        var proc = _currentProc;
        if (proc is null) return false;
        try
        {
            if (proc.HasExited)
            {
                ClearStaleAgent();
                return false;
            }
            return true;
        }
        catch
        {
            // 句柄失效等异常状态：视为已退出。
            ClearStaleAgent();
            return false;
        }
    }

    /// <summary>Agent 已自行退出（崩溃/被杀）：清掉陈旧记录，让下次 Start 能重新启动。</summary>
    private void ClearStaleAgent()
    {
        _log?.Invoke($"AgentLauncher: recorded Agent pid={_currentPid} no longer exists, clearing state");
        _currentProc?.Dispose();
        _currentProc = null;
        _currentPid = 0;
        _currentIsStub = false;
    }

    /// <summary>
    /// 启动 Agent。
    /// </summary>
    /// <param name="inject">是否启用 SendInput 注入</param>
    /// <param name="noInject">调用方是否在 args 中显式带 --no-inject</param>
    /// <param name="pipeOverride">命名管道覆盖（双实例联调时给 Agent 指定连接哪个 Service 管道）</param>
    /// <returns>启动结果</returns>
    /// <exception cref="AgentInjectSafetyException">
    /// 当 inject=true 但 noInject=false 时抛出；调用方应将其映射为退出码非零 + 日志
    /// </exception>
    /// <summary>
    /// 拼装代理命令行（纯函数，便于单测断言"生产路径确实带了 --run"）。
    /// </summary>
    public static List<string> BuildArguments(
        bool inject,
        bool noInject,
        string? pipeOverride,
        string? mediaPipe,
        int monitorIndex = 0,
        int rotation = 0,
        int fps = 30,
        int bitrateBps = 8_000_000)
    {
        var args = new List<string>();

        // **必须带 --run**：不带的话代理跑完一次性自检就退出，不会有常驻画面。
        // 这条曾经缺失——冒烟是手工带 --run 启动代理的，所以掩盖了它；
        // 真实路径（客户端点"开始控制" → start_agent → 本方法）会拿到一个立刻退出的代理。
        args.Add("--run");

        if (inject) args.Add("--inject");
        if (noInject) args.Add("--no-inject");

        // 【重要】开关与取值必须是**两个独立元素**。
        //
        // ProcessStartInfo.ArgumentList 的每个元素 = 一个 argv 项，不会被再按空格切分。
        // 早期写成单个字符串 `$"--pipe {name}"`，实际传给代理的是**一个** argv
        // "--pipe DeskLink.Agent.default"，代理的参数解析器匹配不到 "--pipe"，
        // 直接走 default 分支抛"未知参数"→ 代理启动即失败。
        // （冒烟脚本用的是数组形式，所以掩盖了这个 bug。）
        AddOption(args, "--pipe", pipeOverride);
        // 媒体通道名必须由 Service 告知代理：代理不猜命名规则（否则改名时两边不同步）。
        AddOption(args, "--media-pipe", mediaPipe);

        if (monitorIndex > 0) AddOption(args, "--monitor", monitorIndex.ToString(CultureInfo.InvariantCulture));
        if (rotation != 0) AddOption(args, "--rotation", rotation.ToString(CultureInfo.InvariantCulture));
        if (fps > 0) AddOption(args, "--fps", fps.ToString(CultureInfo.InvariantCulture));
        if (bitrateBps > 0) AddOption(args, "--bitrate", bitrateBps.ToString(CultureInfo.InvariantCulture));

        return args;
    }

    /// <summary>追加"开关 + 取值"两个独立元素；取值为空则整体跳过。</summary>
    private static void AddOption(List<string> args, string flag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        args.Add(flag);
        args.Add(value);
    }

    public StartResult Start(
        bool inject,
        bool noInject,
        string? pipeOverride,
        string? mediaPipe = null,
        int monitorIndex = 0,
        int rotation = 0,
        int fps = 30,
        int bitrateBps = 8_000_000)
    {
        // 强制约束：输入环路防护
        if (inject && !noInject)
        {
            _log?.Invoke("AgentLauncher: --no-inject required when inject=true; refusing to start");
            throw new AgentInjectSafetyException(
                "refusing to start DeskLink.DesktopAgent in inject mode without explicit --no-inject; " +
                "this guard prevents SendInput self-loop (DESIGN.md risk #4)");
        }

        if (_currentPid != 0 && AgentProcessAlive())
        {
            _log?.Invoke($"AgentLauncher: Agent already running pid={_currentPid}");
            return new StartResult(_currentPid, _currentIsStub);
        }
        // Agent 已自行退出（崩溃/被杀）而记录未清：清掉后照常启动新实例，
        // 否则 Start 会永远返回已死的 PID（AgentLaunch 状态误报 + 启动失效）。

        var args = BuildArguments(inject, noInject, pipeOverride, mediaPipe,
            monitorIndex, rotation, fps, bitrateBps);

        // P4 stub 模式：检测不到真实 Agent exe 时仅记录参数并返回桩 PID
        if (!File.Exists(_agentExePath))
        {
            _log?.Invoke($"AgentLauncher: stub mode (agent exe not found at {_agentExePath}); args=[{string.Join(' ', args)}]");
            _currentPid = 0;
            _currentIsStub = true;
            return new StartResult(0, true);
        }

        try
        {
            var psi = new SysProcessStartInfo
            {
                FileName = _agentExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 重定向到文件（见构造器注释）：不重定向会让子进程继承服务进程的句柄，
                // 服务无控制台时输出无处可去；接到管道又不读则会写满缓冲把子进程卡死。
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            var proc = SysProcess.Start(psi);
            if (proc == null)
            {
                throw new InvalidOperationException("Process.Start returned null");
            }

            // 后台把子进程输出泵到文件。必须**持续读取**，否则管道写满即阻塞子进程。
            var outPath = ResolveLogPath("agent.out.log");
            var errPath = ResolveLogPath("agent.err.log");
            _ = Task.Run(() => PumpAsync(proc.StandardOutput, outPath));
            _ = Task.Run(() => PumpAsync(proc.StandardError, errPath));

            _currentPid = proc.Id;
            _currentIsStub = false;
            _currentProc = proc;
            _log?.Invoke($"AgentLauncher: started Agent pid={_currentPid} args=[{string.Join(' ', args)}]");
            return new StartResult(_currentPid, false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"AgentLauncher: start failed {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void Stop()
    {
        if (_currentPid == 0) return;
        try
        {
            // 用启动时持有的 Process 对象杀进程（而非按 PID 重新解析）：
            // PID 已被系统复用时，GetProcessById 会拿到**无关进程**并以
            // LocalSystem 权限误杀（Kill entireProcessTree）。
            var proc = _currentProc;
            if (proc is not null && !proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                _log?.Invoke($"AgentLauncher: killed Agent pid={_currentPid}");
            }
        }
        catch (ArgumentException)
        {
            // 进程已退出
        }
        catch (InvalidOperationException)
        {
            // 进程已退出（本地进程句柄失效）
        }
        catch (Exception ex)
        {
            _log?.Invoke($"AgentLauncher: stop failed {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _currentProc?.Dispose();
            _currentProc = null;
            _currentPid = 0;
            _currentIsStub = false;
        }
    }

    /// <summary>代理输出文件路径；未配置 logDir 时返回 null（丢弃输出）。</summary>
    private string? ResolveLogPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_logDir)) return null;
        try
        {
            Directory.CreateDirectory(_logDir);
            return Path.Combine(_logDir, fileName);
        }
        catch
        {
            // 建目录失败不能让启动失败：宁可丢日志，也要能起代理。
            return null;
        }
    }

    /// <summary>把子进程输出持续泵到文件；路径为空则丢弃（但仍需读，避免阻塞子进程）。</summary>
    private static async Task PumpAsync(StreamReader reader, string? path)
    {
        StreamWriter? writer = null;
        try
        {
            if (path is not null)
            {
                // 追加模式：代理重启时保留历史，便于对比前后两次会话。
                writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,
                };
            }

            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (writer is not null)
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // 管道关闭/进程被杀：正常收尾路径。
        }
        finally
        {
            writer?.Dispose();
        }
    }

    public readonly record struct StartResult(int Pid, bool StubMode);
}

/// <summary>
/// 输入环路防护触发；调用方应映射为退出非零 + 日志。
/// </summary>
public sealed class AgentInjectSafetyException : InvalidOperationException
{
    public AgentInjectSafetyException(string message) : base(message) { }
}
