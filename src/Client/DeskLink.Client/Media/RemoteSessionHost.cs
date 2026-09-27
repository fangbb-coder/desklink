using DeskLink.Client.Services;
using DeskLink.Protocol.Media;

namespace DeskLink.Client.Media;

/// <summary>
/// 把媒体通道的码流接到解码器，产出可供 UI 显示的 BGRA 帧。
///
/// 线程模型（重要）：解码发生在媒体读循环线程（后台），因此
/// <see cref="FrameReady"/> / <see cref="ConfigReceived"/> / <see cref="StatsReceived"/>
/// 都在**后台线程**触发；消费者必须自行 marshal 回 UI 线程再碰 WPF 对象
/// （WriteableBitmap 有线程亲和性）。
/// </summary>
public sealed class RemoteSessionHost : IDisposable
{
    private readonly MediaChannelClient _media;
    private readonly object _gate = new();
    private H264Decoder? _decoder;
    private bool _disposed;

    public RemoteSessionHost(MediaChannelClient media)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));

        _media.DesktopConfigReceived += OnConfig;
        _media.AccessUnitReceived += OnAccessUnit;
        _media.StatsReceived += OnStats;
        _media.Faulted += OnFault;
    }

    /// <summary>解出一帧 BGRA（后台线程触发）。</summary>
    public event Action<BgraFrame>? FrameReady;

    /// <summary>收到编码器/显示器配置（后台线程触发）。</summary>
    public event Action<DesktopConfigPayload>? ConfigReceived;

    /// <summary>收到会话统计（后台线程触发）。</summary>
    public event Action<SessionStatsPayload>? StatsReceived;

    /// <summary>解码/通道错误（后台线程触发）。</summary>
    public event Action<Exception>? Faulted;

    /// <summary>当前解码后端名（"software: xxx"）；未就绪为空。</summary>
    public string DecoderBackend { get; private set; } = "";

    private void OnConfig(DesktopConfigPayload config)
    {
        lock (_gate)
        {
            // 分辨率/编码参数变化（含 DXGI 模式切换）时必须重配解码器，否则解出来是花屏。
            if (_decoder is null || _decoder.Width != config.Width || _decoder.Height != config.Height)
            {
                _decoder?.Dispose();
                _decoder = H264Decoder.TryCreate(config.Width, config.Height, out var error);
                if (_decoder is null)
                {
                    DecoderBackend = "";
                    Faulted?.Invoke(new InvalidOperationException(error ?? "H.264 解码器不可用"));
                }
                else
                {
                    DecoderBackend = _decoder.BackendName;
                }
            }
        }

        ConfigReceived?.Invoke(config);
    }

    private void OnAccessUnit(VideoAccessUnit unit)
    {
        H264Decoder? decoder;
        lock (_gate)
        {
            decoder = _decoder;
            // 首帧前若没收到 DesktopConfig，用默认尺寸惰性建解码器（SPS 会给出真实尺寸）。
            if (decoder is null)
            {
                decoder = H264Decoder.TryCreate(1920, 1080, out var error);
                if (decoder is null)
                {
                    Faulted?.Invoke(new InvalidOperationException(error ?? "H.264 解码器不可用"));
                    return;
                }
                _decoder = decoder;
                DecoderBackend = decoder.BackendName;
            }
        }

        try
        {
            var frame = decoder.Decode(unit.Data);
            if (frame is not null)
            {
                FrameReady?.Invoke(frame);
            }
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }
    }

    private void OnStats(SessionStatsPayload stats) => StatsReceived?.Invoke(stats);

    private void OnFault(Exception ex) => Faulted?.Invoke(ex);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _media.DesktopConfigReceived -= OnConfig;
        _media.AccessUnitReceived -= OnAccessUnit;
        _media.StatsReceived -= OnStats;
        _media.Faulted -= OnFault;

        lock (_gate)
        {
            _decoder?.Dispose();
            _decoder = null;
        }
    }
}
