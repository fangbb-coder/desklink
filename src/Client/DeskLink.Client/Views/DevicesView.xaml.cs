using System.Windows;
using System.Windows.Controls;
using DeskLink.Client.ViewModels;

namespace DeskLink.Client.Views;

/// <summary>设备页（配对/列表/路径选择/撤销）。逻辑全在 DevicesViewModel。</summary>
public partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 打开 <see cref="DirectConnectDialog"/> 让用户输入被控端 IP:端口。
    ///
    /// 这个对话框此前在整个客户端**从未被实例化过**——类写好了、注释也写了，
    /// 但没有任何入口，导致"局域网直连"只能手敲地址文本框。
    /// 对话框只做校验与回填；真正拨号发生在 DevicesViewModel.ConnectLanAsync
    /// （direct_dial RPC），因此这里不直接碰 Service。
    /// </summary>
    private void OnPickLanEndpoint(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DevicesViewModel vm) return;

        var owner = Window.GetWindow(this);
        var dlg = new DirectConnectDialog(vm.LanEndpointText);
        if (owner is not null) dlg.Owner = owner;

        if (dlg.ShowDialog() == true)
        {
            vm.LanEndpointText = dlg.Endpoint.ToString();
        }
    }
}
