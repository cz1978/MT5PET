using TradePet.Core.Session;
using Xunit;

namespace TradePet.Core.Tests;

public sealed class ServerClockTests
{
    [Fact]
    public void WorkerOffset_BecomesAuthoritativeAndResolvesServerMidnight()
    {
        var time = new StubTimeProvider(new DateTimeOffset(2026, 9, 6, 22, 30, 0, TimeSpan.Zero));
        var clock = new ServerClock(time);

        var update = clock.ApplyWorkerOffset(3 * 60 * 60);

        Assert.True(update.Applied);
        Assert.True(update.ContextChanged);
        Assert.True(update.DateChanged);
        Assert.True(clock.IsAuthoritative);
        Assert.Equal(ServerClockSource.WorkerOffset, clock.Source);
        Assert.Equal(new DateOnly(2026, 9, 7), clock.ServerDate);
    }

    [Fact]
    public void FreshBridgeHeartbeat_WinsUntilFreshnessExpires()
    {
        var time = new StubTimeProvider(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        var clock = new ServerClock(time, TimeSpan.FromSeconds(5));
        clock.ApplyBridgeHeartbeat(new DateOnly(2026, 9, 7), 3 * 60 * 60);

        time.UtcNow = time.UtcNow.AddSeconds(4);
        var ignored = clock.ApplyWorkerOffset(-5 * 60 * 60);

        Assert.False(ignored.Applied);
        Assert.Equal(ServerClockSource.BridgeHeartbeat, clock.Source);
        Assert.Equal(new DateOnly(2026, 9, 7), clock.ServerDate);

        time.UtcNow = time.UtcNow.AddSeconds(2);
        var fallback = clock.ApplyWorkerOffset(-5 * 60 * 60);

        Assert.True(fallback.Applied);
        Assert.Equal(ServerClockSource.WorkerOffset, clock.Source);
        Assert.Equal(new DateOnly(2026, 9, 6), clock.ServerDate);
    }

    [Fact]
    public void BridgeDisconnect_AllowsWorkerToTakeOverImmediately()
    {
        var time = new StubTimeProvider(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        var clock = new ServerClock(time);
        clock.ApplyBridgeHeartbeat(new DateOnly(2026, 9, 6), 3 * 60 * 60);

        clock.MarkBridgeDisconnected();
        var update = clock.ApplyWorkerOffset(2 * 60 * 60);

        Assert.True(update.Applied);
        Assert.Equal(ServerClockSource.WorkerOffset, clock.Source);
        Assert.Equal(2 * 60 * 60, clock.UtcOffsetSeconds);
    }

    [Fact]
    public void ResolveAndDayRange_UseTheSameOffset()
    {
        var time = new StubTimeProvider(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        var clock = new ServerClock(time);
        clock.ApplyWorkerOffset(3 * 60 * 60);

        var date = clock.Resolve(new DateTimeOffset(2026, 9, 6, 22, 0, 0, TimeSpan.Zero));
        var range = clock.ResolveDayUtcRange(date);

        Assert.Equal(new DateOnly(2026, 9, 7), date);
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 21, 0, 0, TimeSpan.Zero), range.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 21, 0, 0, TimeSpan.Zero), range.EndUtc);
    }

    [Fact]
    public void BridgeSourceChange_IsContextChangeEvenWhenOffsetMatchesWorker()
    {
        var time = new StubTimeProvider(new DateTimeOffset(2026, 9, 8, 2, 0, 0, TimeSpan.Zero));
        var clock = new ServerClock(time);
        clock.ApplyWorkerOffset(3 * 60 * 60);

        var update = clock.ApplyBridgeHeartbeat(new DateOnly(2026, 9, 8), 3 * 60 * 60);

        Assert.True(update.ContextChanged);
        Assert.Equal(ServerClockSource.BridgeHeartbeat, clock.Source);
    }

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
