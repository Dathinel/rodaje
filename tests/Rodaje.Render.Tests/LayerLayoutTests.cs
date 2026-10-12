using System.Numerics;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Render.Tests;

public sealed class LayerLayoutTests
{
    private const float Precision = 1e-3f;

    [Fact]
    public void Identity_fills_the_sequence_with_a_same_aspect_source()
    {
        LayerPlacement p = LayerLayout.Place(1920, 1080, 3840, 2160, LayerTransform.Identity)!.Value;

        AssertPoint(new(0, 0), p.TopLeft);
        AssertPoint(new(1920, 0), p.TopRight);
        AssertPoint(new(0, 1080), p.BottomLeft);
        AssertPoint(new(1920, 1080), p.BottomRight);
        Assert.Equal(1f, p.Scale);
    }

    [Fact]
    public void A_4x3_source_is_pillarboxed_before_the_transform()
    {
        LayerPlacement p = LayerLayout.Place(1920, 1080, 640, 480, LayerTransform.Identity)!.Value;

        Assert.Equal(1440f, p.LocalWidth);
        Assert.Equal(1080f, p.LocalHeight);
        AssertPoint(new(240, 0), p.TopLeft);
        AssertPoint(new(1680, 1080), p.BottomRight);
    }

    [Fact]
    public void Scale_shrinks_around_the_center_and_translation_moves_the_center()
    {
        LayerPlacement p = LayerLayout.Place(1920, 1080, 1920, 1080, new LayerTransform(100, -50, 0.5, 0, 1))!.Value;

        AssertPoint(new(960 + 100, 540 - 50), p.Center);
        AssertPoint(new(480 + 100, 270 - 50), p.TopLeft);
        AssertPoint(new(1440 + 100, 810 - 50), p.BottomRight);
    }

    [Fact]
    public void A_quarter_turn_is_clockwise_on_screen()
    {
        LayerPlacement p = LayerLayout.Place(400, 400, 200, 100, LayerTransform.Identity with { RotationDegrees = 90 })!.Value;

        // 400×200 centrado en (200, 200): girado en sentido horario, la esquina superior izquierda pasa arriba a la derecha.
        AssertPoint(new(300, 0), p.TopLeft);
        AssertPoint(new(300, 400), p.TopRight);
        AssertPoint(new(100, 0), p.BottomLeft);
        AssertPoint(new(100, 400), p.BottomRight);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(-30)]
    [InlineData(720 + 10)]
    public void Rotation_keeps_the_center_and_the_side_lengths(double degrees)
    {
        LayerPlacement p = LayerLayout.Place(1920, 1080, 1920, 1080, new LayerTransform(10, 20, 0.25, degrees, 1))!.Value;

        AssertPoint(new(970, 560), p.Center);
        Assert.Equal(480, Vector2.Distance(p.TopLeft, p.TopRight), Precision);
        Assert.Equal(270, Vector2.Distance(p.TopLeft, p.BottomLeft), Precision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Nothing_is_placed_without_a_positive_finite_scale(double scale) =>
        Assert.Null(LayerLayout.Place(1920, 1080, 1920, 1080, LayerTransform.Identity with { Scale = scale }));

    [Fact]
    public void Nothing_is_placed_with_non_finite_offsets_or_an_empty_source()
    {
        Assert.Null(LayerLayout.Place(1920, 1080, 1920, 1080, LayerTransform.Identity with { X = double.NaN }));
        Assert.Null(LayerLayout.Place(1920, 1080, 1920, 1080, LayerTransform.Identity with { RotationDegrees = double.PositiveInfinity }));
        Assert.Null(LayerLayout.Place(1920, 1080, 0, 1080, LayerTransform.Identity));
    }

    [Fact]
    public void Texture_coordinates_extend_past_the_corners()
    {
        QuadTexCoords corners = VideoLayout.Corners(0, 0.5f, 0.75f);
        Assert.Equal(new TexCoord(0.25f, 0.375f), LayerLayout.TexCoordAt(corners, 50, 50, 100, 100));
        Assert.Equal(new TexCoord(-0.005f, 0.75f), LayerLayout.TexCoordAt(corners, -1, 100, 100, 100));

        QuadTexCoords rotated = VideoLayout.Corners(90, 1, 1);
        Assert.Equal(new TexCoord(0, 0), LayerLayout.TexCoordAt(rotated, 100, 0, 100, 100));
        Assert.Equal(new TexCoord(1, 1), LayerLayout.TexCoordAt(rotated, 0, 100, 100, 100));
    }

    [Fact]
    public void Sequence_pixels_map_to_normalized_device_coordinates()
    {
        Assert.Equal(new Vector2(-1, 1), LayerLayout.ToNdc(new(0, 0), 1920, 1080));
        Assert.Equal(new Vector2(1, -1), LayerLayout.ToNdc(new(1920, 1080), 1920, 1080));
        Assert.Equal(new Vector2(0, 0), LayerLayout.ToNdc(new(960, 540), 1920, 1080));
    }

    [Theory]
    [InlineData(720, 576, 16, 15, 0, 768, 576)]
    [InlineData(1920, 1080, 1, 1, 90, 1080, 1920)]
    [InlineData(1440, 1080, 4, 3, 270, 1080, 1920)]
    [InlineData(100, 100, 2, 1, 180, 200, 100)]
    public void Source_geometry_applies_the_pixel_aspect_and_then_the_rotation(int width, int height, int sarNum, int sarDen, int rotation,
        int displayWidth, int displayHeight) =>
        Assert.Equal((displayWidth, displayHeight), new SourceGeometry(new Rational(sarNum, sarDen), rotation).DisplaySize(width, height));

    [Fact]
    public void Default_geometry_is_square_pixels()
    {
        Assert.Equal((1920, 1080), default(SourceGeometry).DisplaySize(1920, 1080));
        Assert.Equal((1920, 1080), SourceGeometry.Square.DisplaySize(1920, 1080));
    }

    /// <summary>
    /// Estabilizar va antes de la transformación: cada punto de la imagen ajustada (centrado, en píxeles de la secuencia)
    /// se corrige con la similitud medida en altos de la imagen y después se transforma.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080, 0, 0, 1, 0)]
    [InlineData(1920, 1080, 100, -40, 1.5, 30)]
    [InlineData(640, 480, -25, 60, 0.75, -120)]
    public void Stabilization_is_composed_before_the_transform(int width, int height, double x, double y, double scale, double degrees)
    {
        var correction = new LayerStabilization(1.08 * Math.Cos(0.02), 1.08 * Math.Sin(0.02), 0.013, -0.021);
        var transform = new LayerTransform(x, y, scale, degrees, 1) { Stabilization = correction };
        LayerPlacement p = LayerLayout.Place(1920, 1080, width, height, transform)!.Value;
        LayerPlacement plain = LayerLayout.Place(1920, 1080, width, height, transform with { Stabilization = null })!.Value;

        double fitHeight = plain.LocalHeight;
        double radians = degrees * Math.PI / 180;
        foreach ((float lx, float ly) in new[] { (0f, 0f), (plain.LocalWidth, 0f), (plain.LocalWidth * 0.3f, plain.LocalHeight * 0.8f) })
        {
            double qx = lx - (plain.LocalWidth / 2.0);
            double qy = ly - (plain.LocalHeight / 2.0);
            double cx = (correction.A * qx) - (correction.B * qy) + (correction.X * fitHeight);
            double cy = (correction.B * qx) + (correction.A * qy) + (correction.Y * fitHeight);
            double ex = (scale * ((cx * Math.Cos(radians)) - (cy * Math.Sin(radians)))) + 960 + x;
            double ey = (scale * ((cx * Math.Sin(radians)) + (cy * Math.Cos(radians)))) + 540 + y;
            AssertPoint(new Vector2((float)ex, (float)ey), p.Map(lx, ly));
        }
    }

    private static void AssertPoint(Vector2 expected, Vector2 actual)
    {
        Assert.True(Vector2.Distance(expected, actual) < Precision, $"Se esperaba {expected} y se obtuvo {actual}.");
    }
}
