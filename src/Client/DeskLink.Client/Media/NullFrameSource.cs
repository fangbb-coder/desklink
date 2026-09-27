namespace DeskLink.Client.Media;

/// <summary>提供 BGRA 帧的抽象（真解码器或演示用的合成帧源）。</summary>
public interface IFrameSource : IDisposable
{
    /// <summary>帧尺寸；未知时为 0（由第一帧确定）。</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>是否有可用帧。</summary>
    bool IsAvailable { get; }
}

/// <summary>
/// 合成帧源：在没有真实会话（Service 侧媒体管道尚未实现 / 未连接）时，
/// 让远程页仍能显示画面并验证渲染链路，而不是一片空白。
///
/// 它不是"假装连上了"——UI 必须显式标注这是演示画面，避免误导用户。
/// </summary>
public sealed class NullFrameSource : IFrameSource
{
    private int _tick;

    public NullFrameSource(int width = 640, int height = 360)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }
    public bool IsAvailable => true;

    /// <summary>生成下一帧（移动的彩色条纹，便于肉眼确认"画面在刷新"）。</summary>
    public BgraFrame Next()
    {
        _tick++;
        var pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Width + x) * 4;
                pixels[o + 0] = (byte)((x + _tick) & 0xFF);       // B
                pixels[o + 1] = (byte)((y * 2 + _tick) & 0xFF);   // G
                pixels[o + 2] = (byte)((_tick * 3) & 0xFF);       // R
                pixels[o + 3] = 255;                              // A
            }
        }
        return new BgraFrame(Width, Height, pixels);
    }

    public void Dispose() { }
}
