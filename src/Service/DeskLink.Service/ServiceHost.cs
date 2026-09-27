// DeskLink.Service 宿主生命周期。
//
// 职责：
//   - 在 IHostedService 启动顺序里：先 KeyStore.LoadOrCreate → PipeServer.Start → RelayClient.Start
//   - 优雅关闭：反向顺序
//   - 装配 IServiceCore（管道 RPC 业务实现）
//
// 启动参数：
//   - ServiceOptions（由 Program.cs 解析后传入）
//
// 不在本类做：
//   - 命令行解析（Program）
//   - Windows 服务注册（Program 通过 HostBuilder.UseWindowsService）
//   - QUIC/TCP 选择（RelayClient 内部）
using DeskLink.Protocol.Common;
using DeskLink.Service.Configuration;
using DeskLink.Service.Direct;
using DeskLink.Service.FileTransfer;
using DeskLink.Service.Media;
using DeskLink.Service.Pipe;
using DeskLink.Service.Process;
using DeskLink.Service.Relay;
using DeskLink.Service.Security;
using DeskLink.Service.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using DeskLink.Protocol.Pipe;

namespace DeskLink.Service;

public sealed class ServiceHost : IHostedService
{
    private readonly ServiceOptions _options;
    private readonly ILogger<ServiceHost> _logger;
    private readonly KeyStore _keyStore;
    private readonly PairingStore _pairings;
    private readonly AgentLauncher _agent;
    private readonly FirewallHelper _firewall;
    private readonly PipeServer _pipe;
    private readonly RelayClient? _relay;
    private readonly RelaySessionRunner? _runner;
    private readonly DirectServer? _direct;
    private readonly MediaPipeServer? _media;

    public ServiceHost(
        ServiceOptions options,
        ILogger<ServiceHost> logger,
        KeyStore keyStore,
        PairingStore pairings,
        AgentLauncher agent,
        FirewallHelper firewall,
        PipeServer pipe,
        RelayClient? relay,
        RelaySessionRunner? runner,
        DirectServer? direct,
        MediaPipeServer? media)
    {
        _options = options;
        _logger = logger;
        _keyStore = keyStore;
        _pairings = pairings;
        _agent = agent;
        _firewall = firewall;
        _pipe = pipe;
        _relay = relay;
        _runner = runner;
        _direct = direct;
        _media = media;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("DeskLink.Service starting ({Mode}) {Opts}",
            _options.ConsoleMode ? "console" : "service",
            _options);

        // 1) 加载或生成设备密钥（DESIGN.md：DPAPI LocalMachine 跨进程可解密）
        var keys = _keyStore.LoadOrCreate();
        _logger.LogInformation(
            "KeyStore ready: ed25519_pub={EdPub} hint={Hint}",
            Convert.ToBase64String(keys.KeyPair.Ed25519Public),
            Convert.ToBase64String(keys.DeviceIdHint));

        // 2) 启动管道 RPC
        _pipe.Start();

        // 3) 启动中继连接（如果配置了 relayUrl）
        if (_relay != null)
        {
            // 把 E2E 会话驱动器挂为"传输就绪"回调：RelayClient 每建立一条传输，
            // 都会调用它完成 控制握手 → SIGMA → 加密收发（P4/P5）。
            if (_runner != null)
            {
                _relay.OnReady = _runner.OnTransportReadyAsync;
            }
            _relay.Start();
        }
        else
        {
            _logger.LogInformation("RelayClient disabled (no relayUrl configured)");
        }

        // 4) 启动局域网直连监听（被控端角色，P5.5）
        //
        // 启用条件：显式 --enable-direct，或用户在 Settings/安装时已放行端口
        // （注册表 DirectEnabled=1，与防火墙规则同源）。
        var directEnabled = _options.EnableDirect || _firewall.QueryEnabled(_options.DirectPort);
        if (_direct != null && directEnabled)
        {
            // 为每个直连会话注册控制流探针（与中继路径共用同一实现）。
            _direct.OnSessionEstablished = session =>
            {
                var probe = new SessionControlProbe(
                    msg => _logger.LogInformation("[direct] {Msg}", msg));
                probe.Attach(session.Pump);
                _ = probe.SendProbeAsync();

                // 文件传输引擎（P6）：与中继路径共用同一实现；scope 为空则一律拒绝。
                var fileEngine = new FileTransferEngine(
                    new FileTransferScope(_options.FileScopeRoots),
                    log: msg => _logger.LogInformation("[direct/file] {Msg}", msg));
                fileEngine.Attach(new SessionPumpFileSink(session.Pump));
                session.Pump.Register(ProtocolConstants.LogicalStream.File, fileEngine.OnFrame);
                session.FileEngine = fileEngine;
            };
            try
            {
                await _direct.StartAsync(cancellationToken);
                _logger.LogInformation(
                    "DirectServer enabled on port {Port} (quic={Quic})",
                    _direct.Port, _direct.QuicListening);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DirectServer failed to start on port {Port}", _direct.Port);
            }
        }
        else if (_direct != null)
        {
            _logger.LogInformation(
                "DirectServer disabled (enable with --enable-direct or Settings) port={Port}",
                _options.DirectPort);
        }

        // 5) 启动本地媒体通道转发泵（P8/P9 收尾）
        //
        // 它是"客户端看到真实画面"的唯一通路：客户端连 `{prefix}.Media.{instance}`，
        // 桌面代理连 `{prefix}.AgentMedia.{instance}`，Service 在两者之间转发
        // （画面下行按"丢最旧"、输入上行按"优先丢鼠标移动"，详见 MediaPipeServer）。
        // 没有客户端或没有代理时只是丢帧，不会阻塞任何一方。
        if (_media != null)
        {
            await _media.StartAsync(cancellationToken);
        }

        _logger.LogInformation("DeskLink.Service started");
        return;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("DeskLink.Service stopping");

        if (_media != null)
        {
            await _media.DisposeAsync();
        }

        if (_direct != null)
        {
            await _direct.StopAsync();
        }

        if (_relay != null)
        {
            await _relay.StopAsync();
        }

        await _pipe.StopAsync();
        _agent.Stop();

        _logger.LogInformation("DeskLink.Service stopped");
    }
}
