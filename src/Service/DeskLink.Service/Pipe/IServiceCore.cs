// 管道处理器接口（PipeServer → 业务层解耦）。
//
// 业务层实现持有 KeyStore / PairingStore / RelayClient / AgentLauncher 引用；
// 管道层只关心"方法名 → 参数 → 结果"的派发。
// 单测可以注入桩实现，不依赖真实 KeyStore。
using System.Text.Json;

using DeskLink.Protocol.Pipe;

namespace DeskLink.Service.Pipe;

public interface IServiceCore
{
    DeviceInfoResult GetDeviceInfo();
    SignChallengeResult SignChallenge(ReadOnlySpan<byte> challenge);
    StatusResult GetStatus();
    ListPairingsResult ListPairings();
    void Pair(ReadOnlySpan<byte> peerPub, string label);
    void Unpair(ReadOnlySpan<byte> peerPub);
    GetConfigResult GetConfig();
    SetConfigResult SetConfig(string? relayUrl, int? directPort);
    StartAgentResult StartAgent(bool inject, bool noInject, string? pipeOverride, string? mediaPipe = null);
    void StopAgent();

    /// <summary>
    /// 控制端主动拨号到局域网对端（P5.5 出站方向）。
    ///
    /// 这是 WPF 客户端"局域网直连"路径的**唯一**实际拨号入口：没有它，
    /// 客户端只能校验 IP:端口然后显示"已连接"，而对端从未被连上。
    /// 方法在 SIGMA 完成、收发泵启动后返回 true；会话随后在后台维持。
    /// </summary>
    Task<DirectDialResultDto> DialDirectAsync(string peerPubB64, string host, int port, CancellationToken ct = default);

    /// <summary>被控端"随时断开"（DESIGN 使用流程第 5 条）：关闭本机全部 E2E 会话。</summary>
    EndSessionResult EndSession();

    // —— 文件传输（P6）——
    //
    // 这些是**异步**方法：文件传输要等对端 ack / 校验，不能阻塞管道派发线程。
    FileScopeResult GetFileScope();
    Task<FileListResultDto> ListRemoteFilesAsync(string path, CancellationToken ct = default);
    Task<FileTransferResultDto> UploadFileAsync(string local, string remote, string policy, CancellationToken ct = default);
    Task<FileTransferResultDto> DownloadFileAsync(string remote, string local, string policy, CancellationToken ct = default);
}
