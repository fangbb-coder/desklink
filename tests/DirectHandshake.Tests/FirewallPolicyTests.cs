using DeskLink.Service.Security;
using Xunit;

namespace DeskLink.DirectHandshake.Tests;

/// <summary>
/// P5.6 防火墙策略测试。
///
/// 通过注入 <see cref="IProcessRunner"/> + <see cref="IFirewallStateStore"/> 把
/// "执行 netsh" 与"读写注册表"都换成内存假实现，因此本组用例
///   - 不需要管理员权限；
///   - 不会改动本机真实防火墙；
///   - 能完整断言命令拼装、幂等性、端口变更"删旧加新"、未提权不改动等语义。
/// </summary>
public class FirewallPolicyTests
{
    /// <summary>内存版 netsh：维护 规则名 → localport 的映射，模拟 add/delete/show 语义。</summary>
    private sealed class FakeNetsh : IProcessRunner
    {
        public readonly Dictionary<string, int> Rules = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<IReadOnlyList<string>> Calls = new();
        public bool SimulateAddFailure;

        public ProcessRunResult Run(string fileName, IReadOnlyList<string> args)
        {
            Assert.Equal("netsh", fileName);
            Calls.Add(args);

            // args: advfirewall firewall <verb> rule ...
            var verb = args.Count > 2 ? args[2] : "";
            string? name = null;
            int? port = null;
            foreach (var a in args)
            {
                if (a.StartsWith("name=", StringComparison.OrdinalIgnoreCase)) name = a["name=".Length..];
                if (a.StartsWith("localport=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(a["localport=".Length..], out var p)) port = p;
            }

            switch (verb)
            {
                case "add":
                    if (SimulateAddFailure) return new ProcessRunResult(1, "", "add failed");
                    if (name is null) return new ProcessRunResult(1, "", "missing name");
                    Rules[name] = port ?? 0;
                    return new ProcessRunResult(0, "Ok.", "");
                case "delete":
                    if (name is not null && Rules.Remove(name)) return new ProcessRunResult(0, "Ok.", "");
                    return new ProcessRunResult(1, "No rules match the specified criteria.", "");
                case "show":
                    if (name is not null && Rules.TryGetValue(name, out var pp))
                        return new ProcessRunResult(0, $"Rule Name: {name}\r\nLocalPort: {pp}\r\n", "");
                    return new ProcessRunResult(1, "No rules match the specified criteria.", "");
                default:
                    return new ProcessRunResult(1, "", $"unknown verb {verb}");
            }
        }

        /// <summary>取"添加规则"调用（用于断言端口）。</summary>
        public IEnumerable<int> AddedPorts =>
            Calls.Where(c => c.Count > 2 && c[2] == "add")
                 .Select(c => int.Parse(c.First(x => x.StartsWith("localport=", StringComparison.OrdinalIgnoreCase))["localport=".Length..]));
    }

    private sealed class FakeState : IFirewallStateStore
    {
        public bool Enabled;
        public int Port;
        public bool GetEnabled(int port) => Enabled && Port == port;
        public bool Set(bool enabled, int port) { Enabled = enabled; Port = port; return true; }
    }

    private static FirewallHelper Make(FakeNetsh netsh, FakeState state, bool elevated)
        => new(log: null, runner: netsh, isElevated: () => elevated, state: state);

    [Fact]
    public void BuildAddRuleArgs_HasExpectedShape()
    {
        var args = FirewallHelper.BuildAddRuleArgs(FirewallHelper.TcpRuleName, "TCP", 47200);
        Assert.Equal("advfirewall", args[0]);
        Assert.Equal("firewall", args[1]);
        Assert.Equal("add", args[2]);
        Assert.Equal("rule", args[3]);
        Assert.Contains("name=DeskLink-Direct-TCP", args);
        Assert.Contains("dir=in", args);
        Assert.Contains("action=allow", args);
        Assert.Contains("protocol=TCP", args);
        Assert.Contains("localport=47200", args);
    }

    [Theory]
    // 英文 netsh 输出
    [InlineData("Rule Name: X\r\nLocalPort:                            47200\r\n", 47200)]
    // 中文 netsh 输出
    [InlineData("规则名称:  X\r\n本地端口:                            47201\r\n", 47201)]
    // 多端口
    [InlineData("LocalPort:  47200,47201\r\n", 47200)]
    // 没有端口行
    [InlineData("Rule Name: X\r\n", null)]
    public void ParseLocalPort_HandlesLocalisedOutput(string stdout, int? expected)
    {
        Assert.Equal(expected, FirewallHelper.ParseLocalPort(stdout));
    }

    [Fact]
    public void AddRule_Idempotent()
    {
        var netsh = new FakeNetsh();
        var state = new FakeState { Enabled = true, Port = 47200 };
        var helper = Make(netsh, state, elevated: true);

        Assert.True(helper.EnsureInboundRule(FirewallHelper.TcpRuleName, "TCP", 47200));
        Assert.True(helper.EnsureInboundRule(FirewallHelper.TcpRuleName, "TCP", 47200));

        // 幂等做法 = 每次"先删后加"，最终只剩一条同名规则。
        Assert.Single(netsh.Rules);
        Assert.Equal(47200, netsh.Rules[FirewallHelper.TcpRuleName]);
        // 两次调用共 2 次 delete + 2 次 add
        Assert.Equal(2, netsh.Calls.Count(c => c[2] == "delete"));
        Assert.Equal(2, netsh.Calls.Count(c => c[2] == "add"));
    }

    [Fact]
    public void RemoveRule_NotExists_IsIdempotentSuccess()
    {
        var netsh = new FakeNetsh();
        var helper = Make(netsh, new FakeState(), elevated: true);

        // 规则不存在：netsh 返回 1 + "No rules match"，应视为幂等成功。
        Assert.True(helper.RemoveInboundRule("DeskLink-Direct-TCP"));
        Assert.True(helper.RemoveInboundRule("DeskLink-Direct-TCP"));
    }

    [Fact]
    public void PortChange_ReconciledRules()
    {
        var netsh = new FakeNetsh();
        var state = new FakeState { Enabled = true, Port = 47200 };
        var helper = Make(netsh, state, elevated: true);

        // 先按旧端口放行
        Assert.True(helper.SetEnabled(47200, true));
        Assert.Equal(47200, netsh.Rules[FirewallHelper.TcpRuleName]);

        // 端口变更：旧规则清理、新端口生效
        Assert.True(helper.ApplyPortChange(47200, 47201));

        Assert.Equal(47201, netsh.Rules[FirewallHelper.TcpRuleName]);
        Assert.Equal(47201, netsh.Rules[FirewallHelper.UdpRuleName]);
        // 只应存在两条规则（TCP + UDP），没有旧端口残留
        Assert.Equal(2, netsh.Rules.Count);
        Assert.Equal(47201, state.Port);
        Assert.True(state.Enabled);
    }

    [Fact]
    public void SetEnabled_AddsBothTcpAndUdp()
    {
        var netsh = new FakeNetsh();
        var state = new FakeState();
        var helper = Make(netsh, state, elevated: true);

        Assert.True(helper.SetEnabled(47200, true));

        Assert.True(netsh.Rules.ContainsKey(FirewallHelper.TcpRuleName));
        Assert.True(netsh.Rules.ContainsKey(FirewallHelper.UdpRuleName));
        Assert.True(state.Enabled);
        Assert.Equal(47200, state.Port);

        // 放行后 IsPortAllowed 应为 true（走 show 解析）
        Assert.True(helper.IsPortAllowed(47200));
        Assert.False(helper.IsPortAllowed(47201));
    }

    [Fact]
    public void SetEnabled_Disable_RemovesBothRules()
    {
        var netsh = new FakeNetsh();
        var state = new FakeState { Enabled = true, Port = 47200 };
        var helper = Make(netsh, state, elevated: true);
        helper.SetEnabled(47200, true);
        Assert.Equal(2, netsh.Rules.Count);

        Assert.True(helper.SetEnabled(47200, false));

        Assert.Empty(netsh.Rules);
        Assert.False(state.Enabled);
        Assert.False(helper.IsPortAllowed(47200));
    }

    [Fact]
    public void NotElevated_DoesNotTouchFirewallNorRegistry()
    {
        var netsh = new FakeNetsh();
        var state = new FakeState();
        var helper = Make(netsh, state, elevated: false);

        Assert.False(helper.SetEnabled(47200, true));

        // 关键：未提权时不得"假装成功"——既不调 netsh，也不写状态。
        Assert.Empty(netsh.Calls);
        Assert.False(state.Enabled);
        Assert.False(helper.QueryEnabled(47200));
    }

    [Fact]
    public void ApplyPortChange_WhenNotEnabled_OnlyUpdatesRecordedPort()
    {
        var netsh = new FakeNetsh();
        var state = new FakeState { Enabled = false, Port = 47200 };
        var helper = Make(netsh, state, elevated: true);

        Assert.True(helper.ApplyPortChange(47200, 47202));

        Assert.Empty(netsh.Calls);      // 未启用 → 不动防火墙
        Assert.Equal(47202, state.Port);
        Assert.False(state.Enabled);
    }

    [Fact]
    public void ApplyPortChange_NotElevated_ReturnsFalseAndKeepsOldPort()
    {
        var netsh = new FakeNetsh();
        var state = new FakeState { Enabled = true, Port = 47200 };
        var helper = Make(netsh, state, elevated: false);

        Assert.False(helper.ApplyPortChange(47200, 47203));

        Assert.Empty(netsh.Calls);
        Assert.Equal(47200, state.Port);   // 状态未被改坏
    }

    [Fact]
    public void AddFailure_DoesNotRecordEnabled()
    {
        var netsh = new FakeNetsh { SimulateAddFailure = true };
        var state = new FakeState();
        var helper = Make(netsh, state, elevated: true);

        Assert.False(helper.SetEnabled(47200, true));
        Assert.False(state.Enabled);   // 规则没加上就不许把状态标成已启用
    }

    [Fact]
    public void Repair_ReappliesRulesWhenEnabled()
    {
        var netsh = new FakeNetsh();
        var state = new FakeState { Enabled = true, Port = 47200 };
        var helper = Make(netsh, state, elevated: true);

        Assert.True(helper.Repair(47200));
        Assert.True(netsh.Rules.ContainsKey(FirewallHelper.TcpRuleName));
        Assert.True(netsh.Rules.ContainsKey(FirewallHelper.UdpRuleName));
    }

    [Fact]
    public void Repair_SkipsWhenDisabled()
    {
        var netsh = new FakeNetsh();
        var state = new FakeState { Enabled = false, Port = 47200 };
        var helper = Make(netsh, state, elevated: true);

        Assert.True(helper.Repair(47200));
        Assert.Empty(netsh.Calls);
    }
}
