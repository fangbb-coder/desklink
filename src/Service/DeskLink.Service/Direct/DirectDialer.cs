// P5.5 局域网直连**出站**拨号（控制端角色）。
//
// 为什么需要这个类（此前缺失，WPF 客户端的直连入口因此是"假接线"）：
//   DirectServer 只负责**被控端监听**（谁连我）。控制端要主动连别人时，
//   唯一能建立直连会话的入口是 `--direct-probe` 这个一次性 CLI——它拨完就退出，
//   会话随之结束。WPF 客户端点了"局域网直连"之后没有任何东西去拨号，
//   于是 UI 显示"已连接"而画面永远黑屏。
//
// 职责：
//   - 按 (host, port) 主动连到被控端：QUIC 优先，失败回落 TCP/TLS。
//   - 拨号前先校验对端**在本机配对列表里**（直连不复用 relay 挑战，
//     身份根来源就是配对公钥；本地没配对就没必要浪费一次往返）。
//   - 复用 DirectClient 完成 SIGMA，返回的 pump 交给宿主挂业务处理器。
//   - 维护出站会话表：同对端重复拨号顶替旧会话；撤销 / end_session 可关闭。
//
// 生命周期与 DirectServer.RunE2EAsync 保持一致：
//   处理器挂好 → pump.Start → 等 Completion → 释放引擎/pump/session。
//
// 与 DirectServer 的分工：DirectServer 管"入站会话"，本类管"出站会话"。
// 两者各自持有一张会话表，ServiceCore 在撤销 / end_session / 取文件引擎时
// 遍历两者（见 ServiceCore.AllDirectSessions）。

using DeskLink.Protocol.Session;
using DeskLink.Service.Configuration;
using DeskLink.Service.Security;
using DeskLink.Service.Session;

namespace DeskLink.Service.Direct;

/// <summary>出站直连的拨号结果。</summary>
public sealed class DirectDialOutcome
{
    public bool Ok { get; init; }

    /// <summary>失败原因（已本地化，可直接展示给用户）。</summary>
    public string? Detail { get; init; }

    /// <summary>实际使用的传输："quic" / "tcp-tls"。</summary>
    public string? Transport { get; init; }
}

/// <summary>控制端出站直连拨号器。</summary>
public sealed class DirectDialer : IAsyncDisposable
{
    private readonly KeyStore _keys;
    private readonly PairingStore _pairings;
    private readonly ServiceOptions _options;
    private readonly Action<string>? _log;
    private readonly DirectClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<DirectSession> _sessions = new();
    private readonly object _sessionsGate = new();

    public DirectDialer(
        KeyStore keys,
        PairingStore pairings,
        ServiceOptions options,
        Action<string>? log = null)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _pairings = pairings ?? throw new ArgumentNullException(nameof(pairings));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
        _client = new DirectClient(keys, log);
    }

    /// <summary>
    /// 会话建立回调（在 pump.Start 之前触发），与 <see cref="DirectServer.OnSessionEstablished"/>
    /// 同签名。宿主（ServiceHost）在此挂控制流探针与文件传输引擎。
    /// </summary>
    public Action<DirectSession>? OnSessionEstablished { get; set; }

    /// <summary>当前出站活跃会话（只读快照）。</summary>
    public IReadOnlyList<DirectSession> Sessions
    {
        get { lock (_sessionsGate) { return _sessions.ToList(); } }
    }

    /// <summary>
    /// 拨号到指定对端。**方法返回即代表会话已建立**（SIGMA 完成、处理器已挂好、
    /// 收发泵已启动）；会话随后在后台维持，结束或被撤销时自动清理。
    /// </summary>
    /// <param name="peerDeviceId">对端 device_id（32 字节）。</param>
    public async Task<DirectDialOutcome> DialAsync(
        string host,
        int port,
        byte[] peerDeviceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new DirectDialOutcome { Ok = false, Detail = "直连地址为空" };
        }
        if (port is < 1 or > 65535)
        {
            return new DirectDialOutcome { Ok = false, Detail = $"直连端口越界：{port}（应为 1..65535）" };
        }
        if (peerDeviceId is null || peerDeviceId.Length != 32)
        {
            return new DirectDialOutcome { Ok = false, Detail = "对端 device_id 非法" };
        }

        // 本地未配对就不必浪费一次往返去让对端拒绝我们。
        if (!IsPaired(peerDeviceId))
        {
            return new DirectDialOutcome
            {
                Ok = false,
                Detail = "该设备不在本机配对列表中，请先完成配对",
            };
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        // 与 DirectClient 内部的 SIGMA 超时（15s）协同：传输层失败/超时都会被
        // 归一化成 Ok=false + Detail，不会把异常抛到管道派发线程。
        linked.CancelAfter(TimeSpan.FromSeconds(20));

        var conn = await _client
            .ConnectAsync(host, port, peerDeviceId, linked.Token, startPump: false)
            .ConfigureAwait(false);

        if (!conn.Ok || conn.Pump is null || conn.Session is null)
        {
            return new DirectDialOutcome { Ok = false, Detail = conn.Detail ?? "直连失败" };
        }

        var session = new DirectSession(peerDeviceId, conn.Pump);
        var e2eSession = conn.Session;

        // 顶替同对端的旧出站会话：同一设备重复拨号只保留最新一条，避免旧连接泄漏。
        List<DirectSession> superseded;
        lock (_sessionsGate)
        {
            superseded = _sessions
                .Where(s => s.PeerDeviceId.AsSpan().SequenceEqual(peerDeviceId))
                .ToList();
            foreach (var s in superseded) _sessions.Remove(s);
            _sessions.Add(session);
        }
        foreach (var s in superseded)
        {
            _log?.Invoke($"DirectDialer: superseding previous outbound session for peer={Convert.ToHexString(peerDeviceId)[..16]}...");
            try { s.RequestClose(); } catch { /* best-effort */ }
        }

        // 处理器必须在 pump.Start 之前注册（startPump:false 已保证 pump 未启动）。
        OnSessionEstablished?.Invoke(session);
        conn.Pump.Start(linked.Token);

        _log?.Invoke(
            $"DirectDialer: session established peer={Convert.ToHexString(peerDeviceId)[..16]}... " +
            $"target={host}:{port} transport={conn.Transport}");

        // 后台维持会话生命周期；这里立即返回，让 UI 不用等整个会话结束。
        _ = Task.Run(() => RunSessionAsync(session, e2eSession, linked.Token), CancellationToken.None);

        return new DirectDialOutcome { Ok = true, Transport = conn.Transport };
    }

    private async Task RunSessionAsync(DirectSession session, EncryptedSession e2eSession, CancellationToken ct)
    {
        try
        {
            await session.Pump.Completion.ConfigureAwait(false);
        }
        catch { /* 会话结束原因已由 DirectClient 记录 */ }
        finally
        {
            lock (_sessionsGate) _sessions.Remove(session);
            var engine = session.FileEngine;
            session.FileEngine = null;
            if (engine is not null)
            {
                try { await engine.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            try { await session.Pump.DisposeAsync().ConfigureAwait(false); } catch { }
            e2eSession.Dispose();
            _log?.Invoke(
                $"DirectDialer: session closed peer={Convert.ToHexString(session.PeerDeviceId)[..16]}...");
        }
    }

    /// <summary>关闭与指定对端 device_id 匹配的全部出站会话（撤销/踢线）。</summary>
    public int CloseSessionsFor(ReadOnlySpan<byte> peerDeviceId)
    {
        var target = peerDeviceId.ToArray();
        List<DirectSession> matches;
        lock (_sessionsGate)
        {
            matches = _sessions.Where(s => s.PeerDeviceId.AsSpan().SequenceEqual(target)).ToList();
            foreach (var s in matches) _sessions.Remove(s);
        }
        foreach (var s in matches)
        {
            _log?.Invoke($"DirectDialer: closing outbound session for revoked peer={Convert.ToHexString(target)[..16]}...");
            try { s.RequestClose(); } catch { /* best-effort */ }
        }
        return matches.Count;
    }

    /// <summary>关闭全部出站会话（end_session / 停机）。</summary>
    public int CloseAllSessions()
    {
        List<DirectSession> all;
        lock (_sessionsGate) { all = _sessions.ToList(); _sessions.Clear(); }
        foreach (var s in all) { try { s.RequestClose(); } catch { } }
        return all.Count;
    }

    private bool IsPaired(byte[] peerDeviceId)
    {
        // 与 DirectServer.IsPaired 同一判据：device_id = BLAKE3("desklink/device/v1" || ed25519_pub)。
        foreach (var p in _pairings.List())
        {
            try
            {
                var pub = Convert.FromBase64String(p.PeerPubB64);
                if (pub.Length != 32) continue;
                var id = E2ESessionHost.ComputeDeviceId(pub);
                if (id.AsSpan().SequenceEqual(peerDeviceId)) return true;
            }
            catch (FormatException) { continue; }
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        CloseAllSessions();
        try { await Task.Delay(0).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
