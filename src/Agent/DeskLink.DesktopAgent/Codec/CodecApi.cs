using System.Runtime.InteropServices;

namespace DeskLink.DesktopAgent.Codec;

/// <summary>
/// ICodecAPI 的最小互操作声明。
///
/// 为什么必须自己声明：Vortice.MediaFoundation 3.2.0 **没有** 导出 ICodecAPI
/// （已用反射确认：程序集内不存在该类型）。要调 CODECAPI_* 这些编码器参数，
/// 只能通过 COM 查询接口拿到它。
///
/// 关键坑：
///   1. vtable 顺序必须与 Windows SDK 的 ICodecAPI 完全一致，因为 InterfaceIsIUnknown
///      是按槽位分派的。这里按 SDK 声明顺序列出前 7 个方法（SetValue 在第 7 槽），
///      后面两个（RegisterForEvent / UnregisterForEvent）不影响前面的槽位，可以省略。
///   2. 参数是 VARIANT*，用 `object` + MarshalAs(UnmanagedType.Struct) 让内置 COM
///      封送器处理，避免手写 VARIANT 结构体（16 字节联合 + 对齐，手写极易错）。
///      装箱成 `uint` 会封送成 VT_UI4，正好匹配 CODECAPI_* 里要求 UI4 的那些属性。
///   3. ICodecAPI 是**可选**的：拿不到或 SetValue 失败都不应影响编码，
///      调用方必须忽略失败并退回到"只用 IMFMediaType 属性"的配置方式。
/// </summary>
[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecApiInterop
{
    [PreserveSig]
    int IsSupported(ref Guid api);

    [PreserveSig]
    int IsModifiable(ref Guid api);

    [PreserveSig]
    int GetParameterRange(ref Guid api, out object valueMin, out object valueMax, out object steppingDelta);

    [PreserveSig]
    int GetParameterValues(ref Guid api, out IntPtr values, out uint valuesCount);

    [PreserveSig]
    int GetDefaultValue(ref Guid api, out object value);

    [PreserveSig]
    int GetValue(ref Guid api, out object value);

    [PreserveSig]
    int SetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] object value);
}

/// <summary>
/// CODECAPI_* 属性 GUID（取自 Windows SDK codecapi.h）。
/// 只列出 v1 真正会用到的几个，避免抄错一堆用不上的常量。
/// </summary>
internal static class CodecApiGuids
{
    /// <summary>码率控制模式；0=CBR，1=PeakConstrainedVBR，2=UnconstrainedVBR，3=Quality。</summary>
    public static readonly Guid AVEncCommonRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");

    /// <summary>平均码率（bit/s）。</summary>
    public static readonly Guid AVEncCommonMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");

    /// <summary>恒定质量模式的品质等级（0..100）。</summary>
    public static readonly Guid AVEncCommonQuality = new("FCBF57A3-7EA5-4B0C-9644-69B40C39C391");

    /// <summary>GOP 长度（帧）。设为 1 表示每帧都是关键帧——只在"必须每帧独立"时用。</summary>
    public static readonly Guid AVEncMPVGOPSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");

    /// <summary>强制下一帧为关键帧（SPS/PPS 随 IDR 一起发出）。</summary>
    public static readonly Guid AVEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");

    /// <summary>
    /// 低延迟模式。MF 的 H.264 编码器默认有十几帧的前瞻缓冲，
    /// 这里尽力开启低延迟以压低端到端延迟；属性不被支持时静默失败。
    /// 注意：本机未能验证它是否真的消除了缓冲（见交付说明的已知限制）。
    /// </summary>
    public static readonly Guid AVLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
}

/// <summary>
/// 对 <see cref="ICodecApiInterop"/> 的托管包装：把 COM 查询与"失败即忽略"的语义收在一处。
/// </summary>
internal sealed class CodecApi : IDisposable
{
    private readonly ICodecApiInterop _api;
    private readonly object _rcw;

    private CodecApi(object rcw, ICodecApiInterop api)
    {
        _rcw = rcw;
        _api = api;
    }

    /// <summary>
    /// 从任意 COM 对象的原生指针尝试取得 ICodecAPI。
    /// 失败返回 null —— 这是预期情况之一（软件 MFT 也可能不实现 ICodecAPI）。
    /// </summary>
    public static CodecApi? TryCreate(IntPtr nativePointer)
    {
        if (nativePointer == IntPtr.Zero) return null;
        try
        {
            // GetObjectForIUnknown 会 AddRef 并返回 RCW；转成接口会做一次 QueryInterface。
            var rcw = Marshal.GetObjectForIUnknown(nativePointer);
            if (rcw is ICodecApiInterop api)
            {
                return new CodecApi(rcw, api);
            }
            Marshal.ReleaseComObject(rcw);
            return null;
        }
        catch
        {
            return null;
        }
    }

    public bool IsSupported(Guid api)
    {
        try { return _api.IsSupported(ref api) >= 0; }
        catch { return false; }
    }

    /// <summary>设置一个 UI4 属性；失败返回 false（调用方应忽略）。</summary>
    public bool TrySetUInt32(Guid api, uint value)
    {
        try
        {
            object boxed = value; // 装箱成 uint → 封送为 VT_UI4
            return _api.SetValue(ref api, boxed) >= 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { Marshal.ReleaseComObject(_rcw); } catch { /* 进程退出路径，忽略 */ }
    }
}
