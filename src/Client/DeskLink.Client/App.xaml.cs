using System.Windows;
using DeskLink.Client.Security;
using DeskLink.Client.Services;
using DeskLink.Client.ViewModels;

namespace DeskLink.Client;

/// <summary>
/// WPF 应用入口。只负责"组装"：解析参数 → 建依赖（指纹库 / Service 门面）→ 建主窗口。
/// 业务逻辑一律在 ViewModel 里，App 不做决策。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = ClientOptions.Parse(e.Args);
        var store = new PeerFingerprintStore();
        var api = new ServiceApi(options.PipeName);
        var vm = new MainViewModel(api, store);

        var window = new MainWindow(vm, options);
        MainWindow = window;
        window.Show();
    }
}
