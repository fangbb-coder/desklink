namespace DeskLink.DesktopAgent.Codec;

/// <summary>
/// H.264 Annex-B 码流的最小解析工具。
///
/// 用途：
///   - 验证"会话开始先发 SPS/PPS"（DESIGN 的 DXGI 细节条目）——首帧输出里必须能找到
///     NAL type 7(SPS) 与 8(PPS)；
///   - 判断关键帧（IDR，NAL type 5），供自适应码率与"新客户端加入时补发关键帧"使用。
///
/// 只做解析，不做拼装，保持纯函数以便单元测试直接覆盖。
/// </summary>
public static class AnnexB
{
    public const int NalTypeNonIdrSlice = 1;
    public const int NalTypeIdrSlice = 5;
    public const int NalTypeSei = 6;
    public const int NalTypeSps = 7;
    public const int NalTypePps = 8;
    public const int NalTypeAud = 9;

    /// <summary>
    /// 按 Annex-B 起始码（00 00 01 或 00 00 00 01）切分 NAL 单元。
    /// 返回的每个元素都是**不含起始码**的 NAL（首字节即 NAL header）。
    /// </summary>
    public static List<byte[]> SplitNalUnits(ReadOnlySpan<byte> data)
    {
        var units = new List<byte[]>();
        int i = 0;
        int start = -1;

        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0)
                {
                    // 起始码前若有一个多余的 0（4 字节起始码的尾部），要排除掉
                    int end = i;
                    if (end > start && data[end - 1] == 0) end--;
                    if (end > start) units.Add(data.Slice(start, end - start).ToArray());
                }
                start = i + 3;
                i += 3;
            }
            else
            {
                i++;
            }
        }

        if (start >= 0 && start < data.Length)
        {
            units.Add(data.Slice(start).ToArray());
        }

        return units;
    }

    /// <summary>取 NAL header 里的 nal_unit_type（低 5 位）。</summary>
    public static int GetNalUnitType(ReadOnlySpan<byte> nal)
        => nal.Length == 0 ? -1 : nal[0] & 0x1F;

    public static bool ContainsNalType(ReadOnlySpan<byte> annexB, int nalType)
    {
        foreach (var nal in SplitNalUnits(annexB))
        {
            if (GetNalUnitType(nal) == nalType) return true;
        }
        return false;
    }

    /// <summary>是否包含 SPS 或 PPS。</summary>
    public static bool ContainsParameterSets(ReadOnlySpan<byte> annexB)
        => ContainsNalType(annexB, NalTypeSps) || ContainsNalType(annexB, NalTypePps);

    /// <summary>是否包含 IDR 关键帧。</summary>
    public static bool ContainsKeyFrame(ReadOnlySpan<byte> annexB)
        => ContainsNalType(annexB, NalTypeIdrSlice);
}
