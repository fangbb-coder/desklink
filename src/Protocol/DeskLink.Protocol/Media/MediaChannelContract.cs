// 本地媒体通道契约（Service ↔ WPF 客户端）。
//
// 为什么必须与 RPC 管道分开（重要架构约束）：
//   - RPC 管道（`DeskLink.Client.{instance}`）按设计**只走元数据**，帧上限 64KB，
//     绝不允许编码码流进入（见 PipeContract 的说明）。
//   - 但 WPF 客户端要显示远端桌面，就必须拿到 H.264 码流（几 Mbps 量级），
//     并把本机键鼠事件送回去。这是**流**，不是请求/响应。
//   因此另开一条"媒体通道"：单独命名管道（`DeskLink.Media.{instance}`），
//   只承载编码码流与输入事件，不做 JSON 编解码、不做请求响应配对。
//
// 线上格式（与 RPC 管道不同，这里是**定长头 + 原始负载**，全大端）：
//   [u32 len BE][u8 frameType][payload...]，其中 len = 1 + payload.Length
//   最大负载受内层帧目录的 u16 上限约束（65535 字节），因此大帧必须由上层分片
//   （桌面码流按 NAL/访问单元分片，见 DesktopVideoChunk）。
//
// 为什么复用 ProtocolConstants.FrameType 而不是另定义一套枚举：
//   会话内的 desktop/control 帧类型已经定义好了语义（DesktopVideo / DesktopConfig /
//   InputMouseMove / ...）。媒体通道只是把同样的语义搬到本地 IPC 上，
//   复用可以让"同一份输入事件编解码"同时服务会话与本地通道，避免两套定义漂移。
using System.Buffers.Binary;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Frames;

namespace DeskLink.Protocol.Media;

/// <summary>媒体通道帧封装（定长头 + 原始负载）。</summary>
public static class MediaChannelFraming
{
    /// <summary>帧头长度：[u32 len][u8 frameType]。</summary>
    public const int HeaderSize = 4 + 1;

    /// <summary>单个媒体帧的最大负载（受内层帧目录 u16 上限约束）。</summary>
    public const int MaxPayloadSize = FrameCodec.MaxFramePayloadWireSize;

    /// <summary>把一帧编码成线上字节。</summary>
    public static byte[] Encode(ProtocolConstants.FrameType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"媒体帧负载 {payload.Length} 超过上限 {MaxPayloadSize}（上层必须分片）");
        }
        var buf = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)(1 + payload.Length));
        buf[4] = (byte)type;
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>解析帧头，得到负载长度（不含头）。头不完整返回 false。</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> header, out uint payloadLength, out ProtocolConstants.FrameType type)
    {
        payloadLength = 0;
        type = default;
        if (header.Length < HeaderSize) return false;

        var len = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0, 4));
        if (len < 1 || len > 1 + MaxPayloadSize) return false;
        payloadLength = len - 1;
        type = (ProtocolConstants.FrameType)header[4];
        return true;
    }
}

/// <summary>
/// 把字节流切成媒体帧（命名管道是字节流，不保证"一帧一次到达"）。
/// 用法：<c>Append(chunk)</c> 后循环 <c>TryRead</c>，与 SessionPump 的剥帧逻辑同构。
/// </summary>
public sealed class MediaFrameReader
{
    private byte[] _buf = new byte[16 * 1024];
    private int _len;

    /// <summary>追加新收到的字节。</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        var need = _len + data.Length;
        if (need > _buf.Length)
        {
            var cap = _buf.Length;
            while (cap < need) cap *= 2;
            Array.Resize(ref _buf, cap);
        }
        data.CopyTo(_buf.AsSpan(_len));
        _len = need;
    }

    /// <summary>尝试取出一个完整帧。</summary>
    public bool TryRead(out ProtocolConstants.FrameType type, out byte[] payload)
    {
        type = default;
        payload = Array.Empty<byte>();

        if (_len < MediaChannelFraming.HeaderSize) return false;
        if (!MediaChannelFraming.TryReadHeader(_buf.AsSpan(0, MediaChannelFraming.HeaderSize),
                out var payloadLen, out type))
        {
            // 头非法：无法恢复（长度前缀已不可信），由调用方决定断链重连。
            throw new InvalidDataException("媒体通道帧头非法（长度字段越界）");
        }

        var total = MediaChannelFraming.HeaderSize + (int)payloadLen;
        if (_len < total) return false;

        payload = new byte[payloadLen];
        Array.Copy(_buf, MediaChannelFraming.HeaderSize, payload, 0, payload.Length);

        var rest = _len - total;
        if (rest > 0) Array.Copy(_buf, total, _buf, 0, rest);
        _len = rest;
        return true;
    }
}

/// <summary>桌面视频分片负载（DesktopVideo）。</summary>
/// <remarks>
/// 布局：[u32 frameSeq][u64 ptsMicros][u8 flags][h264 数据...]
///   flags bit0 = 关键帧（IDR），bit1 = 一个访问单元的最后一分片。
/// 大访问单元（1080p 关键帧可达数百 KB）必须分片，因为单帧负载上限 65535。
/// </remarks>
public readonly record struct DesktopVideoChunk(
    uint FrameSeq,
    ulong PtsMicros,
    bool IsKeyFrame,
    bool IsLastFragment,
    byte[] Data)
{
    public const int FixedSize = 4 + 8 + 1;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize + Data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), FrameSeq);
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(4, 8), PtsMicros);
        buf[12] = (byte)((IsKeyFrame ? 1 : 0) | (IsLastFragment ? 2 : 0));
        Data.CopyTo(buf.AsSpan(FixedSize));
        return buf;
    }

    public static DesktopVideoChunk Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedSize)
        {
            throw new InvalidDataException($"DesktopVideo 负载过短：{payload.Length}");
        }
        var seq = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4));
        var pts = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(4, 8));
        var flags = payload[12];
        return new DesktopVideoChunk(seq, pts, (flags & 1) != 0, (flags & 2) != 0,
            payload.Slice(FixedSize).ToArray());
    }
}

/// <summary>编码器/显示器配置（DesktopConfig）。</summary>
/// <remarks>
/// 布局：[u16 width][u16 height][u8 codec(1=H264)][u8 backend(0=hardware,1=software)]
///       [u16 fps][u32 bitrateBps][u16 monitorIndex][u8 rotation(0/90/180/270)]
/// 客户端据此初始化解码器；旋转与分辨率变化时必须重配（DXGI 模式切换）。
/// </remarks>
public readonly record struct DesktopConfigPayload(
    ushort Width,
    ushort Height,
    byte Codec,
    byte Backend,
    ushort Fps,
    uint BitrateBps,
    ushort MonitorIndex,
    byte RotationDegrees)
{
    public const int Size = 2 + 2 + 1 + 1 + 2 + 4 + 2 + 1;

    public const byte CodecH264 = 1;
    public const byte BackendHardware = 0;
    public const byte BackendSoftware = 1;

    public byte[] Encode()
    {
        var buf = new byte[Size];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), Width);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), Height);
        buf[4] = Codec;
        buf[5] = Backend;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), Fps);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), BitrateBps);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(12, 2), MonitorIndex);
        buf[14] = RotationDegrees;
        return buf;
    }

    public static DesktopConfigPayload Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Size) throw new InvalidDataException($"DesktopConfig 负载过短：{payload.Length}");
        return new DesktopConfigPayload(
            BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(0, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2)),
            payload[4],
            payload[5],
            BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6, 2)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8, 4)),
            BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(12, 2)),
            payload[14]);
    }
}

/// <summary>会话统计（SessionControl 的子类型之一，供状态条显示）。</summary>
/// <remarks>布局：[u32 fpsX10][u32 kbps][i32 rttMs][u8 degraded(0/1)]。</remarks>
public readonly record struct SessionStatsPayload(uint FpsX10, uint Kbps, int RttMs, bool Degraded)
{
    public const int Size = 4 + 4 + 4 + 1;

    public byte[] Encode()
    {
        var buf = new byte[Size];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), FpsX10);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), Kbps);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(8, 4), RttMs);
        buf[12] = (byte)(Degraded ? 1 : 0);
        return buf;
    }

    public static SessionStatsPayload Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Size) throw new InvalidDataException($"SessionStats 负载过短：{payload.Length}");
        return new SessionStatsPayload(
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)),
            BinaryPrimitives.ReadInt32BigEndian(payload.Slice(8, 4)),
            payload[12] != 0);
    }
}

/// <summary>
/// 背压/丢帧反馈（Service → 代理，帧类型 <c>MediaFlow</c>）。
///
/// 为什么需要它：自适应码率要"感知网络"，但**代理不知道链路状况** ——
/// 它只管抓屏编码。真正能看到"往客户端发不动"的是 Service 里的转发泵：
/// 当客户端的消费速度跟不上时，泵会按策略丢帧。把丢帧数与队列深度回传给代理，
/// 代理才能据此降档，形成一个**闭环**（否则自适应控制器只能看到本地编码耗时，
/// 网络侧变差时完全无感）。
///
/// 布局：[u32 droppedFrames][u32 clientQueueDepth][u32 droppedInput]。
///
/// 字段含义：
///   - droppedFrames    ：下行（画面）被丢弃的累计帧数 → 代理据此估算丢帧率并降档。
///   - clientQueueDepth ：下行队列当前深度 → 队列在涨说明客户端消费不过来。
///   - droppedInput     ：上行（输入）被丢弃的累计事件数。**这是"输入开始不跟手"的
///                        直接信号**：DESIGN 要求优先保输入响应，所以一旦输入被丢，
///                        代理必须更激进地降画质（牺牲画面换输入）。
///
/// 说明：不含 RTT —— 真正的 RTT 需要控制端回包，属于后续工作；
/// 当前闭环覆盖"管道/客户端消费不过来"与"输入开始丢"这两类最典型的变差。
/// </summary>
public readonly record struct MediaFlowPayload(uint DroppedFrames, uint ClientQueueDepth, uint DroppedInput)
{
    public const int Size = 4 + 4 + 4;

    public byte[] Encode()
    {
        var buf = new byte[Size];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), DroppedFrames);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), ClientQueueDepth);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), DroppedInput);
        return buf;
    }

    public static MediaFlowPayload Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Size) throw new InvalidDataException($"MediaFlow 负载过短：{payload.Length}");
        return new MediaFlowPayload(
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8, 4)));
    }
}

/// <summary>输入事件编解码（媒体通道方向：客户端 → Service）。</summary>
/// <remarks>
/// 全部使用**scancode**而不是虚拟键码：远程注入走 SendInput 的 KEYEVENTF_SCANCODE，
/// scancode 才与物理布局无关（VK 会受本机键盘布局影响）。
/// 鼠标坐标使用**归一化千分比**（0..1000）而不是像素：客户端不知道远端分辨率，
/// 归一化可以避免"客户端缩放显示时坐标错位"。
/// </remarks>
public static class InputEventCodec
{
    /// <summary>InputMouseMove：[i16 xNormPermille][i16 yNormPermille]。</summary>
    public static byte[] EncodeMouseMove(int xPermille, int yPermille)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(0, 2), (short)Math.Clamp(xPermille, 0, 1000));
        BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(2, 2), (short)Math.Clamp(yPermille, 0, 1000));
        return buf;
    }

    public static (int X, int Y) DecodeMouseMove(ReadOnlySpan<byte> p)
    {
        if (p.Length < 4) throw new InvalidDataException("InputMouseMove 负载过短");
        return (BinaryPrimitives.ReadInt16BigEndian(p.Slice(0, 2)),
                BinaryPrimitives.ReadInt16BigEndian(p.Slice(2, 2)));
    }

    /// <summary>InputMouseButton：[u8 button(0=left,1=right,2=middle)][u8 down]。</summary>
    public static byte[] EncodeMouseButton(byte button, bool down)
        => new[] { button, (byte)(down ? 1 : 0) };

    public static (byte Button, bool Down) DecodeMouseButton(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) throw new InvalidDataException("InputMouseButton 负载过短");
        return (p[0], p[1] != 0);
    }

    /// <summary>InputWheel：[i16 delta]（正 = 向上/向前）。</summary>
    public static byte[] EncodeWheel(int delta)
    {
        var buf = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buf, (short)Math.Clamp(delta, short.MinValue, short.MaxValue));
        return buf;
    }

    public static int DecodeWheel(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) throw new InvalidDataException("InputWheel 负载过短");
        return BinaryPrimitives.ReadInt16BigEndian(p);
    }

    /// <summary>InputKey：[u16 scancode][u8 flags][u8 down]；flags bit0 = 扩展键（E0 前缀）。</summary>
    public static byte[] EncodeKey(ushort scancode, bool extended, bool down)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), scancode);
        buf[2] = (byte)(extended ? 1 : 0);
        buf[3] = (byte)(down ? 1 : 0);
        return buf;
    }

    public static (ushort ScanCode, bool Extended, bool Down) DecodeKey(ReadOnlySpan<byte> p)
    {
        if (p.Length < 4) throw new InvalidDataException("InputKey 负载过短");
        return (BinaryPrimitives.ReadUInt16BigEndian(p.Slice(0, 2)), p[2] != 0, p[3] != 0);
    }
}
