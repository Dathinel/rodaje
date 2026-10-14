using Rodaje.Core.Timing;
using Rodaje.Playback;
using Xunit;

namespace Rodaje.Playback.Tests.Caza;

public class DecodedFrameCazaTests
{
    [Fact]
    public void Constructor_InitializesProperties()
    {
        using var payload = new TestPayload();
        var pts = Time.FromSeconds(1);
        var duration = Time.FromSeconds(0.04);
        var frame = new DecodedFrame(10, pts, duration, payload, 2048);

        Assert.Equal(10, frame.Index);
        Assert.Equal(pts, frame.Pts);
        Assert.Equal(duration, frame.Duration);
        Assert.Same(payload, frame.Payload);
        Assert.Equal(2048, frame.SizeBytes);

        frame.Dispose();
    }

    [Fact]
    public void TryRetain_WhenActive_IncrementsReferenceCount()
    {
        using var payload = new TestPayload();
        var frame = new DecodedFrame(0, Time.Zero, Time.FromSeconds(1), payload, 100);

        bool retained = frame.TryRetain();

        Assert.True(retained);

        frame.Dispose();
        Assert.False(payload.IsDisposed);

        frame.Dispose();
        Assert.True(payload.IsDisposed);
    }

    [Fact]
    public void TryRetain_WhenAlreadyReleased_ReturnsFalse()
    {
        var frame = new DecodedFrame(0, Time.Zero, Time.FromSeconds(1), null, 100);
        frame.Dispose();

        bool retained = frame.TryRetain();

        Assert.False(retained);
    }

    [Fact]
    public void TryRetain_AfterMultipleReferencesReleased_ReturnsFalse()
    {
        var frame = new DecodedFrame(0, Time.Zero, Time.FromSeconds(1), null, 100);
        Assert.True(frame.TryRetain());
        Assert.True(frame.TryRetain());

        frame.Dispose();
        frame.Dispose();
        frame.Dispose();

        Assert.False(frame.TryRetain());
    }

    [Fact]
    public void Dispose_WhenLastReferenceDisposed_DisposesPayload()
    {
        var payload = new TestPayload();
        var frame = new DecodedFrame(0, Time.Zero, Time.FromSeconds(1), payload, 100);

        frame.Dispose();

        Assert.True(payload.IsDisposed);
        Assert.Equal(1, payload.DisposeCount);
    }

    [Fact]
    public void Dispose_WhenReferencesRemain_DoesNotDisposePayload()
    {
        var payload = new TestPayload();
        var frame = new DecodedFrame(0, Time.Zero, Time.FromSeconds(1), payload, 100);
        Assert.True(frame.TryRetain());

        frame.Dispose();

        Assert.False(payload.IsDisposed);
        Assert.Equal(0, payload.DisposeCount);

        frame.Dispose();
        Assert.True(payload.IsDisposed);
    }

    [Fact]
    public void Dispose_WhenAlreadyReleased_DoesNotDisposePayloadAgain()
    {
        var payload = new TestPayload();
        var frame = new DecodedFrame(0, Time.Zero, Time.FromSeconds(1), payload, 100);

        frame.Dispose();
        Assert.Equal(1, payload.DisposeCount);

        frame.Dispose();
        Assert.Equal(1, payload.DisposeCount);
    }

    [Fact]
    public void Dispose_WhenPayloadIsNull_DoesNotThrow()
    {
        var frame = new DecodedFrame(0, Time.Zero, Time.FromSeconds(1), null, 100);

        var exception = Record.Exception(() => frame.Dispose());

        Assert.Null(exception);
    }

    private sealed class TestPayload : IDisposable
    {
        public int DisposeCount { get; private set; }

        public bool IsDisposed => DisposeCount > 0;

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
