using DeskLink.Service.Relay;
using Xunit;

namespace DeskLink.Service.Tests;

public class BackoffPolicyTests
{
    [Fact]
    public void First_Delay_Is_Approximately_Base()
    {
        var b = new BackoffPolicy(baseMs: 1_000, maxMs: 30_000);
        // ±20% 抖动
        var d = b.NextDelayMs();
        Assert.InRange(d, 800, 1200);
        Assert.Equal(1, b.Attempt);
    }

    [Fact]
    public void Delay_Doubles_Until_Max()
    {
        var b = new BackoffPolicy(baseMs: 1_000, maxMs: 8_000);
        var d1 = b.NextDelayMs();
        var d2 = b.NextDelayMs();
        var d3 = b.NextDelayMs();
        var d4 = b.NextDelayMs();
        var d5 = b.NextDelayMs();

        Assert.InRange(d1, 800, 1200);
        Assert.InRange(d2, 1600, 2400);
        Assert.InRange(d3, 3200, 4800);
        Assert.InRange(d4, 6400, 9600);
        Assert.InRange(d5, 6400, 9600); // clamped at max 8000
        Assert.Equal(5, b.Attempt);
    }

    [Fact]
    public void Reset_Restarts_From_Base()
    {
        var b = new BackoffPolicy(baseMs: 1_000, maxMs: 30_000);
        for (int i = 0; i < 5; i++) b.NextDelayMs();
        b.Reset();
        Assert.Equal(0, b.Attempt);
        var d = b.NextDelayMs();
        Assert.InRange(d, 800, 1200);
    }

    [Fact]
    public void Invalid_Base_Or_Max_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffPolicy(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffPolicy(2_000, 1_000));
    }
}
