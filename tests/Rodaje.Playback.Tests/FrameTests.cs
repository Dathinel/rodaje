using Rodaje.Core.Timing;
using Rodaje.Playback.Tests.Support;

namespace Rodaje.Playback.Tests;

public class FrameTests
{
    [Fact]
    public void DecodedFrame_LiberaElContenidoConLaUltimaReferencia()
    {
        var tracker = new PayloadTracker();
        DecodedFrame frame = Frames.Make(10, Rational.Fps25, tracker);
        Assert.Equal(Time.FromSeconds(0.4), frame.Pts);
        Assert.Equal(Time.FromSeconds(0.04), frame.Duration);
        Assert.Equal(Time.FromSeconds(0.44), frame.End);
        Assert.True(frame.Contains(Time.FromSeconds(0.4)));
        Assert.False(frame.Contains(Time.FromSeconds(0.44)));
        Assert.False(frame.Contains(Time.FromSeconds(0.39)));

        Assert.True(frame.TryRetain());
        frame.Dispose();
        Assert.Equal(1, tracker.Live);
        Assert.False(frame.IsReleased);

        frame.Dispose();
        Assert.Equal(0, tracker.Live);
        Assert.True(frame.IsReleased);
        Assert.False(frame.TryRetain());
        frame.Dispose();
        Assert.Equal(0, tracker.Live);
    }

    [Fact]
    public void DecodedFrame_ValidaArgumentos()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedFrame(-1, Time.Zero, Time.FromTicks(1), null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedFrame(0, Time.Zero, Time.Zero, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedFrame(0, Time.Zero, Time.FromTicks(1), null, -1));
        using var noPayload = new DecodedFrame(0, Time.Zero, Time.FromTicks(1), null, 0);
        Assert.Null(noPayload.Payload);
    }

    [Fact]
    public void DecodedFrame_ReferenciasConcurrentesNoPierdenLaCuenta()
    {
        var tracker = new PayloadTracker();
        DecodedFrame frame = Frames.Make(0, Rational.Fps25, tracker);
        Parallel.For(0, 10_000, _ =>
        {
            Assert.True(frame.TryRetain());
            frame.Dispose();
        });
        Assert.Equal(1, tracker.Live);
        frame.Dispose();
        Assert.Equal(0, tracker.Live);
    }
}
