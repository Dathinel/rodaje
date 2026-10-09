using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;

namespace Rodaje.Engine.Tests;

public sealed class PresentationTests
{
    [Theory]
    [InlineData(1920, 1080, 1920, 1080, 0, 0, 1920, 1080)]
    [InlineData(1000, 1000, 1920, 1080, 0, 218, 1000, 563)]
    [InlineData(1920, 1080, 1080, 1920, 656, 0, 608, 1080)]
    [InlineData(800, 600, 720, 576, 25, 0, 750, 600)]
    public void Fit_letterboxes_and_pillarboxes(int tw, int th, int dw, int dh, int x, int y, int w, int h) =>
        Assert.Equal(new PixelRect(x, y, w, h), VideoLayout.Fit(tw, th, dw, dh));

    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(100, 0, 100)]
    [InlineData(100, 100, 0)]
    public void Fit_with_nothing_to_show_is_empty(int tw, int th, int dw) =>
        Assert.True(VideoLayout.Fit(tw, th, dw, 100).IsEmpty);

    [Fact]
    public void Corners_without_rotation_crop_the_decoder_padding()
    {
        QuadTexCoords q = VideoLayout.Corners(0, 0.5f, 0.9375f);
        Assert.Equal(new TexCoord(0, 0), q.TopLeft);
        Assert.Equal(new TexCoord(0.5f, 0), q.TopRight);
        Assert.Equal(new TexCoord(0, 0.9375f), q.BottomLeft);
        Assert.Equal(new TexCoord(0.5f, 0.9375f), q.BottomRight);
    }

    [Fact]
    public void Rotating_90_clockwise_brings_the_bottom_left_to_the_top_left()
    {
        QuadTexCoords q = VideoLayout.Corners(90, 1, 1);
        Assert.Equal(new TexCoord(0, 1), q.TopLeft);
        Assert.Equal(new TexCoord(0, 0), q.TopRight);
        Assert.Equal(new TexCoord(1, 1), q.BottomLeft);
        Assert.Equal(new TexCoord(1, 0), q.BottomRight);
    }

    [Fact]
    public void Rotations_compose_like_quarter_turns()
    {
        QuadTexCoords r180 = VideoLayout.Corners(180, 1, 1);
        Assert.Equal(new TexCoord(1, 1), r180.TopLeft);
        Assert.Equal(new TexCoord(0, 0), r180.BottomRight);

        QuadTexCoords r270 = VideoLayout.Corners(270, 1, 1);
        Assert.Equal(new TexCoord(1, 0), r270.TopLeft);
        Assert.Equal(new TexCoord(0, 1), r270.BottomRight);
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(359, 0)]
    [InlineData(180, 180)]
    public void Rotation_is_normalized_to_a_quarter_turn(int input, int expected) =>
        Assert.Equal(expected, VideoLayout.NormalizeRotation(input));

    [Theory]
    [InlineData(false, 16, 235, 128)]
    [InlineData(true, 64, 940, 512)]
    public void Limited_range_maps_black_white_and_neutral_chroma(bool tenBit, int black, int white, int neutral)
    {
        YuvRange range = YuvRange.For(tenBit);
        // Lo que devuelve el muestreo UNORM para un código dado (P010 lo guarda en los 10 bits altos).
        float Sample(int code) => tenBit ? code * 64 / 65535f : code / 255f;

        Assert.Equal(0, (Sample(black) * range.LumaScale) + range.LumaBias, 4);
        Assert.Equal(1, (Sample(white) * range.LumaScale) + range.LumaBias, 4);
        Assert.Equal(0, (Sample(neutral) * range.ChromaScale) + range.ChromaBias, 4);
    }

    [Theory]
    [InlineData(0L, "0:00.000")]
    [InlineData(1_500L, "0:01.500")]
    [InlineData(61_001L, "1:01.001")]
    [InlineData(3_723_456L, "1:02:03.456")]
    [InlineData(-2_000L, "-0:02.000")]
    public void Timecode_formats_minutes_and_hours(long milliseconds, string expected) =>
        Assert.Equal(expected, Timecode.Format(Time.FromTicks(milliseconds * (Time.TicksPerSecond / 1000))));
}
