using System.Windows.Controls;

namespace DeskLink.Client.Views;

/// <summary>
/// 被控端低干扰状态条：显示"正在被控制"与会话统计（fps/kbps/rtt/degraded）。
/// 这是被控端用户看到的东西，因此刻意做成不抢焦点的窄条而非弹窗。
/// </summary>
public partial class StatusBar : UserControl
{
    public StatusBar()
    {
        InitializeComponent();
    }
}
