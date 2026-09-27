using System.Windows;

namespace DeskLink.Client.Views;

/// <summary>
/// 对端指纹确认对话框（中继路径 TOFU）。
/// 只有用户显式点"确认并信任"才返回 true；关闭/取消一律视为不信任。
/// </summary>
public partial class FingerprintConfirmDialog : Window
{
    public FingerprintConfirmDialog(string deviceName, string fingerprint)
    {
        InitializeComponent();
        DeviceNameText.Text = deviceName;
        FingerprintText.Text = fingerprint;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
