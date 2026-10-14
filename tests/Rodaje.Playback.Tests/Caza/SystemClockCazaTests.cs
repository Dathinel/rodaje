using System;
using System.Collections.Concurrent;
using System.Threading;
using Rodaje.Core.Timing;
using Rodaje.Playback;
using Xunit;

namespace Rodaje.Playback.Tests.Caza;

public sealed class SystemClockCazaTests
{
    [Fact]
    public void InjectableTimeProviderDrivesWallClockPosition()
    {
        var provider = new ControllableTimeProvider();
        var clock = new SystemClock(provider);

        Assert.Equal(Time.Zero, clock.Position);
        Assert.False(clock.IsRunning);

        clock.Start();
        provider.Advance(TimeSpan.FromSeconds(2));

        Assert.True(clock.IsRunning);
        Assert.Equal(Time.FromSeconds(2), clock.Position);

        provider.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(Time.FromSeconds(2.5), clock.Position);
    }

    [Fact]
    public void PauseFreezesPositionAndUpdatesIsRunning()
    {
        var provider = new ControllableTimeProvider();
        var clock = new SystemClock(provider);

        clock.Start();
        provider.Advance(TimeSpan.FromSeconds(3));
        Assert.True(clock.IsRunning);
        Assert.Equal(Time.FromSeconds(3), clock.Position);

        clock.Pause();
        Assert.False(clock.IsRunning);

        provider.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(Time.FromSeconds(3), clock.Position);

        clock.Start();
        Assert.True(clock.IsRunning);
        provider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Time.FromSeconds(4), clock.Position);
    }

    [Fact]
    public void SeekUpdatesPositionWithoutChangingRunningState()
    {
        var provider = new ControllableTimeProvider();
        var clock = new SystemClock(provider);

        clock.Seek(Time.FromSeconds(10));
        Assert.False(clock.IsRunning);
        Assert.Equal(Time.FromSeconds(10), clock.Position);

        clock.Start();
        Assert.True(clock.IsRunning);
        provider.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(Time.FromSeconds(12), clock.Position);

        clock.Seek(Time.FromSeconds(40));
        Assert.True(clock.IsRunning);
        Assert.Equal(Time.FromSeconds(40), clock.Position);

        provider.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(Time.FromSeconds(43), clock.Position);
    }

    [Fact]
    public void PositionDoesNotRetrocedeWhileRunning()
    {
        var provider = new ControllableTimeProvider();
        var clock = new SystemClock(provider);

        clock.Start();
        Time previous = clock.Position;

        for (int i = 0; i < 10; i++)
        {
            provider.Advance(TimeSpan.FromMilliseconds(100));
            Time current = clock.Position;
            Assert.True(current >= previous);
            previous = current;
        }
    }

    [Fact]
    public void PositionCanRetrocedeWhenSeekOccurs()
    {
        var provider = new ControllableTimeProvider();
        var clock = new SystemClock(provider);

        clock.Start();
        provider.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(Time.FromSeconds(10), clock.Position);

        clock.Seek(Time.FromSeconds(2));
        Assert.Equal(Time.FromSeconds(2), clock.Position);

        provider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Time.FromSeconds(3), clock.Position);
    }

    [Fact]
    public void OperationsAreThreadSafeUnderConcurrency()
    {
        var provider = new ControllableTimeProvider();
        var clock = new SystemClock(provider);
        using var barrier = new Barrier(4);
        var exceptions = new ConcurrentBag<Exception>();

        var threads = new[]
        {
            new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < 100; i++)
                {
                    try
                    {
                        clock.Start();
                        clock.Pause();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }),
            new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < 100; i++)
                {
                    try
                    {
                        clock.Seek(Time.FromSeconds(i));
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }),
            new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < 100; i++)
                {
                    try
                    {
                        _ = clock.Position;
                        _ = clock.IsRunning;
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }),
            new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < 100; i++)
                {
                    try
                    {
                        provider.Advance(TimeSpan.FromMilliseconds(5));
                        _ = clock.Position;
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            })
        };

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.Empty(exceptions);
    }

    private sealed class ControllableTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta) => _ = Interlocked.Add(ref _timestamp, delta.Ticks);
    }
}
