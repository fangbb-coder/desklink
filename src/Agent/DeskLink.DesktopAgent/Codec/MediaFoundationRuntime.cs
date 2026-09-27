using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace DeskLink.DesktopAgent.Codec;

/// <summary>
/// Media Foundation 生命周期与 MFT 枚举的薄封装。
///
/// 为什么需要引用计数：<c>MFStartup</c> / <c>MFShutdown</c> 是进程级的，
/// 而编码器、解码器可能同时存在（例如自检里先编码再解码）。多个对象各自调用
/// MFStartup/MFShutdown 会把别人的 MF 平台拆掉，因此这里统一做进程内引用计数。
/// </summary>
internal static class MediaFoundationRuntime
{
    private static readonly object Gate = new();
    private static int _refCount;

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_refCount == 0)
            {
                MediaFactory.MFStartup(false);
            }
            _refCount++;
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_refCount == 0) return;
            _refCount--;
            if (_refCount == 0)
            {
                try { MediaFactory.MFShutdown(); } catch { /* 进程退出路径，忽略 */ }
            }
        }
    }

    // MFTEnumEx 的 flags 直接写常量而不是用 Vortice 的 EnumFlag 枚举：
    // 枚举成员名（EnumFlagSyncmft 等）容易与 Windows SDK 的 MFT_ENUM_FLAG_* 混淆，
    // 用裸常量 + 注释更不容易出错。值取自 mftransform.h。
    private const uint MftEnumFlagSyncMft = 0x00000001;

    /// <summary>
    /// 按 类别 + 输入子类型 + 输出子类型 枚举 MFT，返回可用的 IMFActivate 列表。
    ///
    /// 注意：<paramref name="flags"/> 用同步 MFT 标志时**不会**返回异步 MFT。
    /// Windows 上绝大多数硬件 H.264 编码器是异步 MFT（需要 MF_TRANSFORM_ASYNC_UNLOCK
    /// + 事件驱动的 ProcessOutput），v1 明确不支持异步路径（见 DESIGN 风险回顾：
    /// "v1 软件同步 MFT 保底可靠，硬件路径独立开关 + 自动回落"）。
    /// 因此"先试硬件"实际只在驱动暴露同步硬件 MFT 时才会命中，否则自动落到软件 MFT。
    /// </summary>
    public static List<IMFActivate> EnumTransforms(Guid category, Guid inputSubtype, Guid outputSubtype, uint flags)
    {
        var result = new List<IMFActivate>();
        IntPtr arrayPtr = IntPtr.Zero;
        try
        {
            var inputInfo = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = inputSubtype,
            };
            var outputInfo = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = outputSubtype,
            };

            MediaFactory.MFTEnumEx(category, flags, inputInfo, outputInfo, out arrayPtr, out uint count);
            if (arrayPtr == IntPtr.Zero || count == 0) return result;

            for (uint i = 0; i < count; i++)
            {
                var p = Marshal.ReadIntPtr(arrayPtr, (int)(i * (uint)IntPtr.Size));
                if (p == IntPtr.Zero) continue;
                // SharpGen 的 ComObject(IntPtr) 构造函数接管所有权（不额外 AddRef），
                // 因此这里 new 出来的对象 Dispose 时正好释放 MFTEnumEx 返回的那一份引用。
                result.Add(new IMFActivate(p));
            }
        }
        catch
        {
            // 枚举失败（MF 未启动 / 无匹配 MFT）返回已收集到的部分。
        }
        finally
        {
            if (arrayPtr != IntPtr.Zero)
            {
                // MFTEnumEx 返回的数组由调用方用 CoTaskMemFree 释放。
                Marshal.FreeCoTaskMem(arrayPtr);
            }
        }

        return result;
    }

    /// <summary>
    /// 按 类别 + 输入子类型 + 输出子类型 枚举**同步** MFT。
    ///
    /// 注意 MFT_ENUM_FLAG_* 的语义：多个标志之间是**并集**而不是交集。
    /// 例如 `SYNCMFT|HARDWARE` 返回的是"同步 MFT 或 硬件 MFT"，会把异步硬件 MFT 也带进来。
    /// 因此这里只用 SYNCMFT，然后逐个用 <see cref="IsHardware"/> 判断是不是硬件实现，
    /// 而不是靠枚举标志去推断——否则会把软件 MFT 误标成硬件。
    ///
    /// v1 明确不支持异步 MFT（需要 MF_TRANSFORM_ASYNC_UNLOCK + IMFMediaEventGenerator
    /// 事件驱动），见 DESIGN 风险回顾："v1 软件同步 MFT 保底可靠，硬件路径独立开关 + 自动回落"。
    /// </summary>
    public static List<IMFActivate> EnumSyncTransforms(Guid category, Guid inputSubtype, Guid outputSubtype)
        => EnumTransforms(category, inputSubtype, outputSubtype, MftEnumFlagSyncMft);

    /// <summary>
    /// 判断某个 MFT 是否为硬件实现。
    /// 依据是 MFT_ENUM_HARDWARE_URL_Attribute：硬件 MFT 一定会带上这个属性。
    /// </summary>
    public static bool IsHardware(IMFActivate activate)
    {
        try
        {
            var url = activate.GetString(TransformAttributeKeys.MftEnumHardwareUrlAttribute);
            return !string.IsNullOrWhiteSpace(url);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取 activate 的友好名；拿不到就返回空串。</summary>
    public static string GetFriendlyName(IMFActivate activate)
    {
        try
        {
            var name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
            return string.IsNullOrWhiteSpace(name) ? "" : name;
        }
        catch
        {
            return "";
        }
    }
}
