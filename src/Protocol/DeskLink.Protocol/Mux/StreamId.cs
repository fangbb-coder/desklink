using DeskLink.Protocol.Common;

namespace DeskLink.Protocol.Mux;

// sid 分配约定：
// 0           保留给握手/控制流
// 1..15       系统保留（PingPong、关闭等）
// 16..31      desktop 逻辑流
// 32..47      control 逻辑流
// 48..63      file 逻辑流
// 64..255     各逻辑流内部进一步细分（多路文件传输、并行视频）
//
// 每端按"本地出站 sid 为偶数、对端入站 sid 为奇数"的常见模式不太适用——
// 本协议 sid 是逻辑流标识（不是 QUIC 流 id），两端使用相同 sid 表示同一通道。
// 实际绑定由调用方决定（Service 层在握手成功后建立映射）。
public static class StreamId
{
    public const byte Handshake = ProtocolConstants.ReservedStreamId;       // 0
    public const byte DesktopBase = 16;
    public const byte ControlBase = 32;
    public const byte FileBase = 48;
    public const byte UserRangeStart = 64;
    public const byte Max = ProtocolConstants.MaxStreamId;                 // 255

    public static byte FromLogical(ProtocolConstants.LogicalStream kind, byte sub = 0)
    {
        var baseSid = kind switch
        {
            ProtocolConstants.LogicalStream.Desktop => DesktopBase,
            ProtocolConstants.LogicalStream.Control => ControlBase,
            ProtocolConstants.LogicalStream.File => FileBase,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var sid = baseSid + sub;
        if (sid > Max)
        {
            throw new ArgumentOutOfRangeException(nameof(sub), $"sub {sub} overflow for {kind}");
        }
        return (byte)sid;
    }
}
