// P5 FileAckTracker 测试。

using DeskLink.Service.FileTransfer;
using Xunit;

namespace Service.Tests;

public class FileAckTrackerTests
{
    [Fact]
    public void Init_ResetsState()
    {
        var t = new FileAckTracker();
        t.Init(10);
        Assert.Equal(10uL, t.TotalChunks);
        Assert.Equal(0uL, t.AckedCount);
        Assert.True(t.IsComplete() == false);
    }

    [Fact]
    public void RecordAck_SingleRange()
    {
        var t = new FileAckTracker();
        t.Init(10);
        t.RecordAck(0, new[] { true, true, true, false, false });

        Assert.Equal(3uL, t.AckedCount);
        var missing = t.MissingChunks(10);
        Assert.Equal(new ulong[] { 3, 4, 5, 6, 7, 8, 9 }, missing);
    }

    [Fact]
    public void RecordAck_MultipleRanges_Merge()
    {
        var t = new FileAckTracker();
        t.Init(10);
        t.RecordAck(0, new[] { true, true, false, false, false });
        t.RecordAck(5, new[] { true, true, false, true, true });

        // acked: 0,1,5,6,8,9
        Assert.Equal(6uL, t.AckedCount);
        var missing = t.MissingChunks(10);
        Assert.Equal(new ulong[] { 2, 3, 4, 7 }, missing);
    }

    [Fact]
    public void RecordAck_AdjacentRanges_Merge()
    {
        var t = new FileAckTracker();
        t.Init(10);
        t.RecordAck(0, new[] { true, true });
        t.RecordAck(2, new[] { true, true });

        Assert.Equal(4uL, t.AckedCount);
        var missing = t.MissingChunks(10);
        Assert.Equal(new ulong[] { 4, 5, 6, 7, 8, 9 }, missing);
    }

    [Fact]
    public void RecordAck_IgnoresOutOfRange()
    {
        var t = new FileAckTracker();
        t.Init(5);
        t.RecordAck(3, new[] { true, true, true, true }); // 3,4,5,6 —— 5,6 越界

        Assert.Equal(2uL, t.AckedCount);
    }

    [Fact]
    public void MissingChunks_RespectsMaxCount()
    {
        var t = new FileAckTracker();
        t.Init(100);
        var missing = t.MissingChunks(5);
        Assert.Equal(5, missing.Length);
        Assert.Equal(new ulong[] { 0, 1, 2, 3, 4 }, missing);
    }

    [Fact]
    public void IsComplete_TrueWhenAllAcked()
    {
        var t = new FileAckTracker();
        t.Init(5);
        t.RecordAck(0, new[] { true, true, true, true, true });
        Assert.True(t.IsComplete());
    }

    [Fact]
    public void IsComplete_EmptyTotal()
    {
        var t = new FileAckTracker();
        t.Init(0);
        Assert.True(t.IsComplete());
    }

    [Fact]
    public void ConcurrentRecordAck_DoesNotCrash()
    {
        var t = new FileAckTracker();
        t.Init(1000);
        var tasks = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            var baseChunk = (ulong)(i * 10);
            tasks.Add(Task.Run(() =>
            {
                var bits = new bool[10];
                for (var j = 0; j < 10; j++) bits[j] = true;
                t.RecordAck(baseChunk, bits);
            }));
        }
        Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));
        Assert.Equal(500uL, t.AckedCount);
    }
}
