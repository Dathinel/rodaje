using FotoToma.Core.Timing;
using FotoToma.Playback.Tests.Support;

namespace FotoToma.Playback.Tests;

public class ClockTests
{
    [Fact]
    public void ManualClock_SoloAvanzaMientrasCorre()
    {
        var clock = new ManualClock(Time.FromSeconds(1));
        clock.Advance(Time.FromSeconds(5));
        Assert.Equal(Time.FromSeconds(1), clock.Position);
        Assert.False(clock.IsRunning);

        clock.Start();
        clock.Advance(Time.FromSeconds(2));
        Assert.True(clock.IsRunning);
        Assert.Equal(Time.FromSeconds(3), clock.Position);

        clock.Pause();
        clock.Advance(Time.FromSeconds(2));
        Assert.Equal(Time.FromSeconds(3), clock.Position);

        clock.Seek(Time.FromSeconds(10));
        Assert.Equal(Time.FromSeconds(10), clock.Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(Time.FromTicks(-1)));
    }

    [Fact]
    public void SystemClock_MideElTiempoDePared()
    {
        var provider = new FakeTimeProvider();
        var clock = new SystemClock(provider);
        provider.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(Time.Zero, clock.Position);

        clock.Start();
        clock.Start();
        provider.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.True(clock.IsRunning);
        Assert.Equal(Time.FromSeconds(1.5), clock.Position);

        clock.Pause();
        clock.Pause();
        provider.Advance(TimeSpan.FromSeconds(3));
        Assert.False(clock.IsRunning);
        Assert.Equal(Time.FromSeconds(1.5), clock.Position);

        clock.Start();
        provider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Time.FromSeconds(2.5), clock.Position);

        clock.Seek(Time.FromSeconds(60));
        provider.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(Time.FromSeconds(60.25), clock.Position);
    }

    [Fact]
    public void SystemClock_PorDefectoUsaElRelojDelSistema()
    {
        var clock = new SystemClock();
        clock.Start();
        Time first = clock.Position;
        Assert.True(clock.Position >= first);
        Assert.Throws<ArgumentNullException>(() => new SystemClock(null!));
    }

    [Fact]
    public void SampleCountingClock_SumaMuestrasDesdeElUltimoSeek()
    {
        var clock = new FakeDeviceClock(48_000);
        Assert.Equal(48_000, clock.SampleRate);

        clock.Start();
        clock.Played = 48_000;
        Assert.Equal(Time.FromSeconds(1), clock.Position);

        clock.Pause();
        Assert.False(clock.IsRunning);
        clock.Start();
        clock.Played = 72_000;
        Assert.Equal(Time.FromSeconds(1.5), clock.Position);

        clock.Seek(Time.FromSeconds(30));
        Assert.Equal(Time.FromSeconds(30), clock.Position);
        clock.Played += 4_800;
        Assert.Equal(Time.FromSeconds(30.1), clock.Position);
        Assert.Equal(["start", "pause", "start", "seek 00:00:30.000"], clock.Calls);
    }

    [Fact]
    public void SampleCountingClock_ValidaLaTasa() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FakeDeviceClock(0));

    private sealed class FakeDeviceClock(int sampleRate) : SampleCountingClock(sampleRate)
    {
        public long Played { get; set; }

        public List<string> Calls { get; } = [];

        protected override long ReadSamplesPlayed() => Played;

        protected override void OnStart()
        {
            Calls.Add("start");
            base.OnStart();
        }

        protected override void OnPause()
        {
            Calls.Add("pause");
            base.OnPause();
        }

        protected override void OnSeek(Time position)
        {
            Calls.Add($"seek {position}");
            base.OnSeek(position);
        }
    }
}
