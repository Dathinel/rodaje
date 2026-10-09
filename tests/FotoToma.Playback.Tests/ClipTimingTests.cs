using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Playback.Tests;

public class ClipTimingTests
{
    [Fact]
    public void Cuadros_ConEntradaAMitadDeCuadro()
    {
        // 25 fps: cuadro 10 = [0,40 s, 0,44 s); entrada y salida a mitad de cuadro
        var timing = new ClipTiming(Time.FromSeconds(5), Time.FromSeconds(0.42), Time.FromSeconds(1.01), Rational.Fps25);

        Assert.Equal(Time.FromSeconds(0.59), timing.Duration);
        Assert.Equal(Time.FromSeconds(5.59), timing.End);
        Assert.Equal(10, timing.FirstFrame);
        Assert.Equal(25, timing.LastFrame);
        Assert.Equal(10, timing.FrameAt(Time.FromSeconds(5)));
        Assert.Equal(10, timing.FrameAt(Time.Zero));
        Assert.Equal(11, timing.FrameAt(Time.FromSeconds(5.02)));
        Assert.Equal(25, timing.FrameAt(Time.FromSeconds(5.59)));
        Assert.Equal(25, timing.FrameAt(Time.FromSeconds(99)));
        Assert.Equal(Time.FromSeconds(5), timing.TimelineStartOf(10));
        Assert.Equal(Time.FromSeconds(5), timing.TimelineStartOf(-3));
        Assert.Equal(Time.FromSeconds(5.02), timing.TimelineStartOf(11));
        Assert.Equal(Time.FromSeconds(5.58), timing.TimelineStartOf(999));
        Assert.Equal(Time.FromSeconds(0.42), timing.ToSource(Time.FromSeconds(5)));
        Assert.Equal(Time.FromSeconds(5), timing.ToTimeline(Time.FromSeconds(0.42)));
    }

    [Fact]
    public void SalidaJustoEnBordeDeCuadro_NoIncluyeElSiguiente()
    {
        var timing = new ClipTiming(Time.Zero, Time.Zero, Time.FromFrames(100, Rational.Ntsc30), Rational.Ntsc30);
        Assert.Equal(0, timing.FirstFrame);
        Assert.Equal(99, timing.LastFrame);
    }

    [Fact]
    public void FromClip_CopiaLosTiempos()
    {
        var clip = new Clip { Start = Time.FromSeconds(3), SourceIn = Time.FromSeconds(1), SourceOut = Time.FromSeconds(2) };
        var timing = ClipTiming.FromClip(clip, Rational.Fps24);
        Assert.Equal(new ClipTiming(Time.FromSeconds(3), Time.FromSeconds(1), Time.FromSeconds(2), Rational.Fps24), timing);
        Assert.Throws<ArgumentNullException>(() => ClipTiming.FromClip(null!, Rational.Fps24));
    }

    [Fact]
    public void Constructor_ValidaRango()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClipTiming(Time.Zero, Time.FromSeconds(2), Time.FromSeconds(2), Rational.Fps25));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClipTiming(Time.Zero, Time.FromTicks(-1), Time.FromSeconds(2), Rational.Fps25));
    }
}
