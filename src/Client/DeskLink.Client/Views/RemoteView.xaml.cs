using System.Windows.Controls;

namespace DeskLink.Client.Views;

/// <summary>
/// 远程页：远端画面 + 会话内三操作（文件/全屏/断开）。
/// 画面由 ViewModel 通过 WriteableBitmap 喂入；本类不含业务逻辑。
/// </summary>
public partial class RemoteView : UserControl
{
    public RemoteView()
    {
        InitializeComponent();
    }
}
