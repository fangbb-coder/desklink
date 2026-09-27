// 媒体通道出站队列（含丢弃策略）。
//
// 为什么单独抽一个类并设为 public：
//   丢弃策略是"保输入响应"这条 DESIGN 要求的实现核心（见下面的 PreferInput），
//   但**端到端用管道制造背压不可靠** —— 命名管道有 64KB 内核缓冲，
//   小帧（鼠标移动只有 10 字节）要灌满缓冲才能让队列真正溢出，
//   测试会变得又慢又不稳定。把队列抽出来后可以精确、确定地验证丢弃行为。
//
// 队列容量按"帧"而不是"字节"计：媒体帧大小差异极大（鼠标移动 10 字节、
// 关键帧 60KB），按字节计会让"多少帧延迟"不可预测；按帧计则延迟上界明确。
using System.IO.Pipes;
using DeskLink.Protocol.Common;

namespace DeskLink.Service.Media;

/// <summary>队列满时的丢弃策略。</summary>
public enum MediaDropPolicy
{
    /// <summary>丢最旧的（画面语义：只要最新的，旧的没有价值）。</summary>
    DropOldest,

    /// <summary>
    /// 优先丢最旧的**鼠标移动**；没有可丢的移动事件时才丢新来的。
    ///
    /// 为什么不能无脑丢最旧：输入流里混着鼠标移动与按键按下/抬起。
    /// 移动丢了无所谓（下一个位置就覆盖了），但**丢掉一个"抬起"会让远端按键永久卡住**
    /// —— 这是比丢一帧画面严重得多的故障。因此宁可丢新来的移动，也要保住按键。
    /// </summary>
    PreferInput,
}

/// <summary>带丢弃策略的媒体帧队列。</summary>
public sealed class MediaFrameQueue
{
    private readonly LinkedList<QueuedFrame> _queue = new();
    private readonly object _gate = new();

    public MediaFrameQueue(int capacity)
    {
        Capacity = Math.Max(1, capacity);
    }

    public int Capacity { get; }

    /// <summary>因队列满而被丢弃的帧数（累计）。</summary>
    public long Dropped { get; private set; }

    /// <summary>当前排队帧数。</summary>
    public int Depth
    {
        get { lock (_gate) return _queue.Count; }
    }

    /// <summary>
    /// 入队一帧（已编码的线上字节 + 其帧类型，类型用于丢弃策略判断）。
    /// 返回 true 表示已入队（调用方需要发一次信号）；false 表示被丢弃。
    /// </summary>
    public bool Enqueue(byte[] frame, ProtocolConstants.FrameType type, MediaDropPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            if (_queue.Count >= Capacity)
            {
                if (policy == MediaDropPolicy.DropOldest)
                {
                    _queue.RemoveFirst();
                    Dropped++;
                }
                else if (TryDropOldestMouseMove())
                {
                    Dropped++;
                }
                else
                {
                    // 没有可丢的移动事件 → 丢新来的，保住按键/滚轮。
                    Dropped++;
                    return false;
                }
            }

            _queue.AddLast(new QueuedFrame(frame, type));
            return true;
        }
    }

    /// <summary>出队一帧；空队列返回 false。</summary>
    public bool TryDequeue(out byte[] frame, out ProtocolConstants.FrameType type)
    {
        lock (_gate)
        {
            if (_queue.First is null)
            {
                frame = Array.Empty<byte>();
                type = default;
                return false;
            }

            var item = _queue.First.Value;
            _queue.RemoveFirst();
            frame = item.Frame;
            type = item.Type;
            return true;
        }
    }

    /// <summary>清空（连接收尾时用）。</summary>
    public void Clear()
    {
        lock (_gate) _queue.Clear();
    }

    private bool TryDropOldestMouseMove()
    {
        for (var node = _queue.First; node is not null; node = node.Next)
        {
            if (node.Value.Type == ProtocolConstants.FrameType.InputMouseMove)
            {
                _queue.Remove(node);
                return true;
            }
        }
        return false;
    }

    private readonly record struct QueuedFrame(byte[] Frame, ProtocolConstants.FrameType Type);
}
