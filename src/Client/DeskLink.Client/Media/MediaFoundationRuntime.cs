using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace DeskLink.Client.Media;

/// <summary>
/// Media Foundation 生命周期与 MFT 枚举的薄封装（客户端副本）。
///
/// 为什么不引用 Agent 里的同名类型：Client 只引用 Protocol，不能依赖 Agent 程序集
/// （否则会把 DXGI/捕获等一整套被控端依赖拖进控制端 UI）。
/// MFStartup/MFShutdown 是进程级的，这里用引用计数避免多个解码器互相拆台。
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

    // 直接用裸常量而不是 Vortice 的枚举名，避免与 Windows SDK 的 MFT_ENUM_FLAG_* 混淆（值取自 mftransform.h）。
    private const uint MftEnumFlagSyncMft = 0x00000001;

    public static List<IMFActivate> EnumSyncTransforms(Guid category, Guid inputSubtype, Guid outputSubtype)
    {
        var result = new List<IMFActivate>();
        IntPtr arrayPtr = IntPtr.Zero;
        try
        {
            var inputInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = inputSubtype };
            var outputInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = outputSubtype };

            MediaFactory.MFTEnumEx(category, MftEnumFlagSyncMft, inputInfo, outputInfo, out arrayPtr, out uint count);
            if (arrayPtr == IntPtr.Zero || count == 0) return result;

            for (uint i = 0; i < count; i++)
            {
                var p = Marshal.ReadIntPtr(arrayPtr, (int)(i * (uint)IntPtr.Size));
                if (p == IntPtr.Zero) continue;
                // SharpGen 的 ComObject(IntPtr) 接管所有权，Dispose 时正好释放 MFTEnumEx 返回的那份引用。
                result.Add(new IMFActivate(p));
            }
        }
        catch
        {
            // 枚举失败返回已收集到的部分。
        }
        finally
        {
            if (arrayPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(arrayPtr);
        }

        return result;
    }

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
