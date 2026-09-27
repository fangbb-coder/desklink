// 控制流往返探针（P5 验收："控制流往返"）。
//
// 从中继路径（RelaySessionRunner）与局域网直连路径（DirectServer）共用同一实现，
// 避免两条路径各写一份导致行为漂移。
//
// 探针协议（避免无限回环）：
//   payload = [u8 kind][u32 seq BE]，共 5 字节
//   kind = 0x01 probe     —— 请求方发出
//   kind = 0x02 probe-ack —— 应答方回发（**不再触发对端回应**）
//
// 语义：
//   - 收到 probe     → 回 probe-ack（尽力而为，fire-and-forget）
//   - 收到 probe-ack → RoundTripOk = true，并触发 OnRoundTripOk
// 其余 SessionControl 子类型在 P5 仅记录日志（真实语义在 P7/P8 接入）。
using System.Buffers.Binary;
using DeskLink.Protocol.Common;

namespace DeskLink.Service.Session;

/// <summary>控制流探针的收发与状态。</summary>
public sealed class SessionControlProbe
{
    /// <summary>子类型：请求。</summary>
    public const byte ProbeKind = 0x01;

    /// <summary>子类型：应答。</summary>
    public const byte ProbeAckKind = 0x02;

    /// <summary>payload 长度：[u8 kind][u32 seq BE]。</summary>
    public const int ProbePayloadLen = 5;

    private readonly Action<string>? _log;
    private SessionPump? _pump;
    private uint _probeSeq;

    /// <summary>是否已完成一次 probe → ack 往返（P5 验收标志）。</summary>
    public bool RoundTripOk { get; private set; }

    /// <summary>往返成功时触发（供状态机更新）。</summary>
    public event Action? OnRoundTripOk;

    public SessionControlProbe(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>把本探针注册为 pump 的控制流处理器。</summary>
    public void Attach(SessionPump pump)
    {
        _pump = pump ?? throw new ArgumentNullException(nameof(pump));
        pump.Register(ProtocolConstants.LogicalStream.Control, Handle);
    }

    /// <summary>发送一条 probe（序号单调递增）。</summary>
    public async Task SendProbeAsync(CancellationToken ct = default)
    {
        var pump = _pump;
        if (pump is null) return;
        var seq = ++_probeSeq;
        var payload = new byte[ProbePayloadLen];
        payload[0] = ProbeKind;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), seq);
        try
        {
            await pump.SendControlAsync(ProtocolConstants.FrameType.SessionControl, payload, ct)
                .ConfigureAwait(false);
            _log?.Invoke($"SessionControl: probe sent seq={seq}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"SessionControl: probe send failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>控制流入站处理器。</summary>
    public void Handle(InboundFrame frame)
    {
        if (frame.Type != ProtocolConstants.FrameType.SessionControl)
        {
            _log?.Invoke($"SessionControl: control frame type=0x{(byte)frame.Type:x2} len={frame.Payload.Length}");
            return;
        }
        if (frame.Payload.Length < ProbePayloadLen)
        {
            _log?.Invoke($"SessionControl: malformed SessionControl len={frame.Payload.Length}");
            return;
        }

        var kind = frame.Payload[0];
        var seq = BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(1, 4));

        if (kind == ProbeKind)
        {
            var ack = new byte[ProbePayloadLen];
            ack[0] = ProbeAckKind;
            BinaryPrimitives.WriteUInt32BigEndian(ack.AsSpan(1, 4), seq);
            var pump = _pump;
            if (pump is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await pump.SendControlAsync(ProtocolConstants.FrameType.SessionControl, ack)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // 尽力而为：会话可能已结束
                    }
                });
            }
            _log?.Invoke($"SessionControl: probe received seq={seq}, ack sent");
        }
        else if (kind == ProbeAckKind)
        {
            RoundTripOk = true;
            OnRoundTripOk?.Invoke();
            _log?.Invoke($"SessionControl: control round-trip OK seq={seq} (P5 acceptance)");
        }
        else
        {
            _log?.Invoke($"SessionControl: unknown subkind=0x{kind:x2} seq={seq}");
        }
    }
}
