using System.Windows;
using DeskLink.Client.Services;

namespace DeskLink.Client.Views;

/// <summary>
/// 局域网直连对话框：输入 <c>IP:端口</c>，用 <see cref="DirectEndpoint"/> 校验后才允许确定。
/// </summary>
public partial class DirectConnectDialog : Window
{
    public DirectConnectDialog(string initial = "")
    {
        InitializeComponent();
        EndpointText.Text = initial;
    }

    /// <summary>校验通过的直连目标（未确认时为 default）。</summary>
    public DirectEndpoint Endpoint { get; private set; }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (!DirectEndpoint.TryParse(EndpointText.Text, out var endpoint, out var error))
        {
            ErrorText.Text = error;
            return;
        }

        Endpoint = endpoint;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
