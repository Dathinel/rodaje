using Rodaje.Core.Timing;
using Rodaje.Playback;
using Xunit;

namespace Rodaje.Playback.Tests.Caza;

public class SampleCountingClockCazaTests
{
    [Fact]
    public void Position_ImmediatelyAfterSeek_EqualsSeekDestination()
    {
        var clock = new TestSampleCountingClock(48000);
        var target = Time.FromSeconds(5);
        clock.Seek(target);

        Assert.Equal(target, clock.Position);
    }

    [Fact]
    public void Position_AfterSeekAndSamplesHeard_EqualsSeekDestinationPlusSamplesHeard()
    {
        var clock = new TestSampleCountingClock(48000);
        var target = Time.FromSeconds(10);
        clock.Seek(target);

        clock.SetSamplesPlayed(24000);

        var expected = target + Time.FromSamples(24000, 48000);
        Assert.Equal(expected, clock.Position);
    }

    [Fact]
    public void Position_AfterMultipleSeeks_CalculatesFromLastSeekDestinationAndSamplesSinceThen()
    {
        var clock = new TestSampleCountingClock(48000);
        clock.Seek(Time.FromSeconds(5));
        clock.SetSamplesPlayed(48000);

        var lastTarget = Time.FromSeconds(20);
        clock.Seek(lastTarget);
        clock.SetSamplesPlayed(48000 + 12000);

        var expected = lastTarget + Time.FromSamples(12000, 48000);
        Assert.Equal(expected, clock.Position);
    }

    [Fact]
    public void Position_WhenSamplesPlayedBeforeSeek_IgnoresSamplesHeardBeforeSeek()
    {
        var clock = new TestSampleCountingClock(48000);
        clock.SetSamplesPlayed(96000);

        var target = Time.FromSeconds(1);
        clock.Seek(target);
        clock.SetSamplesPlayed(96000 + 4800);

        var expected = target + Time.FromSamples(4800, 48000);
        Assert.Equal(expected, clock.Position);
    }

    [Fact]
    public void Position_AfterBackwardSeek_CalculatesFromLastSeekDestination()
    {
        var clock = new TestSampleCountingClock(48000);
        clock.Seek(Time.FromSeconds(30));
        clock.SetSamplesPlayed(48000);

        var backwardTarget = Time.FromSeconds(10);
        clock.Seek(backwardTarget);
        clock.SetSamplesPlayed(48000 + 24000);

        var expected = backwardTarget + Time.FromSamples(24000, 48000);
        Assert.Equal(expected, clock.Position);
    }

    [Fact]
    public void Position_SeekToZeroWithZeroSamples_EqualsZero()
    {
        var clock = new TestSampleCountingClock(44100);
        clock.SetSamplesPlayed(88200);
        clock.Seek(Time.Zero);

        Assert.Equal(Time.Zero, clock.Position);
    }

    private sealed class TestSampleCountingClock : SampleCountingClock
    {
        private long _samplesPlayed;

        public TestSampleCountingClock(int sampleRate)
            : base(sampleRate)
        {
        }

        public void SetSamplesPlayed(long samplesPlayed)
        {
            _samplesPlayed = samplesPlayed;
        }

        protected override long ReadSamplesPlayed() => _samplesPlayed;
    }
}
