// SessionPump 分帧边界测试。
//
// 覆盖三类"对端违反分帧约定"的输入——它们都不会经过解密，所以可以用随机密钥
// 构造 EncryptedSession，不需要真实握手：
//   1. 外层 len = 0（非法：至少要容纳 sid 字节）
//   2. 外层 len 超过上界（DoS 保护）
//   3. 正常帧被拆成多次到达（分片读取）必须仍能正确还原
//
// 为什么要测 1 和 2：早期实现遇到这两种输入时只是 `return false` 并清缓冲，
// 于是同一个非法前缀会被反复解析、接收缓冲无限增长——对端一句话就能把内存吃光；
// 或者"清缓冲后继续"导致流永久错位却不报错。正确处置是**终止会话**。
using System.Buffers.Binary;
using System.Net;
using DeskLink.Protocol.Session;
using DeskLink.Service.Relay;
using DeskLink.Service.Session;
using Xunit;
using Xunit.Abstractions;

namespace DeskLink.Service.Tests;

public class SessionPumpFramingTests
{
    private readonly ITestOutputHelper _out;

    public SessionPumpFramingTests(ITestOutputHelper output) => _out = output;

    // 固定密钥对：A 的 sendKey == B 的 recvKey，反之亦然（模拟一次真实握手的结果）。
    private static readonly byte[] KeyAtoB = MakeKey(0);
    private static readonly byte[] KeyBtoA = MakeKey(255);

    private static byte[] MakeKey(int seed)
    {
        var k = new byte[32];
        for (var i = 0; i < 32; i++) k[i] = (byte)(seed - i);
        return k;
    }

    /// <summary>本端（接收侧）：recv 用 KeyAtoB，send 用 KeyBtoA。</summary>
    private static EncryptedSession LocalSession() => new(KeyBtoA, KeyAtoB);

    /// <summary>对端（发送侧）：send 用 KeyAtoB，recv 用 KeyBtoA。</summary>
    private static EncryptedSession PeerSession() => new(KeyAtoB, KeyBtoA);

    [Fact]
    public async Task IllegalOuterLenZero_AbortsSession()
    {
        // len = 0 的 4 字节前缀 + 之后持续灌数据。
        var fake = new ScriptedTransport(
            new byte[] { 0x00, 0x00, 0x00, 0x00 },
            new byte[4096], new byte[4096], new byte[4096], new byte[4096]);

        await using var pump = new SessionPump(fake, LocalSession(), m => _out.WriteLine(m));
        pump.Start();
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(fake.BytesConsumed < 64 * 1024,
            $"对端灌了 {fake.BytesConsumed} 字节才终止；应当在读到非法长度后立刻停");
    }

    [Fact]
    public async Task OversizedOuterLen_AbortsSession()
    {
        // len = 0x7FFFFFFF（远超 256KB 上界）。
        var huge = new byte[] { 0x7F, 0xFF, 0xFF, 0xFF };
        var fake = new ScriptedTransport(huge, new byte[4096], new byte[4096]);

        await using var pump = new SessionPump(fake, LocalSession(), m => _out.WriteLine(m));
        pump.Start();
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(fake.BytesConsumed < 64 * 1024, $"超大长度未被及时拒绝（已消费 {fake.BytesConsumed} 字节）");
    }

    [Fact]
    public async Task FragmentAcrossReads_IsReassembled()
    {
        // 用对端会话加密一个真实帧，再按 1 字节/次的粒度投喂——覆盖"半帧累积"路径。
        using var sender = PeerSession();
        var sealedFrame = sender.SealFrame(
            DeskLink.Protocol.Common.ProtocolConstants.FrameType.SessionControl,
            new byte[] { 0xAB, 0xCD, 0xEF },
            DeskLink.Protocol.Mux.StreamId.ControlBase,
            tcpMux: true);

        var chunks = sealedFrame.Select(b => new[] { b }).ToArray();
        var fake = new ScriptedTransport(chunks);

        await using var pump = new SessionPump(fake, LocalSession(), m => _out.WriteLine(m));
        var received = new TaskCompletionSource<(byte[] Payload, byte Sid)>(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Register(
            DeskLink.Protocol.Common.ProtocolConstants.LogicalStream.Control,
            f => received.TrySetResult((f.Payload, f.Sid)));
        pump.Start();

        var (payload, sid) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0xAB, 0xCD, 0xEF }, payload);
        Assert.Equal(DeskLink.Protocol.Mux.StreamId.ControlBase, sid);

        await pump.DisposeAsync();
    }

    /// <summary>
    /// 按脚本投喂字节的假传输：每次 ReceiveAsync 返回脚本中的下一段；
    /// 脚本耗尽后阻塞到取消（模拟对端不再发数据）。
    /// </summary>
    private sealed class ScriptedTransport : IRelayTransport
    {
        private readonly byte[][] _script;
        private int _idx;

        public long BytesConsumed { get; private set; }

        public ScriptedTransport(params byte[][] script) => _script = script;

        public RelayTransportKind Kind => RelayTransportKind.TcpTls;
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;

        public ValueTask ConnectAsync(Uri relay, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            BytesConsumed += data.Length;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_idx >= _script.Length)
            {
                // 脚本耗尽：一直挂起，直到调用方取消（会话被终止）。
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return 0;
            }
            var chunk = _script[_idx++];
            var n = Math.Min(chunk.Length, buffer.Length);
            chunk.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }

        public ValueTask CloseAsync() => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
