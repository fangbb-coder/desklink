using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskLink.Protocol.Media;

namespace DeskLink.Client.ViewModels;

/// <summary>会话状态（与 DESIGN 的会话状态条一一对应）。</summary>
public enum SessionState
{
    Idle,
    Connecting,
    Established,
    Recovering,
    WaitingForLocalLogin,
    Error,
}

/// <summary>
/// 远程页视图模型：会话状态机 + 统计显示。
///
/// 状态机为什么值得单独建模：DESIGN 明确要求 ACCESS_LOST 期间显示"正在恢复画面"
/// 以避免用户误判掉线，且锁屏时显示"等待本地登录"。这些状态必须**只能**由
/// 受控的迁移产生——若任由各处随意赋状态，很容易出现"明明在恢复却显示已连接"
/// 这种误导性 UI。因此这里用一张迁移表把关，非法迁移直接拒绝并返回 false。
/// </summary>
public sealed class RemoteViewModel : ObservableObject
{
    private static readonly Dictionary<SessionState, SessionState[]> AllowedTransitions = new()
    {
        [SessionState.Idle] = new[] { SessionState.Connecting },
        [SessionState.Connecting] = new[] { SessionState.Established, SessionState.Error, SessionState.WaitingForLocalLogin },
        [SessionState.Established] = new[] { SessionState.Recovering, SessionState.Error, SessionState.Idle },
        // Recovering / WaitingForLocalLogin 也必须允许 → Idle：恢复/等待期间发生
        // 真实断开（用户点断开、媒体管道关闭）时，资源已被清但状态若迁不走，
        // UI 会永远卡在"正在恢复画面…"，与会话实际状态脱节。
        [SessionState.Recovering] = new[] { SessionState.Established, SessionState.Error, SessionState.Idle },
        [SessionState.WaitingForLocalLogin] = new[] { SessionState.Established, SessionState.Error, SessionState.Idle },
        [SessionState.Error] = new[] { SessionState.Connecting, SessionState.Idle },
    };

    private SessionState _state = SessionState.Idle;
    private string _errorMessage = "";
    private double _fps;
    private double _kbps;
    private int _rttMs = -1;
    private bool _degraded;
    private bool _softwareCodec;
    private string _peerLabel = "";
    private WriteableBitmap? _frame;
    private bool _demoMode;

    /// <summary>会话状态变化（App 据此切换 RemoteView / 状态条）。</summary>
    public event Action<SessionState>? StateChanged;

    public SessionState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsEstablished));
                OnPropertyChanged(nameof(IsRecovering));
                OnPropertyChanged(nameof(StateText));
                StateChanged?.Invoke(value);
            }
        }
    }

    public bool IsEstablished => _state == SessionState.Established;

    /// <summary>ACCESS_LOST 重建期间为 true（状态条显示"正在恢复画面"）。</summary>
    public bool IsRecovering => _state == SessionState.Recovering;

    public string PeerLabel
    {
        get => _peerLabel;
        set => SetProperty(ref _peerLabel, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public double Fps
    {
        get => _fps;
        private set => SetProperty(ref _fps, value);
    }

    public double Kbps
    {
        get => _kbps;
        private set => SetProperty(ref _kbps, value);
    }

    public int RttMs
    {
        get => _rttMs;
        private set => SetProperty(ref _rttMs, value);
    }

    /// <summary>自适应降级已生效（降了帧率/码率/分辨率）。</summary>
    public bool Degraded
    {
        get => _degraded;
        private set => SetProperty(ref _degraded, value);
    }

    /// <summary>当前使用软件编码（硬件编码器不可用），DESIGN 要求在会话状态中告知用户。</summary>
    public bool SoftwareCodec
    {
        get => _softwareCodec;
        private set => SetProperty(ref _softwareCodec, value);
    }

    /// <summary>
    /// 远端画面帧。RemoteView 的 Image.Source 绑到这里。
    /// 注意：<see cref="WriteableBitmap"/> 有线程亲和性，<see cref="UpdateFrame"/> 必须在
    /// 创建该位图的线程（UI 线程）上调用——解码在后台线程，必须由调用方 marshal 回 UI。
    /// </summary>
    public WriteableBitmap? Frame
    {
        get => _frame;
        private set => SetProperty(ref _frame, value);
    }

    /// <summary>写入一帧 BGRA32 像素。尺寸变化时重建位图。</summary>
    public void UpdateFrame(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0) return;
        if (bgra.Length < width * height * 4) return;

        if (_frame is null || _frame.PixelWidth != width || _frame.PixelHeight != height)
        {
            _frame = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            OnPropertyChanged(nameof(Frame));
        }

        _frame.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
    }

    /// <summary>
    /// 演示模式：当前显示的是 <see cref="Media.NullFrameSource"/> 合成画面而非真实远端画面。
    /// 显式标注以避免误导用户（Service 侧媒体管道未就绪时 UI 仍可打开）。
    /// </summary>
    public bool DemoMode
    {
        get => _demoMode;
        set => SetProperty(ref _demoMode, value);
    }

    public string StateText => _state switch
    {
        SessionState.Idle => "未连接",
        SessionState.Connecting => "正在连接…",
        SessionState.Established => "已连接",
        SessionState.Recovering => "正在恢复画面…",
        SessionState.WaitingForLocalLogin => "等待本地登录",
        SessionState.Error => "连接错误",
        _ => "未知",
    };

    /// <summary>按迁移表切换状态；非法迁移返回 false 且不改状态。</summary>
    public bool TryTransition(SessionState to)
    {
        if (to == _state) return true;
        if (!AllowedTransitions.TryGetValue(_state, out var allowed) || !allowed.Contains(to))
        {
            return false;
        }

        State = to;
        if (to != SessionState.Error) ErrorMessage = "";
        return true;
    }

    /// <summary>发起连接（Idle/Error → Connecting）。</summary>
    public bool BeginConnect()
    {
        if (!TryTransition(SessionState.Connecting)) return false;
        ErrorMessage = "";
        return true;
    }

    public bool MarkEstablished() => TryTransition(SessionState.Established);

    /// <summary>捕获管线 ACCESS_LOST：进入"正在恢复画面"，不丢控制权。</summary>
    public bool MarkAccessLost() => TryTransition(SessionState.Recovering);

    /// <summary>恢复完成，回到已连接。</summary>
    public bool MarkRecovered() => TryTransition(SessionState.Established);

    /// <summary>被控端锁屏或未登录：显示等待本地登录，不去尝试控制登录界面。</summary>
    public bool MarkWaitingForLocalLogin() => TryTransition(SessionState.WaitingForLocalLogin);

    public bool MarkError(string message)
    {
        if (!TryTransition(SessionState.Error)) return false;
        ErrorMessage = message;
        return true;
    }

    public bool MarkDisconnected()
    {
        if (!TryTransition(SessionState.Idle)) return false;
        ResetStats();
        return true;
    }

    /// <summary>更新统计显示。</summary>
    public void UpdateStats(SessionStatsPayload stats)
    {
        Fps = stats.FpsX10 / 10.0;
        Kbps = stats.Kbps;
        RttMs = stats.RttMs;
        Degraded = stats.Degraded;
    }

    /// <summary>从编码器配置得知当前后端（硬件/软件），软件时在会话状态中标注。</summary>
    public void ApplyDesktopConfig(DesktopConfigPayload config)
    {
        SoftwareCodec = config.Backend == DesktopConfigPayload.BackendSoftware;
    }

    private void ResetStats()
    {
        Fps = 0;
        Kbps = 0;
        RttMs = -1;
        Degraded = false;
    }
}
