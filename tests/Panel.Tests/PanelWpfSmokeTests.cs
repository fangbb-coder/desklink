using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Diagnostics;
using System.Windows.Threading;
using DeskLink.Panel;
using DeskLink.Panel.ViewModels;
using Xunit;

namespace DeskLink.Panel.Tests;

/// <summary>
/// 面板窗口的 WPF 冒烟。
///
/// ## 为什么要逐页渲染
///
/// 把控制端功能搬进「我是主控端」选项卡时，**非选中页的内容根本不在视觉树里**
/// （TabControl 只把选中项放进 ContentPresenter），只渲染一次等于只检查了半边界面。
/// 这里对每一个选项卡都切过去各渲染一遍——没被点开过的那一页最容易藏绑定写错。
///
/// ## 为什么必须真 Show()（2026-09-29 在 DeskLink.Client 上血的教训）
///
/// WPF 的数据绑定是**惰性**的：视觉树没被度量/渲染时，`<Run.Text>` 上的绑定根本不解析。
/// 真实事故：Client 把绑定放在 `<Run.Text>`（默认 TwoWay）却指向只读属性，
/// 启动即抛 InvalidOperationException 整个进程终止，而当时 120 个单测全绿。
/// 所以这里一律 `Show()` + 跑 `ContextIdle`/`Loaded` + `UpdateLayout()`。
///
/// ## 这条渲染冒烟抓得到什么、抓不到什么（实测结论，别再改回去）
///
/// - 抓得到：绑定模式写错这类**硬异常**（渲染时直接抛 InvalidOperationException）。
///   变异「把 `&lt;Run Text&gt;` 的 Mode=OneWay 去掉」会让本类 6 条用例一起变红。
/// - **抓不到**：绑到一个不存在的属性。Release 下 WPF 静默失败，
///   `PresentationTraceSources.DataBindingSource` 一个字都不吐（变异
///   `PublicKey → PublicKeyTypo` 实测本类全绿）。所以属性名对不对只能靠
///   <see cref="Xaml_每个绑定的目标属性都必须真实存在"/> 静态查。
///   监听器仍留着：它零成本，且在别的 .NET 版本上可能会吐。
public class PanelWpfSmokeTests
{
    // ── 渲染 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 主窗口逐个选项卡渲染_无绑定错误()
    {
        var r = Render(window =>
        {
            // 走一遍真实的初始化路径：公钥、地址、防火墙、状态全都会被填上并被绑定消费。
            var vm = (MainViewModel)window.DataContext;
            vm.InitializeAsync().GetAwaiter().GetResult();
        });

        Assert.True(r.Failure is null,
            $"MainWindow 渲染失败：{r.Failure?.GetType().Name} / {r.Failure?.Message}");
        Assert.True(string.IsNullOrWhiteSpace(r.BindingErrors),
            $"MainWindow 存在数据绑定错误：{r.BindingErrors}");
    }

    [Fact]
    public void 点选卡会把角色同步给ViewModel()
    {
        // 界面能看对还不够：SelectedIndex 必须 TwoWay 落到 SelectedRoleIndex 上，
        // 否则用户点了「我是主控端」，一键准备却按被控端的参数去跑。
        var r = Render(
            window =>
            {
                var tab = FindTabControl(window) ?? throw new InvalidOperationException("没找到 TabControl");
                var vm = (MainViewModel)window.DataContext;
                vm.InitializeAsync().GetAwaiter().GetResult();

                tab.SelectedIndex = 0;   // 我是主控端
                Pump(window);
            },
            readBack: window => ((MainViewModel)window.DataContext).SelectedRoleIndex);

        Assert.True(r.Failure is null, $"切换选项卡失败：{r.Failure?.Message}");
        Assert.Equal(0, r.Value);
    }

    // ── 选项卡结构 ────────────────────────────────────────────────────────

    [Fact]
    public void 两个角色选项卡都存在且主控端在左()
    {
        Assert.Equal(new[] { "我是主控端", "我是被控端" }, Snapshot().Headers);
    }

    [Fact]
    public void 控制端专属功能都在主控端选项卡下()
    {
        var s = Snapshot();

        // 主控端页 = 用户说的"控制界面所有功能"：一键准备、给出自己的公钥、配对、打开控制界面
        Assert.Contains("一键准备主控端", s.ControllerButtons);
        Assert.Contains("配对", s.ControllerButtons);
        Assert.Contains("打开控制界面", s.ControllerButtons);

        // 被控端页：放行入站端口 + 一键准备 + 把公钥/地址发给对方
        Assert.Contains("一键准备被控端", s.ControlledButtons);
        Assert.Contains("放行入站端口", s.ControlledButtons);

        // 两边都不该出现对方的专属按钮
        Assert.DoesNotContain("放行入站端口", s.ControllerButtons);
        Assert.DoesNotContain("打开控制界面", s.ControlledButtons);
    }

    [Fact]
    public void 被控端页带着要发给主控端的公钥与地址()
    {
        var s = Snapshot();

        Assert.Contains("本机公钥（Base64）", s.ControlledTexts);
        Assert.Contains(s.ControlledTexts, t => t.StartsWith("本机地址", StringComparison.Ordinal));

        // 主控端页只需要自己的公钥（让对方配回来），不需要本机地址
        Assert.Contains("本机公钥（Base64）", s.ControllerTexts);
        Assert.DoesNotContain(s.ControllerTexts, t => t.StartsWith("本机地址", StringComparison.Ordinal));
    }

    [Fact]
    public void 本机服务与高级设置放在选项卡之外_两个角色共用()
    {
        var s = Snapshot();

        foreach (var texts in new[] { s.ControllerTexts, s.ControlledTexts })
        {
            Assert.DoesNotContain("启动服务", texts);
            Assert.DoesNotContain("停止服务", texts);
            Assert.DoesNotContain(texts, t => t.StartsWith("中继地址", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, t => t.StartsWith("文件共享目录", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void 被控端也有对方的公钥输入框和配对按钮()
    {
        // 审文案时发现的流程断点：主控端页写着"到被控端页粘进『对方的公钥』输入框再点配对"，
        // 而被控端页当时**根本没有那个输入框**。直连握手是双向 SIGMA，只配一边连不上，
        // 于是"被控端一键准备"完全没配对 → 用户按提示操作仍然失败。
        var s = Snapshot();

        Assert.Contains(s.ControlledTexts, t => t.StartsWith("对方的公钥", StringComparison.Ordinal));
        Assert.Contains("配对", s.ControlledButtons);
    }

    [Fact]
    public void 两页的对方公钥输入框绑的是同一个字段()
    {
        // 两边必须共用 PeerPub，而不是各写各的——否则用户在一页填了、切到另一页又是空的。
        var text = File.ReadAllText(LocatePanelXaml());
        var peerBindings = ExtractBindings(text).Where(b => b.Path == "PeerPub").ToList();

        Assert.Equal(2, peerBindings.Count);
    }

    [Fact]
    public void Quic那行绑定状态而不是写死可用()
    {
        // 以前这里是硬编码的"QUIC 可用：…"，本机根本不支持时也在说可用——UI 撒谎。
        var text = File.ReadAllText(LocatePanelXaml());
        Assert.Contains(ExtractBindings(text), b => b.Path == "QuicText");
        Assert.DoesNotContain("QUIC 可用：", text);
    }

    [Fact]
    public void 提权提示在两处说法一致()
    {
        // 实际行为是 runas 重启自己：UAC 弹窗 + 面板重启。旧文案一处说"弹出提权提示"、
        // 一处说"提权重启"，都不完整也互相矛盾。
        var s = Snapshot();
        var all = s.ControllerTexts.Concat(s.ControlledTexts).ToList();

        Assert.Contains(all, t => t.Contains("UAC", StringComparison.Ordinal));
        Assert.Contains(all, t => t.Contains("以管理员身份重新打开", StringComparison.Ordinal));
    }

    [Fact]
    public void 角色称呼统一用主控端_不混用控制端()
    {
        var s = Snapshot();
        foreach (var t in s.ControllerTexts.Concat(s.ControlledTexts))
        {
            Assert.DoesNotContain("控制端", t);
        }
    }

    // ── 静态护栏（不需要 STA 线程，任何环境都能跑的第一道网）──────────────

    [Fact]
    public void Xaml_两个一键准备按钮必须绑Command_不能绑Click()
    {
        // 这两个 Command 的 CanExecute 是 `!IsBusy`，本来就是为了防重入。
        // 绑 Click 等于把这个保护整个绕开：连点两次，第二次照样重跑流程，
        // 照样弹一句"已就绪"，而服务根本没起来。
        var xaml = StripXmlComments(File.ReadAllText(LocatePanelXaml()));

        AssertPrepareButtonUsesCommand(xaml, "一键准备主控端", "PrepareControllerCommand");
        AssertPrepareButtonUsesCommand(xaml, "一键准备被控端", "PrepareControlledCommand");
    }

    private static void AssertPrepareButtonUsesCommand(string xaml, string content, string command)
    {
        var at = xaml.IndexOf($"Content=\"{content}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"XAML 里找不到按钮「{content}」");

        // 就近往后 300 字符内必须出现 Command="{Binding <command>}"，且不能出现 Click=
        var window = xaml.Substring(at, Math.Min(300, xaml.Length - at));
        Assert.Contains($"Command=\"{{Binding {command}}}\"", window);
        Assert.DoesNotContain("Click=", window);
    }

    [Fact]
    public void OnClosing_必须先问再拆_不能让用户点否之后变僵尸()
    {
        // 拆排在确认之前的后果：用户点「否」→ e.Cancel 掉了窗口，
        // 可定时器已经 Stop、事件已经摘 → 状态永远不刷新、日志不再滚动，
        // 而且没有任何地方会把它们装回去。窗口还在，但已经不会动了。
        var src = File.ReadAllText(LocatePanelSource("MainWindow.xaml.cs"));
        var start = src.IndexOf("OnClosing", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 OnClosing");

        var body = src[start..];
        var stopAt = body.IndexOf("_statusTimer?.Stop()", StringComparison.Ordinal);
        var cancelAt = body.IndexOf("e.Cancel = true", StringComparison.Ordinal);
        var unsubAt = body.IndexOf("CollectionChanged -= OnLogChanged", StringComparison.Ordinal);

        Assert.True(stopAt >= 0, "OnClosing 里应有 _statusTimer?.Stop()");
        Assert.True(cancelAt >= 0, "OnClosing 里应有 e.Cancel = true");
        Assert.True(unsubAt >= 0, "OnClosing 里应有摘事件");
        Assert.True(stopAt > cancelAt, "Stop 定时器必须排在 e.Cancel = true 之后");
        Assert.True(unsubAt > cancelAt, "摘事件必须排在 e.Cancel = true 之后");
    }

    [Fact]
    public void ViewModel_命令异常必须有人接_不能变成没人观察的Task异常()
    {
        // 改绑 Command 之后，MainWindow 那边就没有 RunGuarded 可兜底了。
        // 兜底随之下沉到 ViewModel，这里盯着它还在。
        var src = File.ReadAllText(LocatePanelSource("ViewModels\\MainViewModel.cs"));
        Assert.Contains("CommandFailed", src);
        Assert.Contains("catch (Exception ex)", src);
    }

    [Fact]
    public void Xaml_每个绑定的目标属性都必须真实存在()
    {
        // 这条是**实测换来的**：渲染时挂 PresentationTraceSources.DataBindingSource 监听器
        // 抓不到"绑到不存在的属性"——Release 下 WPF 静默失败，trace 一个字都不吐
        // （已用 PublicKeyTypo 变异验证：渲染冒烟全绿）。
        // 所以属性名对不对只能静态查，而且必须查全量选项卡——TabControl 只渲染选中页。
        foreach (var (path, _, _, tag, attr) in ExtractBindings(File.ReadAllText(LocatePanelXaml())))
        {
            _ = typeof(MainViewModel).GetProperty(path)
                ?? throw new Xunit.Sdk.XunitException(
                    $"MainWindow.xaml 的 <{tag} {attr}=\"{{Binding {path}}}\"> 绑到了 MainViewModel 上" +
                    $"不存在的属性「{path}」。绑定会静默失效，界面上恒为空。");
        }
    }

    [Fact]
    public void Xaml_默认TwoWay的绑定目标必须能接受写回()
    {
        // <Run.Text> 的默认绑定模式是 **TwoWay**（不是 TextBlock.Text 的 OneWay 直觉），
        // TextBox.Text / CheckBox.IsChecked / Selector.SelectedIndex 同理。
        // 绑到只读属性（get-only 或 private setter）→ 运行时抛 InvalidOperationException。
        // 真实事故：Client 的 SettingsView 把 ActiveRelayUrl 这样绑，DeskLink.Client.exe 每次启动即崩，
        // 而当时 120 个单测全绿、链路演练也全过。
        foreach (var (path, mode, _, tag, attr) in ExtractBindings(File.ReadAllText(LocatePanelXaml())))
        {
            var effective = mode is not null
                ? (mode == "OneWay" ? BindingMode.OneWay : mode == "TwoWay" ? BindingMode.TwoWay : (BindingMode?)null)
                : DefaultBindingMode(tag, attr);
            if (effective != BindingMode.TwoWay) continue;

            var prop = typeof(MainViewModel).GetProperty(path)
                ?? throw new Xunit.Sdk.XunitException(
                    $"MainWindow.xaml 的 <{tag} {attr}=\"{{Binding {path}}}\"> 绑到了不存在的属性：{path}");

            Assert.True(prop.GetSetMethod(nonPublic: false) is not null,
                $"MainWindow.xaml 的 <{tag} {attr}> 默认就是 TwoWay 绑定，却指向了没有 public setter 的" +
                $"「{path}」。运行时会抛 InvalidOperationException（真机表现：窗口一开就崩）。" +
                "要么显式写 Mode=OneWay，要么给它加 public setter。");
        }
    }

    [Fact]
    public void Xaml_没有把字面量和Binding混写在同一个Text属性里()
    {
        // WPF 的坑：一个属性值里同时出现字面量和 {Binding ...} 时，**整串按字面量处理**，
        // 界面上会原样显示 "{Binding PairingCount}"。真机上就这么被用户拍过照。
        // 本项目的规矩：Text= 要么是纯字面量，要么是整串一个绑定；混合就拆成多个 <Run>。
        // 扫之前必须剥掉 XML 注释——MainWindow.xaml 里正好有一段注释，
        // 里面**故意**写了"不能这么写"的反面教材样例，不剥掉会把护栏自己判成违规。
        var xaml = StripXmlComments(File.ReadAllText(LocatePanelXaml()));

        int i = 0;
        while (true)
        {
            int start = xaml.IndexOf("Text=\"", i, StringComparison.Ordinal);
            if (start < 0) break;
            int end = xaml.IndexOf('"', start + 6);
            if (end < 0) break;

            var value = xaml[(start + 6)..end];
            if (value.Contains("{Binding", StringComparison.Ordinal) && !IsWholeBinding(value))
            {
                Assert.Fail(
                    $"MainWindow.xaml 里 Text=\"{value}\" 把字面量和 {{Binding}} 混在了一起，" +
                    "界面上会原样显示 {Binding ...}。请拆成多个 <Run>，或让 Text= 整串是一个绑定。");
            }
            i = end + 1;
        }
    }

    [Fact]
    public void Xaml_选项卡是双向绑定_点了会同步回角色()
    {
        // 只断言"界面摆对了"不够：SelectedIndex 必须是 TwoWay 落到 SelectedRoleIndex 上，
        // 否则用户点了「我是主控端」，一键准备却按被控端的参数去跑（变异 F 已验证会变红）。
        var xaml = StripXmlComments(File.ReadAllText(LocatePanelXaml()));
        Assert.Contains("SelectedIndex=\"{Binding SelectedRoleIndex, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.True(typeof(MainViewModel).GetProperty(nameof(MainViewModel.SelectedRoleIndex))?.GetSetMethod(nonPublic: false) is not null,
            "SelectedRoleIndex 必须有 public setter，选项卡才能写回来。");
    }

    // ── 结构快照 ──────────────────────────────────────────────────────────

    /// <summary>在 STA 线程上把两个选项卡里的按钮标题/文字文本**取成字符串**再带回调用线程。</summary>
    private sealed record TabSnapshot(
        string[] Headers,
        string[] ControllerButtons, string[] ControllerTexts,
        string[] ControlledButtons, string[] ControlledTexts);

    private static TabSnapshot Snapshot()
    {
        TabSnapshot? snapshot = null;

        Render(window =>
        {
            var tabs = FindTabControl(window)?.Items.OfType<TabItem>().ToList();
            if (tabs is null || tabs.Count < 2) return;

            snapshot = new TabSnapshot(
                tabs.Select(t => (t.Header as string) ?? "").ToArray(),
                ButtonsOf(tabs[0]), TextsOf(tabs[0]),
                ButtonsOf(tabs[1]), TextsOf(tabs[1]));
        });

        Assert.NotNull(snapshot);
        return snapshot!;
    }

    private static string[] ButtonsOf(TabItem tab) =>
        LogicalDescendants(tab.Content as DependencyObject)
            .OfType<Button>().Select(b => b.Content as string ?? "").ToArray();

    private static string[] TextsOf(TabItem tab) =>
        LogicalDescendants(tab.Content as DependencyObject)
            .OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();

    // ── 工具 ──────────────────────────────────────────────────────────────

    private static bool IsWholeBinding(string value) =>
        value.StartsWith('{') && value.EndsWith('}') && !value[1..^1].Contains('{');

    /// <summary>
    /// 去掉 XML 注释再扫静态护栏——MainWindow.xaml 里正好有一段注释，
    /// 里面**故意**写了"不能这么写"的反面教材样例，不剥掉会把护栏自己判成违规。
    /// </summary>
    private static string StripXmlComments(string xaml)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < xaml.Length)
        {
            int start = xaml.IndexOf("<!--", i, StringComparison.Ordinal);
            if (start < 0) { sb.Append(xaml, i, xaml.Length - i); break; }

            sb.Append(xaml, i, start - i);
            int end = xaml.IndexOf("-->", start, StringComparison.Ordinal);
            if (end < 0) break;
            i = end + 3;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 抽出 XAML 里所有 <c>{Binding ...}</c>，并带上它们所在的元素名/属性名，
    /// 好让"这个目标默认是不是 TwoWay"能被静态判定。
    /// </summary>
    private static List<(string Path, string? Mode, bool IsRunText, string Tag, string Attr)>
        ExtractBindings(string xaml)
    {
        var result = new List<(string, string?, bool, string, string)>();
        int i = 0;
        while (true)
        {
            int start = xaml.IndexOf("{Binding", i, StringComparison.Ordinal);
            if (start < 0) break;

            // 花括号配对：参数里可能嵌套 {StaticResource Xxx}，正则没法直接切
            int depth = 0, j = start;
            for (; j < xaml.Length; j++)
            {
                if (xaml[j] == '{') depth++;
                else if (xaml[j] == '}' && --depth == 0) break;
            }
            if (j >= xaml.Length) break;

            var parts = xaml[(start + "{Binding".Length)..j].Split(',');
            var path = parts[0].Trim();
            string? mode = null;
            foreach (var raw in parts.Skip(1))
            {
                var part = raw.Trim();
                if (part.StartsWith("Path=", StringComparison.Ordinal)) path = part[5..].Trim();
                if (part.StartsWith("Mode=", StringComparison.Ordinal)) mode = part[5..].Trim();
            }

            var (tag, attr) = Enclosing(xaml, start);
            result.Add((path, mode, tag.StartsWith("Run", StringComparison.Ordinal), tag, attr));
            i = j + 1;
        }
        return result;
    }

    /// <summary>找出某个 {Binding} 所在的元素名与属性名，例如 (&lt;Run, "Text")。</summary>
    private static (string Tag, string Attr) Enclosing(string xaml, int at)
    {
        // 情况一：属性值里 Attr="{Binding ...}" —— 取紧邻的那个标签与属性名
        int quote = xaml.LastIndexOf('"', at);
        if (quote >= 2 && xaml[quote - 1] == '=')
        {
            int nameEnd = quote - 1, nameStart = nameEnd;
            while (nameStart > 0 && (char.IsLetterOrDigit(xaml[nameStart - 1]) || xaml[nameStart - 1] is '_' or '.')) nameStart--;
            int lt = xaml.LastIndexOf('<', nameStart);
            if (lt >= 0) return (TagName(xaml, lt), xaml[nameStart..nameEnd]);
        }

        // 情况二：元素内容里 <Run.Text>{Binding ...}</Run.Text>
        int lt2 = xaml.LastIndexOf('<', at);
        return lt2 >= 0 ? (TagName(xaml, lt2), "Text") : ("?", "?");
    }

    private static string TagName(string xaml, int lt)
    {
        int p = lt + 1;
        while (p < xaml.Length && !char.IsWhiteSpace(xaml[p]) && xaml[p] is not ('/' or '>')) p++;
        return xaml[(lt + 1)..p];
    }

    /// <summary>
    /// WPF 里这些目标属性**默认就是 TwoWay**（DefaultBindingMode = TwoWay），
    /// 漏写 Mode=OneWay 就会往 source 回写，撞上只读属性就抛异常。
    /// 其余目标走 BindingMode.Default = OneWay，安全。
    /// </summary>
    private static BindingMode? DefaultBindingMode(string tag, string attr) => (tag, attr) switch
    {
        ("Run", "Text") or ("Run.Text", "Text") => BindingMode.TwoWay,
        ("TextBox", "Text") or ("TextBox", "SelectedText") => BindingMode.TwoWay,
        ("CheckBox", "IsChecked") or ("ToggleButton", "IsChecked") => BindingMode.TwoWay,
        ("TabControl", "SelectedIndex") or ("TabControl", "SelectedItem") => BindingMode.TwoWay,
        ("ComboBox", "Text") or ("ComboBox", "SelectedItem") => BindingMode.TwoWay,
        ("PasswordBox", "Password") => BindingMode.TwoWay,
        _ => null,
    };

    private static string LocatePanelXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Tools", "DeskLink.Panel", "MainWindow.xaml");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到 src\\Tools\\DeskLink.Panel\\MainWindow.xaml，静态护栏无法运行。");
    }

    private static string LocatePanelSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Tools", "DeskLink.Panel", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"找不到 src\\Tools\\DeskLink.Panel\\{fileName}，静态护栏无法运行。");
    }

    private static TabControl? FindTabControl(DependencyObject root) =>
        LogicalDescendants(root).OfType<TabControl>().FirstOrDefault();

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject? root)
    {
        if (root is null) yield break;
        // LogicalTreeHelper.GetChildren 返回非泛型 IEnumerable，元素要自己收窄
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dep) continue;
            yield return dep;
            foreach (var grand in LogicalDescendants(dep)) yield return grand;
        }
    }

    private sealed record RenderResult(Exception? Failure, string BindingErrors, object? Value);

    /// <summary>
    /// 在 STA 线程上 <c>Show()</c> 窗口，并且**逐个选项卡**都渲染一遍。
    /// 窗口摆到屏幕外（Left/Top = -32000）避免干扰真机桌面。
    /// </summary>
    private static RenderResult Render(
        Action<Window> beforePump, Func<Window, object?>? readBack = null, int timeoutSeconds = 30)
    {
        Exception? failure = null;
        var bindingErrors = new StringBuilder();
        object? value = null;

        var thread = new Thread(() =>
        {
            // 绑定错误默认只进 trace 不抛异常，但"绑到不存在的属性"正是要拦的。
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Clear();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener(bindingErrors));

            Window? window = null;
            try
            {
                window = NewPanelWindow();
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Show();
                Pump(window);

                var tab = FindTabControl(window);
                int pages = tab?.Items.Count ?? 0;
                for (int index = 0; index < pages; index++)
                {
                    tab!.SelectedIndex = index;
                    Pump(window);
                }

                beforePump(window);
                Pump(window);
                value = readBack?.Invoke(window);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Clear();
                try { window?.Close(); } catch (Exception) { /* 无头宿主可能关不掉 */ }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(timeoutSeconds)), "STA 线程渲染窗口超时");
        return new RenderResult(failure, bindingErrors.ToString(), value);
    }

    private static Window NewPanelWindow() =>
        // startServicePolling: false —— 不起 2 秒轮询、Loaded 时也不自检，
        // 否则冒烟测试会真的去拉起 DeskLink.Service 子进程。
        new MainWindow(new FakeServiceHost(), new PanelSettings { DataDir = @"C:\dl-smoke" }, startServicePolling: false);

    private static void Pump(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.UpdateLayout();
    }

    private sealed class BindingErrorListener : TraceListener
    {
        private readonly StringBuilder _sink;
        public BindingErrorListener(StringBuilder sink) => _sink = sink;
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _sink.AppendLine(message);
        }
    }
}
