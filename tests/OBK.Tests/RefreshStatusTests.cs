using OBK.AndroidApp;
using Xunit;

namespace OBK.Tests;

public sealed class RefreshStatusTests
{
    [Fact]
    public void SummaryExpiresAtOneMinuteAndOnlyOnce()
    {
        var clock = new TestClock();
        var timeout = new RefreshStatusTimeout(clock);
        timeout.Start();
        clock.Seconds = 59;
        Assert.False(timeout.TryExpire());
        clock.Seconds = 60;
        Assert.True(timeout.TryExpire());
        Assert.False(timeout.Pending);
        Assert.False(timeout.TryExpire());
    }

    [Fact]
    public void NewRefreshGetsItsOwnFullMinute()
    {
        var clock = new TestClock();
        var timeout = new RefreshStatusTimeout(clock);
        timeout.Start();
        clock.Seconds = 40;
        timeout.Start();
        clock.Seconds = 60;
        Assert.False(timeout.TryExpire());
        clock.Seconds = 99;
        Assert.False(timeout.TryExpire());
        clock.Seconds = 100;
        Assert.True(timeout.TryExpire());
    }

    [Fact]
    public void AccountChangeOrNewOperationCancelsOldSummary()
    {
        var clock = new TestClock();
        var timeout = new RefreshStatusTimeout(clock);
        timeout.Start();
        timeout.Cancel();
        clock.Seconds = 60;
        Assert.False(timeout.Pending);
        Assert.False(timeout.TryExpire());
    }

    private sealed class TestClock : TimeProvider
    {
        internal long Seconds { get; set; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
}
