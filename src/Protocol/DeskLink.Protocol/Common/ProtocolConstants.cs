// DeskLink 内层协议常量。两端（C# / Go）必须保持严格一致。
// 任何变更需同步更新 src/vps/internal/proto/proto.go 并跑对齐单测。
namespace DeskLink.Protocol.Common;

public static class ProtocolConstants
{
    // 版本与魔数
    public const ushort ProtocolVersion = 1;
    public const uint MagicNumber = 0x444C4E4B; // "DLNK"

    // 帧与载荷上限
    //
    // 注意区分两个上限（Go 侧同名常量语义一致）：
    //   - MaxFramePayloadSize   ：**缓冲区/DoS 保护**上界（256KB）。用于接收缓冲、
    //                             mux 外层长度等"宽松上界"，比线上格式允许的更大。
    //   - FrameCodec.MaxFramePayloadWireSize：**线上格式**真正允许的最大负载（65535），
    //                             由帧头的 u16 长度字段决定，Encode 侧强校验。
    // 早期 Encode 误用 MaxFramePayloadSize 做校验，导致 65536..262144 的负载
    // 被静默截断长度字段、整条流错位（详见 Frame.cs 的说明）。
    public const int MaxFramePayloadSize = 256 * 1024; // 256 KB 硬上限（缓冲区保护）
    public const int MaxStreamId = 255;                // TCP mux 1 字节 sid
    public const int ReservedStreamId = 0;             // 0 保留给握手/控制

    // 路径与流类别
    public enum PathKind : byte
    {
        Relay = 0x01,
        Direct = 0x02,
    }

    // 三类逻辑流（与 DESIGN.md 业务帧章节一致）
    public enum LogicalStream : byte
    {
        Desktop = 0x10,
        Control = 0x20,
        File = 0x30,
    }

    // 帧类型目录
    public enum FrameType : byte
    {
        // 控制与元数据
        Hello = 0x01,         // 路径协商入口，携带 device_id_hint
        HelloAck = 0x02,
        Close = 0x03,         // 显式关闭帧，携带原因
        Ping = 0x04,
        Pong = 0x05,

        // 桌面
        DesktopVideo = 0x10,  // H.264 编码块（一帧或分片）
        DesktopConfig = 0x11, // 显示器拓扑 / 编码器会话状态

        // 控制
        InputMouseMove = 0x20,
        InputMouseButton = 0x21,
        InputKey = 0x22,
        InputWheel = 0x23,
        SessionControl = 0x24,  // 全屏切换、自适应码率反馈等
        CursorShape = 0x25,     // 客户端合成光标形状/位置

        // 文件
        FileListRequest = 0x30,
        FileListResponse = 0x31,
        FileOpen = 0x32,       // 开始上传/下载
        FileChunk = 0x33,      // 数据分块
        FileAck = 0x34,        // 位图 ack
        FilePause = 0x35,
        FileResume = 0x36,
        FileCancel = 0x37,
        FileComplete = 0x38,   // BLAKE3 摘要 + 原子改名指令
        FileStatus = 0x39,     // 传输状态/失败原因回执（P6；C# 专用，Go 侧不解析业务帧）

        // —— 本地媒体通道专用（P8/P9 收尾）——
        //
        // 这些类型**只出现在 Service ↔ 客户端的本地命名管道上**，绝不进入中继会话，
        // 因此不需要与 Go 侧对齐。
        MediaFlow = 0x3A,      // Service → 代理：背压/丢帧反馈（供自适应码率使用）
    }
}
