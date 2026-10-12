using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Effects;

/// <summary>Vértices, aplanado, keyframes de forma, coberturas y combinación de las máscaras, contra referencias exactas.</summary>
public sealed class MaskPathTests
{
    private static readonly MaskVertex[] Blob =
    [
        new(new Vector2(-0.4f, -0.3f), new Vector2(-0.05f, 0.15f), new Vector2(0.05f, -0.15f)),
        new(new Vector2(0.1f, -0.45f), new Vector2(-0.2f, 0), new Vector2(0.2f, 0), 2.5f),
        new(new Vector2(0.45f, 0.2f), Vector2.Zero, Vector2.Zero),
        new(new Vector2(-0.1f, 0.4f), new Vector2(0.15f, 0.05f), new Vector2(-0.15f, -0.05f), 0.5f),
    ];

    [Fact]
    public void Vertices_round_trip_with_handles_and_weights_and_corners_stay_short()
    {
        string text = MaskPath.Format(Blob);
        MaskVertex[] read = MaskPath.Parse(text);

        Assert.Equal(Blob.Length, read.Length);
        for (int i = 0; i < Blob.Length; i++)
        {
            Assert.Equal(Blob[i].Point, read[i].Point);
            Assert.Equal(Blob[i].In, read[i].In);
            Assert.Equal(Blob[i].Out, read[i].Out);
            Assert.Equal(Blob[i].Weight, read[i].Weight);
        }

        Assert.Contains(";0.45,0.2;", text, StringComparison.Ordinal);
        Assert.Empty(MaskPath.Parse("0,0"));
        Assert.Empty(MaskPath.Parse("0,0;1,1,2"));
        Assert.Empty(MaskPath.Parse("0,0;1,1;x,2"));
        Assert.Equal(2, MaskPath.Parse("0,0;1,1").Length);
    }

    [Theory]
    [InlineData(200, 200)]
    [InlineData(1920, 400)]
    [InlineData(40, 30)]
    public void Flattening_stays_within_the_tolerance_of_the_exact_curve(float width, float height)
    {
        Span<Vector3> polyline = new Vector3[MaskPath.MaxPolyline];
        int count = MaskPath.Flatten(Blob, closed: true, width, height, polyline);
        Assert.InRange(count, Blob.Length, MaskPath.MaxPolyline);
        var scale = new Vector2(width, height);

        // Cada punto de la curva exacta queda a menos de la tolerancia (más un margen de redondeo) de la polilínea.
        double worst = 0;
        for (int s = 0; s < Blob.Length; s++)
        {
            MaskVertex a = Blob[s], b = Blob[(s + 1) % Blob.Length];
            for (int k = 0; k <= 200; k++)
            {
                Vector2 p = MaskPath.Cubic(a.Point, a.Point + a.Out, b.Point + b.In, b.Point, k / 200f) * scale;
                worst = Math.Max(worst, DistanceToPolyline(p, polyline[..count], scale));
            }
        }

        Assert.InRange(worst, 0, MaskPath.FlattenTolerance + 0.01);
    }

    [Fact]
    public void The_four_vertex_ellipse_is_a_circle_within_three_ten_thousandths()
    {
        MaskVertex[] ellipse = MaskPath.Ellipse();
        for (int s = 0; s < 4; s++)
        {
            MaskVertex a = ellipse[s], b = ellipse[(s + 1) % 4];
            for (int k = 0; k <= 50; k++)
            {
                float r = MaskPath.Cubic(a.Point, a.Point + a.Out, b.Point + b.In, b.Point, k / 50f).Length();
                Assert.InRange(r, 0.5f * (1 - 0.0003f), 0.5f * (1 + 0.0003f));
            }
        }
    }

    [Fact]
    public void Splitting_a_segment_keeps_the_curve_and_toggling_makes_a_smooth_point()
    {
        MaskVertex[] split = MaskPath.Split(Blob, 0, 0.3f, closed: true);
        Assert.Equal(Blob.Length + 1, split.Length);
        MaskVertex a = Blob[0], b = Blob[1];
        for (int k = 0; k <= 20; k++)
        {
            float t = k / 20f;
            Vector2 original = MaskPath.Cubic(a.Point, a.Point + a.Out, b.Point + b.In, b.Point, t);
            Vector2 now = t <= 0.3f
                ? MaskPath.Cubic(split[0].Point, split[0].Point + split[0].Out, split[1].Point + split[1].In, split[1].Point, t / 0.3f)
                : MaskPath.Cubic(split[1].Point, split[1].Point + split[1].Out, split[2].Point + split[2].In, split[2].Point, (t - 0.3f) / 0.7f);
            Assert.True(Vector2.Distance(original, now) < 1e-4f, $"t = {t}");
        }

        MaskVertex smooth = MaskPath.Toggle(Blob, 2, closed: true);
        Assert.False(smooth.IsCorner);
        Assert.True(Vector2.Dot(Vector2.Normalize(smooth.In), Vector2.Normalize(smooth.Out)) < -0.999f);
        Assert.True(MaskPath.Toggle([.. Blob[..2], smooth, Blob[3]], 2, closed: true).IsCorner);
    }

    [Fact]
    public void Nearest_finds_the_segment_and_parameter_under_the_pointer()
    {
        MaskVertex[] square = MaskPath.Rectangle();
        (int segment, float t, float distance) = MaskPath.Nearest(square, true, new Vector2(0.1f, -0.52f), new Vector2(100, 100));
        Assert.Equal(0, segment);
        // Sin manejadores el tramo es recto pero su parámetro no es uniforme: el punto del parámetro cae bajo el puntero.
        MaskVertex a = square[0], b = square[1];
        Assert.Equal(0.1f, MaskPath.Cubic(a.Point, a.Point, b.Point, b.Point, t).X, 3);
        Assert.Equal(2f, distance, 2);
    }

    [Fact]
    public void Stars_and_polygons_fit_the_box()
    {
        Assert.Equal(10, MaskPath.Star(5).Length);
        Assert.Equal(6, MaskPath.Polygon(6).Length);
        Assert.All(MaskPath.Star(5, 0.4), v => Assert.InRange(v.Point.Length(), 0.199f, 0.5001f));
        Assert.Equal(-0.5f, MaskPath.Polygon(3)[0].Point.Y, 5);
    }

    [Fact]
    public void Shape_keys_interpolate_hold_on_count_changes_and_round_trip()
    {
        MaskVertex[] a = MaskPath.Rectangle();
        MaskVertex[] b = [.. a.Select(v => v with { Point = v.Point * 2 })];
        MaskVertex[] c = MaskPath.Star(5);
        string text = MaskShapeTrack.Format([(Time.FromSeconds(1), a), (Time.FromSeconds(3), b), (Time.FromSeconds(5), c)]);
        MaskShapeTrack track = MaskShapeTrack.Parse(text)!;

        Assert.Equal(3, track.Count);
        Assert.Equal(text, track.ToString());
        Assert.Equal(a[1].Point, track.Evaluate(Time.Zero)[1].Point);
        Assert.Equal(a[1].Point * 1.5f, track.Evaluate(Time.FromSeconds(2))[1].Point);
        Assert.Equal(4, track.Evaluate(Time.FromSeconds(4)).Length);
        Assert.Equal(10, track.Evaluate(Time.FromSeconds(6)).Length);
        Assert.Null(MaskShapeTrack.Parse("5@0,0;1,1|3@0,0;1,1"));
        Assert.Equal(1, MaskShapeTrack.Parse("0,0;1,0;1,1")!.Count);

        MaskShapeTrack moved = track.With(Time.FromSeconds(3), c).Without(Time.FromSeconds(5))!;
        Assert.Equal(2, moved.Count);
        Assert.Equal(10, moved.ShapeAt(1).Count);
    }

    [Fact]
    public void Rounded_rectangles_cut_their_corners_on_a_circle()
    {
        // Caja de 100 × 60 px con radio de 0,5 · 30 = 15 px: la esquina de afuera queda fuera; el centro del arco, adentro.
        var spec = new MaskSpec(MaskShape.Rectangle, 0.5, 0.5, 0.5, 0.6, 0, 0, 1, false) { Roundness = 0.5 };
        (double corner, _) = MaskGeometry.SignedDistance(spec, 50, 20, 200, 100);
        Assert.Equal(15 * (Math.Sqrt(2) - 1), corner, 6);
        (double arc, _) = MaskGeometry.SignedDistance(spec, 65, 35, 200, 100);
        Assert.Equal(-15, arc, 6);
        (double side, _) = MaskGeometry.SignedDistance(spec, 100, 15, 200, 100);
        Assert.Equal(5, side, 6);
    }

    [Fact]
    public void Expansion_moves_the_half_way_line_and_feather_is_per_vertex_on_a_bezier()
    {
        var rect = new MaskSpec(MaskShape.Rectangle, 0.5, 0.5, 0.5, 0.5, 0, 0.1, 1, false);
        Assert.Equal(0.5, MaskGeometry.Factor(rect with { Expansion = 0.05 }, 155, 50, 200, 100), 6);

        // Cuadrado con peso 3 arriba y 1 abajo: el borde suave de arriba mide el triple.
        MaskVertex[] square =
        [
            new(new Vector2(-0.5f, -0.5f), Vector2.Zero, Vector2.Zero, 3), new(new Vector2(0.5f, -0.5f), Vector2.Zero, Vector2.Zero, 3),
            new(new Vector2(0.5f, 0.5f), Vector2.Zero, Vector2.Zero, 1), new(new Vector2(-0.5f, 0.5f), Vector2.Zero, Vector2.Zero, 1),
        ];
        var bezier = new MaskSpec(MaskShape.Bezier, 0.5, 0.5, 0.5, 0.8, 0, 0.05, 1, false);
        Vector3[] polyline = MaskGeometry.Polyline(MaskShape.Bezier, square, 100, 80);
        double Top(double py) => MaskGeometry.Factor(bezier, 100, py, 200, 100, polyline);
        double Bottom(double py) => MaskGeometry.Factor(bezier, 100, py, 200, 100, polyline);
        // Ancho 5 px abajo y 15 px arriba: a 3 px del contorno hacia adentro, 0,5 + 3/5 (lleno) contra 0,5 + 3/15.
        Assert.Equal(0.7, Top(13), 6);
        Assert.Equal(1, Bottom(87), 6);
        Assert.Equal(0.5, Top(10), 6);
    }

    [Fact]
    public void A_stroke_is_a_thick_line_with_width_per_vertex()
    {
        MaskVertex[] line = [new(new Vector2(-0.5f, 0), Vector2.Zero, Vector2.Zero, 1), new(new Vector2(0.5f, 0), Vector2.Zero, Vector2.Zero, 3)];
        var stroke = new MaskSpec(MaskShape.Stroke, 0.5, 0.5, 0.5, 0.5, 0, 0, 1, false) { Thickness = 0.1 };
        Vector3[] polyline = MaskGeometry.Polyline(MaskShape.Stroke, line, 100, 50);
        Assert.Equal(2, polyline.Length);
        // Grosor 10 px a la izquierda (medio, 5) y 30 a la derecha (medio, 15); en el centro, 20 (medio, 10).
        Assert.Equal(1, MaskGeometry.Factor(stroke, 100, 50 + 9.4, 200, 100, polyline), 6);
        Assert.Equal(0, MaskGeometry.Factor(stroke, 100, 50 + 10.6, 200, 100, polyline), 6);
        Assert.Equal(0.5, MaskGeometry.Factor(stroke, 52, 50 + 5.2, 200, 100, polyline), 1);
        Assert.Equal(0, MaskGeometry.Factor(stroke, 30, 50, 200, 100, polyline), 6);
    }

    [Theory]
    [InlineData(MaskMode.Add, 0.6, 0.5, 0.8)]
    [InlineData(MaskMode.Subtract, 0.6, 0.5, 0.3)]
    [InlineData(MaskMode.Intersect, 0.6, 0.5, 0.3)]
    [InlineData(MaskMode.Difference, 0.6, 0.5, 0.5)]
    public void Modes_combine_with_porter_duff_on_coverage(MaskMode second, double a, double b, double expected)
    {
        // La primera suma (arranca en 0): queda a; la segunda se combina con su modo.
        Assert.Equal(expected, MaskGeometry.Combine([(MaskMode.Add, a), (second, b)]), 9);
    }

    [Fact]
    public void The_first_mode_decides_the_start()
    {
        Assert.Equal(0.4, MaskGeometry.Combine([(MaskMode.Subtract, 0.6)]), 9);
        Assert.Equal(0.6, MaskGeometry.Combine([(MaskMode.Intersect, 0.6)]), 9);
        Assert.Equal(0.6, MaskGeometry.Combine([(MaskMode.Difference, 0.6)]), 9);
        Assert.Equal(1, MaskGeometry.Combine([]), 9);
    }

    [Fact]
    public void A_traced_disc_becomes_a_smooth_bezier_of_the_same_size()
    {
        const int w = 160, h = 120;
        byte[] gray = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double d = Math.Sqrt(((x + 0.5 - 70) * (x + 0.5 - 70)) + ((y + 0.5 - 60) * (y + 0.5 - 60)));
                gray[(y * w) + x] = d <= 40 ? (byte)255 : (byte)0;
                // Una mancha chica aparte: no es la mayor.
                if (x > 140 && y < 10)
                {
                    gray[(y * w) + x] = 255;
                }
            }
        }

        MaskVertex[] vertices = MaskContour.Trace(gray, w, h);
        Assert.InRange(vertices.Length, 8, 64);
        Assert.All(vertices, v => Assert.False(v.IsCorner));
        foreach (MaskVertex v in vertices)
        {
            double r = Vector2.Distance(v.Point * new Vector2(w, h), new Vector2(70, 60));
            Assert.InRange(r, 38.5, 41.5);
        }
    }

    [Fact]
    public void An_l_shape_keeps_its_inner_corner()
    {
        const int w = 64, h = 64;
        byte[] gray = new byte[w * h];
        for (int y = 8; y < 56; y++)
        {
            for (int x = 8; x < 56; x++)
            {
                gray[(y * w) + x] = x < 24 || y >= 40 ? (byte)200 : (byte)0;
            }
        }

        MaskVertex[] vertices = MaskContour.Trace(gray, w, h);
        Vector2[] pixels = [.. vertices.Select(v => (v.Point * new Vector2(w, h)) - new Vector2(0.5f))];
        Assert.Contains(pixels, p => Vector2.Distance(p, new Vector2(23, 40)) < 1.6f);
        Assert.Contains(pixels, p => Vector2.Distance(p, new Vector2(55, 55)) < 1.6f);
        Assert.Empty(MaskContour.Trace(new byte[w * h], w, h));
    }

    private static double DistanceToPolyline(Vector2 p, ReadOnlySpan<Vector3> polyline, Vector2 scale)
    {
        double best = double.MaxValue;
        for (int i = 0; i < polyline.Length; i++)
        {
            Vector2 a = new Vector2(polyline[i].X, polyline[i].Y) * scale;
            Vector2 b = new Vector2(polyline[(i + 1) % polyline.Length].X, polyline[(i + 1) % polyline.Length].Y) * scale;
            Vector2 ab = b - a;
            float t = ab.LengthSquared() < 1e-12f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
            best = Math.Min(best, Vector2.Distance(p, a + (ab * t)));
        }

        return best;
    }
}
