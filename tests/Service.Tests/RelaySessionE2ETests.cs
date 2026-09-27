// P4/P5 端到端集成测试。
//
// 目标（对应验收"单机双实例经本地 relay 配对成功" + "双实例加密帧往返验证"）：
//   在同一个进程里起两个"实例"（各自独立 KeyStore / PairingStore / ServiceOptions），
//   各自经 TCP/TLS 连到一个测试用"迷你中继"，完成
//       控制握手（Hello + Dial） → SIGMA 握手 → 加密会话 → 控制流往返，
//   并断言双方都拿到了 ControlRoundTripOk。
//
// 为什么用"迷你中继"而不是真 relayd：
//   - 本测试要验证的是 **C# 侧** 的协议实现（RelayControlChannel / E2ESessionHost /
//     EncryptedSession / SessionPump）。Go 侧 relayd 已由 src/vps 的 Go 单测覆盖。
//   - 迷你中继只实现客户端依赖的那几条语义：Hello→HelloAck、Dial→PeerEvent(Ready)+
//     DialResult(ok)、两侧都 Dial 后开始**单泵**字节转发（与修复后的 relayd 一致）。
//
// 平台：KeyStore 走 DPAPI(LocalMachine)，仅 Windows 可跑。
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeskLink.Protocol.Handshake;
using DeskLink.Protocol.Relay;
using DeskLink.Service.Configuration;
using DeskLink.Service.FileTransfer;
using DeskLink.Service.Relay;
using DeskLink.Service.Security;
using DeskLink.Service.Session;
using Xunit;
using Xunit.Abstractions;

namespace DeskLink.Service.Tests;

public class RelaySessionE2ETests
{
    private readonly ITestOutputHelper _out;

    public RelaySessionE2ETests(ITestOutputHelper output) => _out = output;

    [SkippableFact(Timeout = 60000)]
    public async Task TwoInstances_EstablishE2E_AndCompleteControlRoundTrip()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "KeyStore 依赖 DPAPI(LocalMachine)");

        await using var relay = new MiniRelayServer();

        // ── 两个"实例"：独立数据目录 + 独立设备密钥 ────────────────────────────
        var dirA = NewTempDir();
        var dirB = NewTempDir();
        try
        {
            var ksA = new KeyStore(dirA);
            var keysA = ksA.LoadOrCreate();
            var idA = E2ESessionHost.ComputeDeviceId(keysA.KeyPair.Ed25519Public);

            var ksB = new KeyStore(dirB);
            var keysB = ksB.LoadOrCreate();
            var idB = E2ESessionHost.ComputeDeviceId(keysB.KeyPair.Ed25519Public);

            // 同一台机器上两个实例，device_id 必然不同（BLAKE3 输出）。
            Assert.NotEqual(Convert.ToHexString(idA), Convert.ToHexString(idB));

            var url = new Uri($"tls://127.0.0.1:{relay.Port}");
            // 用 --peer 显式指定对端（等价于命令行 --peer <hex>），
            // 绕开 registry 配对码流程，专注验证 E2E 通道。
            var optA = new ServiceOptions
            {
                DataDir = dirA,
                RelayUrl = url,
                PeerDeviceId = Convert.ToHexString(idB),
            };
            var optB = new ServiceOptions
            {
                DataDir = dirB,
                RelayUrl = url,
                PeerDeviceId = Convert.ToHexString(idA),
            };

            await using var runnerA = new RelaySessionRunner(ksA, new PairingStore(dirA), optA, m => _out.WriteLine("[A] " + m));
            await using var runnerB = new RelaySessionRunner(ksB, new PairingStore(dirB), optB, m => _out.WriteLine("[B] " + m));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await using var ta = new TcpTlsTransport();
            await using var tb = new TcpTlsTransport();
            await ta.ConnectAsync(url, cts.Token);
            await tb.ConnectAsync(url, cts.Token);
            _out.WriteLine($"both transports connected to mini relay :{relay.Port}");

            // 两条传输各自驱动：控制握手 → SIGMA → 加密收发。
            var taskA = runnerA.OnTransportReadyAsync(ta, cts.Token);
            var taskB = runnerB.OnTransportReadyAsync(tb, cts.Token);

            // ── 断言 1：双方都建立 E2E 会话并完成控制流往返（P5 验收）──────────
            await WaitUntilAsync(
                () => runnerA.ControlRoundTripOk && runnerB.ControlRoundTripOk,
                TimeSpan.FromSeconds(20),
                () => _out.WriteLine($"waiting... A={runnerA.State} B={runnerB.State} " +
                                     $"rttA={runnerA.LastRttMs} rttB={runnerB.LastRttMs}"));

            Assert.True(runnerA.ControlRoundTripOk, $"A 未完成往返，state={runnerA.State}");
            Assert.True(runnerB.ControlRoundTripOk, $"B 未完成往返，state={runnerB.State}");
            Assert.Equal("established", runnerA.State);
            Assert.Equal("established", runnerB.State);

            // ── 断言 2：对端 device_id 解析正确 ────────────────────────────────
            Assert.Equal(Convert.ToHexString(idB), Convert.ToHexString(runnerA.PeerDeviceId!));
            Assert.Equal(Convert.ToHexString(idA), Convert.ToHexString(runnerB.PeerDeviceId!));

            // ── 断言 3：两侧角色互补且与 device_id 字典序一致 ──────────────────
            // 约定：device_id 字典序小者为 SIGMA Initiator（两侧独立计算，无需协商）。
            var aIsSmaller = idA.AsSpan().SequenceCompareTo(idB) < 0;
            Assert.Equal(aIsSmaller ? HandshakeRole.Initiator : HandshakeRole.Responder, runnerA.Role);
            Assert.Equal(aIsSmaller ? HandshakeRole.Responder : HandshakeRole.Initiator, runnerB.Role);
            Assert.NotEqual(runnerA.Role, runnerB.Role);

            // ── 断言 4：Ping/Pong 带时间戳，RTT 是真实测量值（不是占位 0/-1）──
            // 本机回环，RTT 应当被量到且远小于超时；只断言"有样本"，
            // 不卡具体数值（避免 CI 抖动）。
            await WaitUntilAsync(
                () => runnerA.LastRttMs >= 0 && runnerB.LastRttMs >= 0,
                TimeSpan.FromSeconds(5),
                () => _out.WriteLine($"waiting rtt... A={runnerA.LastRttMs} B={runnerB.LastRttMs}"));
            Assert.True(runnerA.LastRttMs >= 0, $"A 未测到 RTT: {runnerA.LastRttMs}");
            Assert.True(runnerB.LastRttMs >= 0, $"B 未测到 RTT: {runnerB.LastRttMs}");
            // 回环链路的 RTT 不可能到秒级；作为"没有把时间戳算错（例如当成 epoch）"的哨兵。
            Assert.True(runnerA.LastRttMs < 5000, $"A RTT 异常: {runnerA.LastRttMs}ms");
            Assert.True(runnerB.LastRttMs < 5000, $"B RTT 异常: {runnerB.LastRttMs}ms");

            // ── 收尾：取消 → 两条会话应正常结束 ───────────────────────────────
            cts.Cancel();
            await Task.WhenAll(SwallowAsync(taskA), SwallowAsync(taskB));
        }
        finally
        {
            TryDeleteDir(dirA);
            TryDeleteDir(dirB);
        }
    }

    /// <summary>
    /// P6 端到端：在真实加密会话（SIGMA + AES-GCM over TCP/TLS through mini relay）上
    /// 完成一次文件传输，并验证 scope 越界被拒。
    ///
    /// 与 FileTransferTests 的内存 sink 版本相比，这一条把文件流放进了**真实会话**，
    /// 覆盖"业务帧经加密 → relay 转发 → 对端解密 → 文件落盘"的完整链路。
    /// </summary>
    [SkippableFact(Timeout = 120000)]
    public async Task TwoInstances_FileTransfer_OverEncryptedSession()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "KeyStore 依赖 DPAPI(LocalMachine)");

        await using var relay = new MiniRelayServer();
        var dirA = NewTempDir();
        var dirB = NewTempDir();
        var srcDir = NewTempDir();
        var dstDir = NewTempDir();
        try
        {
            // 150KB > 默认 48KB 块 → 至少 4 块，覆盖分块/ack 往返。
            var payload = new byte[150_000];
            new Random(4242).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(srcDir, "payload.bin"), payload);

            var ksA = new KeyStore(dirA);
            var keysA = ksA.LoadOrCreate();
            var idA = E2ESessionHost.ComputeDeviceId(keysA.KeyPair.Ed25519Public);
            var ksB = new KeyStore(dirB);
            var keysB = ksB.LoadOrCreate();
            var idB = E2ESessionHost.ComputeDeviceId(keysB.KeyPair.Ed25519Public);

            var url = new Uri($"tls://127.0.0.1:{relay.Port}");
            var optA = new ServiceOptions
            {
                DataDir = dirA,
                RelayUrl = url,
                PeerDeviceId = Convert.ToHexString(idB),
            };
            optA.FileScopeRoots.Add(srcDir);
            var optB = new ServiceOptions
            {
                DataDir = dirB,
                RelayUrl = url,
                PeerDeviceId = Convert.ToHexString(idA),
            };
            optB.FileScopeRoots.Add(dstDir);

            await using var runnerA = new RelaySessionRunner(ksA, new PairingStore(dirA), optA, m => _out.WriteLine("[A] " + m));
            await using var runnerB = new RelaySessionRunner(ksB, new PairingStore(dirB), optB, m => _out.WriteLine("[B] " + m));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await using var ta = new TcpTlsTransport();
            await using var tb = new TcpTlsTransport();
            await ta.ConnectAsync(url, cts.Token);
            await tb.ConnectAsync(url, cts.Token);

            var taskA = runnerA.OnTransportReadyAsync(ta, cts.Token);
            var taskB = runnerB.OnTransportReadyAsync(tb, cts.Token);

            await WaitUntilAsync(
                () => runnerA.ControlRoundTripOk && runnerB.ControlRoundTripOk,
                TimeSpan.FromSeconds(30),
                () => _out.WriteLine($"waiting... A={runnerA.State} B={runnerB.State}"));

            var engineA = runnerA.FileEngine;
            var engineB = runnerB.FileEngine;
            Assert.NotNull(engineA);
            Assert.NotNull(engineB);

            // ── 1) 上传：A 的 scope 文件 → B 的 scope ─────────────────────────
            var outcome = await engineA!.UploadAsync("payload.bin", "payload.bin", FileConflictPolicy.Overwrite);
            Assert.True(outcome.Ok, $"文件传输失败: {outcome.Error}");
            Assert.Equal(payload.Length, outcome.BytesTransferred);

            var written = Path.Combine(dstDir, "payload.bin");
            Assert.True(File.Exists(written), "对端应已落盘");
            Assert.Equal(payload, File.ReadAllBytes(written));
            Assert.False(File.Exists(written + ".part"), ".part 应已被原子改名消耗");

            // ── 2) 冲突策略：目标已存在 + skip → 成功但零字节 ──────────────────
            var skipOutcome = await engineA.UploadAsync("payload.bin", "payload.bin", FileConflictPolicy.Skip);
            Assert.True(skipOutcome.Ok, skipOutcome.Error);
            Assert.Equal(0, skipOutcome.BytesTransferred);

            // ── 3) 远端目录列举 ───────────────────────────────────────────────
            var listing = await engineA.ListAsync("");
            Assert.True(listing.Ok, listing.Error);
            Assert.Contains(listing.Entries, e => e.Name == "payload.bin" && e.Size == payload.Length);

            // ── 4) scope 越界：源路径不在 A 的授权范围内 → 本地即拒绝 ──────────
            var denied = await engineA.UploadAsync(@"..\..\windows\win.ini", "x.bin", FileConflictPolicy.Overwrite);
            Assert.False(denied.Ok);
            Assert.Contains("不在授权范围内", denied.Error!);

            cts.Cancel();
            await Task.WhenAll(SwallowAsync(taskA), SwallowAsync(taskB));
        }
        finally
        {
            TryDeleteDir(dirA);
            TryDeleteDir(dirB);
            TryDeleteDir(srcDir);
            TryDeleteDir(dstDir);
        }
    }

    [Fact]
    public void ParseDeviceId_AcceptsHexAndBase64_RejectsGarbage()
    {
        var raw = new byte[32];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i * 7);

        var hex = Convert.ToHexString(raw);
        Assert.Equal(raw, RelaySessionRunner.ParseDeviceId(hex)!);
        Assert.Equal(raw, RelaySessionRunner.ParseDeviceId("0x" + hex)!);
        Assert.Equal(raw, RelaySessionRunner.ParseDeviceId(hex.ToLowerInvariant())!);
        Assert.Equal(raw, RelaySessionRunner.ParseDeviceId(Convert.ToBase64String(raw))!);

        Assert.Null(RelaySessionRunner.ParseDeviceId("not-a-device-id"));
        Assert.Null(RelaySessionRunner.ParseDeviceId(Convert.ToBase64String(new byte[16])));
    }

    // ── 测试工具 ──────────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> cond, TimeSpan timeout, Action? tick = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return;
            tick?.Invoke();
            await Task.Delay(250);
        }
    }

    private static async Task SwallowAsync(Task t)
    {
        try { await t; } catch { /* 取消/断开属预期 */ }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"desklink-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 尽力而为 */ }
    }
}

/// <summary>
/// 测试用"迷你中继"：实现 C# 客户端依赖的最小 relayd 行为。
///
/// 行为（与修复后的 relayd 语义一致）：
///   1. 接受 TCP/TLS 连接，跑服务端 TLS 握手（自签证书）。
///   2. 读 Hello → 回 HelloAck(ok)。
///   3. 读 Dial → 先回 PeerEvent(Ready)（必须在 DialResult 之前，客户端在 Dial
///      阶段会跳过 PeerEvent），再回 DialResult(ok)。
///   4. 两侧都 Dial 完成后，启动**唯一**一条双向字节泵。
///      后到者不再起第二个泵，只保持连接存活直到泵结束——这是关键：
///      同一字节流被两个 goroutine/任务并发读会乱序，进而破坏 E2E 的 AEAD 计数器。
/// </summary>
internal sealed class MiniRelayServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _cert;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Conn> _conns = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _pumpDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _pumpStarted;

    public int Port { get; }

    private sealed class Conn
    {
        public required SslStream Ssl { get; init; }
        public bool Dialed { get; set; }
    }

    public MiniRelayServer()
    {
        _cert = CreateSelfSignedCert();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        SslStream? ssl = null;
        try
        {
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _cert,
                ClientCertificateRequired = false,
            }, _cts.Token);

            var conn = new Conn { Ssl = ssl };

            // 1) Hello → HelloAck(ok)
            var hello = await ReadCtlAsync(ssl, _cts.Token);
            if (hello.Kind != RelayControlKind.Hello)
            {
                return;
            }
            _ = RelayHelloCodec.Decode(hello.Payload); // 校验 payload 可解（含 device_id）
            lock (_gate) { _conns.Add(conn); }
            await WriteCtlAsync(ssl, RelayControlKind.HelloAck,
                RelayStatusCodec.Encode(true, RelayReason.Ok), _cts.Token);

            // 2) Dial → PeerEvent(Ready) + DialResult(ok)
            var dial = await ReadCtlAsync(ssl, _cts.Token);
            if (dial.Kind != RelayControlKind.Dial)
            {
                return;
            }
            conn.Dialed = true;

            // Ready 必须先于 DialResult 抵达本连接：客户端在 Dial 阶段只跳过 PeerEvent。
            await WriteCtlAsync(ssl, RelayControlKind.PeerEvent,
                EncodePeerEvent(RelayPeerEventKind.Ready, dial.Payload), _cts.Token);
            await WriteCtlAsync(ssl, RelayControlKind.DialResult,
                RelayStatusCodec.Encode(true, RelayReason.Ok), _cts.Token);

            // 3) 决定谁是唯一的泵持有者（先到者）。
            bool isOwner;
            lock (_gate)
            {
                var dialedCount = _conns.Count(c => c.Dialed);
                isOwner = dialedCount >= 2 && !_pumpStarted;
                if (isOwner) _pumpStarted = true;
            }

            if (!isOwner)
            {
                // 后到者：保持连接存活但不读数据，等泵结束。
                await _pumpDone.Task.WaitAsync(_cts.Token);
                return;
            }

            // 先到者：跑唯一的一条双向泵。
            await PumpAsync(_cts.Token);
            _pumpDone.TrySetResult();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 连接级异常：忽略；由测试断言兜底 */ }
        finally
        {
            try { ssl?.Dispose(); } catch { /* 尽力而为 */ }
            try { client.Dispose(); } catch { /* 尽力而为 */ }
        }
    }

    /// <summary>双向字节泵：两个方向各一个任务，任一侧结束即关闭两端。</summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        Conn[] pair;
        lock (_gate) { pair = _conns.ToArray(); }
        if (pair.Length < 2) return;
        var a = pair[0].Ssl;
        var b = pair[1].Ssl;

        static async Task CopyAsync(SslStream dst, SslStream src, CancellationToken ct)
        {
            var buf = new byte[32 * 1024];
            try
            {
                while (true)
                {
                    var n = await src.ReadAsync(buf, ct);
                    if (n <= 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    await dst.FlushAsync(ct);
                }
            }
            catch { /* 任一侧断开 */ }
        }

        var t1 = CopyAsync(b, a, ct);
        var t2 = CopyAsync(a, b, ct);
        await Task.WhenAny(t1, t2);
        try { a.Dispose(); } catch { }
        try { b.Dispose(); } catch { }
        try { await Task.WhenAll(t1, t2); } catch { }
    }

    private static async Task<RelayFrame> ReadCtlAsync(SslStream ssl, CancellationToken ct)
    {
        var hdr = new byte[RelayControl.HeaderSize];
        await ReadExactAsync(ssl, hdr, ct);
        var plen = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(1, 2));
        if (plen > RelayControl.MaxPayload)
        {
            throw new InvalidDataException($"relay control payload too large: {plen}");
        }
        var payload = new byte[plen];
        if (plen > 0)
        {
            await ReadExactAsync(ssl, payload, ct);
        }
        return new RelayFrame((RelayControlKind)hdr[0], payload);
    }

    private static async Task WriteCtlAsync(SslStream ssl, RelayControlKind kind, byte[] payload, CancellationToken ct)
    {
        var frame = RelayControl.Encode(kind, payload);
        await ssl.WriteAsync(frame, ct);
        await ssl.FlushAsync(ct);
    }

    private static async Task ReadExactAsync(SslStream ssl, byte[] buf, CancellationToken ct)
    {
        var got = 0;
        while (got < buf.Length)
        {
            var n = await ssl.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) throw new EndOfStreamException("relay connection closed");
            got += n;
        }
    }

    private static byte[] EncodePeerEvent(RelayPeerEventKind kind, ReadOnlySpan<byte> subject)
    {
        var buf = new byte[1 + RelayControl.DeviceIdSize];
        buf[0] = (byte)kind;
        subject[..RelayControl.DeviceIdSize].CopyTo(buf.AsSpan(1));
        return buf;
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=desklink-test-relay", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // 通过 PFX 往返一次，确保私钥在 Windows 上可被 SslStream 使用。
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _cert.Dispose(); } catch { }
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
