namespace DeskLink.DesktopAgent.Capture;

/// <summary>图像比对结果（回环测试与 `--test-pattern` 自检共用）。</summary>
public sealed record FrameComparisonResult(
    double MeanAbsDiff,
    double MaxBlockMeanDiff,
    int BlockCount,
    int Width,
    int Height)
{
    public bool WithinTolerance(double meanTolerance, double blockTolerance)
        => MeanAbsDiff <= meanTolerance && MaxBlockMeanDiff <= blockTolerance;
}

/// <summary>
/// BGRA 图像比对工具。
///
/// 为什么不能逐字节比对：H.264 是有损编码，即使码率很高，平坦区也会有 ±1~2 的
/// 量化误差，锐利边缘会有振铃。逐字节比对必然失败，且失败也不代表实现有错。
/// 因此按"块平均"比较：把图像切成 NxN 块，先对每块求平均颜色再比对。
/// 块平均天然抑制高频振铃，同时保留"颜色整体偏了"这类真正的错误信号——
/// 例如 BGRA/NV12 通道顺序写反、BT.601/709 混用，都会让某个块的均值明显偏移。
/// </summary>
public static class FrameComparison
{
    public static FrameComparisonResult Compare(
        ReadOnlySpan<byte> expected, int expectedStride,
        ReadOnlySpan<byte> actual, int actualStride,
        int width, int height,
        int blockSize = 16)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize));

        double sumAbs = 0;
        long sampleCount = 0;

        double maxBlockDiff = 0;
        int blockCount = 0;

        for (int by = 0; by < height; by += blockSize)
        {
            int bh = Math.Min(blockSize, height - by);
            for (int bx = 0; bx < width; bx += blockSize)
            {
                int bw = Math.Min(blockSize, width - bx);

                // 每块累计 B/G/R 三个通道的绝对差（跳过 alpha，它是常量 255，没有信息量）。
                long blockSum = 0;
                long blockSamples = 0;

                for (int y = by; y < by + bh; y++)
                {
                    int eRow = y * expectedStride;
                    int aRow = y * actualStride;
                    for (int x = bx; x < bx + bw; x++)
                    {
                        int ep = eRow + x * 4;
                        int ap = aRow + x * 4;
                        for (int c = 0; c < 3; c++)
                        {
                            int d = Math.Abs(expected[ep + c] - actual[ap + c]);
                            blockSum += d;
                            sumAbs += d;
                            sampleCount++;
                            blockSamples++;
                        }
                    }
                }

                if (blockSamples > 0)
                {
                    double blockMean = (double)blockSum / blockSamples;
                    if (blockMean > maxBlockDiff) maxBlockDiff = blockMean;
                    blockCount++;
                }
            }
        }

        double mean = sampleCount > 0 ? sumAbs / sampleCount : double.MaxValue;
        return new FrameComparisonResult(mean, maxBlockDiff, blockCount, width, height);
    }
}
