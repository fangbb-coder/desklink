using DeskLink.DesktopAgent.Capture;
using DeskLink.DesktopAgent.Codec;

namespace DeskLink.DesktopAgent.Pipeline;

/// <summary>回环自检结果。</summary>
public sealed record LoopbackResult(
    bool Success,
    bool Skipped,
    string Message,
    int Width,
    int Height,
    int EncodedBytes,
    double MeanAbsDiff,
    double MaxBlockMeanDiff,
    bool HasParameterSets,
    bool HasKeyFrame,
    string EncoderBackend,
    string DecoderBackend);

/// <summary>
/// 本地回环管线：测试图源 → NV12 → H.264 编码 → H.264 解码 → BGRA → 像素校验。
///
/// 这是 P7 的验收点（DESIGN："本地回环（测试图源→编码→解码→像素校验）"）。
/// 把它做成一个可被测试和 CLI 同时调用的纯函数，好处是：
///   - 单测直接断言像素误差，不需要启动进程；
///   - `--test-pattern` 用同一段代码，所以"命令行自检通过"和"单测通过"含义一致，
///     不存在"两套逻辑各自为政"。
/// 编码器/解码器不可用时返回 Skipped=true 并给出原因（绝不假装成功）。
/// </summary>
public static class LoopbackPipeline
{
    public const double DefaultMeanTolerance = 10.0;
    public const double DefaultBlockTolerance = 28.0;

    /// <param name="rotationDegrees">
    /// 显示器旋转角度（0/90/180/270，P9）。旋转发生在**编码之前**（BGRA 阶段），
    /// 因此 90/270 会交换编码尺寸 —— 这正是纵向显示器上必须做的事，
    /// 否则编码器尺寸与画面方向不符（远端横躺，或 MFT 直接拒绝）。
    /// </param>
    public static LoopbackResult Run(
        int width = 320,
        int height = 240,
        int frameCount = 3,
        bool preferHardware = false,
        double meanTolerance = DefaultMeanTolerance,
        double blockTolerance = DefaultBlockTolerance,
        int rotationDegrees = 0)
    {
        if (frameCount < 1) frameCount = 1;
        if (!FrameRotation.IsValidAngle(rotationDegrees))
        {
            return Skipped(width, height, $"非法旋转角度 {rotationDegrees}（只支持 0/90/180/270）");
        }

        // 旋转会交换宽高，编码器必须用**旋转后**的尺寸。
        var (encWidth, encHeight) = FrameRotation.RotatedSize(width, height, rotationDegrees);
        if ((encWidth & 1) != 0 || (encHeight & 1) != 0)
        {
            return Skipped(width, height, $"旋转后尺寸 {encWidth}x{encHeight} 含奇数边，NV12 无法做 2x2 色度子采样");
        }

        using var source = new TestPatternSource(width, height);

        // 编码最后要校验的那一帧，作为参考原图。
        BgraFrame? reference = null;
        var encodedChunks = new List<byte[]>();

        H264Encoder? encoder = null;
        try
        {
            var settings = new H264EncoderSettings(
                encWidth, encHeight, Fps: 30,
                BitrateBps: Math.Max(2_000_000, encWidth * encHeight * 30 * 3 / 10),
                PreferHardware: preferHardware,
                KeyFrameInterval: 30);

            encoder = H264Encoder.TryCreate(settings, out var encoderError);
            if (encoder is null)
            {
                return Skipped(width, height, $"H.264 编码器不可用：{encoderError}");
            }

            for (int i = 0; i < frameCount; i++)
            {
                // animate:false —— 所有帧内容一致，避免"解码出的是哪一帧"的歧义（见 TestPatternSource.Render）。
                var frame = source.Render(i, animate: false);
                // 旋转在转换之前做：BGRA 旋转是纯行列置换，比旋转 NV12 好验证得多
                // （旋转 NV12 要同时处理 Y 与 UV 的 2x2 色度对，容易错位且表现为整幅偏色）。
                var oriented = FrameRotation.Rotate(frame, rotationDegrees);
                reference = oriented;
                var nv12 = FrameConverter.BgraToNv12Cpu(oriented.Pixels, oriented.Width, oriented.Height, oriented.Stride);
                // 首帧强制 IDR，顺带验证"SPS/PPS 会话开始先发"。
                var chunk = encoder.Encode(nv12, forceKeyFrame: i == 0);
                if (chunk is not null) encodedChunks.Add(chunk);
            }

            // MF 软件 H.264 编码器有前瞻缓冲：短序列不 DRAIN 就一帧码流都拿不到。
            encodedChunks.AddRange(encoder.FlushChunks());

            if (encodedChunks.Count == 0)
            {
                return new LoopbackResult(false, false, "编码器没有产出任何码流", encWidth, encHeight, 0, -1, -1, false, false,
                    encoder.BackendName, "n/a");
            }

            int totalBytes = 0;
            foreach (var c in encodedChunks) totalBytes += c.Length;
            bool hasParameterSets = AnnexB.ContainsNalType(encodedChunks[0], AnnexB.NalTypeSps)
                                    && AnnexB.ContainsNalType(encodedChunks[0], AnnexB.NalTypePps);
            bool hasKeyFrame = AnnexB.ContainsKeyFrame(encodedChunks[0]);

            H264Decoder? decoder = H264Decoder.TryCreate(encWidth, encHeight, out var decoderError);
            if (decoder is null)
            {
                return Skipped(width, height, $"H.264 解码器不可用：{decoderError}");
            }

            using (decoder)
            {
                Nv12Frame? decoded = null;
                foreach (var chunk in encodedChunks)
                {
                    var f = decoder.Decode(chunk);
                    if (f is not null) decoded = f;
                }
                decoded ??= decoder.Flush();

                if (decoded is null)
                {
                    return new LoopbackResult(false, false, "解码器没有产出画面", encWidth, encHeight, totalBytes, -1, -1,
                        hasParameterSets, hasKeyFrame, encoder.BackendName, decoder.BackendName);
                }

                var referenceFrame = reference!;
                int decodedStride = decoded.Width * 4;
                var decodedBgra = new byte[decodedStride * decoded.Height];
                FrameConverter.Nv12ToBgraCpu(decoded.Buffer, decoded.Width, decoded.Height, decodedBgra, decodedStride);

                int compareWidth = Math.Min(referenceFrame.Width, decoded.Width);
                int compareHeight = Math.Min(referenceFrame.Height, decoded.Height);
                var comparison = FrameComparison.Compare(
                    referenceFrame.Pixels, referenceFrame.Stride,
                    decodedBgra, decodedStride,
                    compareWidth, compareHeight);

                bool ok = comparison.WithinTolerance(meanTolerance, blockTolerance);
                string message = ok
                    ? $"回环校验通过（均值差 {comparison.MeanAbsDiff:F2}，最大块均差 {comparison.MaxBlockMeanDiff:F2}）"
                    : $"回环校验未通过（均值差 {comparison.MeanAbsDiff:F2} 限 {meanTolerance}，最大块均差 {comparison.MaxBlockMeanDiff:F2} 限 {blockTolerance}）";

                return new LoopbackResult(
                    ok, false, message, encWidth, encHeight, totalBytes,
                    comparison.MeanAbsDiff, comparison.MaxBlockMeanDiff,
                    hasParameterSets, hasKeyFrame,
                    encoder.BackendName, decoder.BackendName);
            }
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    private static LoopbackResult Skipped(int width, int height, string message)
        => new(false, true, message, width, height, 0, -1, -1, false, false, "n/a", "n/a");
}
