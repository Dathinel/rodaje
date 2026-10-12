using System.Numerics;
using Rodaje.Core.Effects;

namespace Rodaje.Core.Tests.Effects;

/// <summary>Recorte con proporción (<see cref="CropGeometry"/>) y curvas monótonas (<see cref="ToneCurve"/>).</summary>
public sealed class ImageGeometryTests
{
    private const double W = 1920;
    private const double H = 1080;

    private static double Ratio(CropRect c) => c.Width * W / (c.Height * H);

    [Theory]
    [InlineData("16:9", 16.0 / 9)]
    [InlineData("9:16", 9.0 / 16)]
    [InlineData("1:1", 1.0)]
    [InlineData("4:5", 0.8)]
    [InlineData("4:3", 4.0 / 3)]
    public void Fitting_an_aspect_gives_the_largest_centered_crop_with_it(string aspect, double expected)
    {
        double ratio = CropGeometry.Ratio(aspect)!.Value;
        Assert.Equal(expected, ratio, 9);
        CropRect crop = CropGeometry.FitAspect(CropRect.None, ratio, W, H);

        Assert.Equal(expected, Ratio(crop), 3);
        Assert.True(Math.Abs(crop.Width - 1) < 1e-4 || Math.Abs(crop.Height - 1) < 1e-4, "Toca dos bordes opuestos.");
        Assert.Equal(crop.Left, crop.Right, 4);
        Assert.Equal(crop.Top, crop.Bottom, 4);
    }

    [Fact]
    public void Free_has_no_ratio() => Assert.Null(CropGeometry.Ratio("free"));

    [Theory]
    [InlineData(CropGrip.TopLeft, 0.1, 0.02)]
    [InlineData(CropGrip.BottomRight, -0.2, -0.3)]
    [InlineData(CropGrip.Right, -0.15, 0)]
    [InlineData(CropGrip.Top, 0, 0.2)]
    [InlineData(CropGrip.BottomLeft, -0.5, 0.5)]
    public void Dragging_with_an_aspect_keeps_it_and_stays_inside(CropGrip grip, double dx, double dy)
    {
        CropRect start = CropGeometry.FitAspect(CropRect.None, 1, W, H);
        CropRect moved = CropGeometry.Drag(start, grip, dx, dy, 1, W, H);

        Assert.Equal(1, Ratio(moved), 3);
        Assert.InRange(moved.Left, 0, 1);
        Assert.InRange(moved.Right, 0, 1);
        Assert.InRange(moved.Top, 0, 1);
        Assert.InRange(moved.Bottom, 0, 1);
        Assert.True(moved.Width >= CropGeometry.MinSize - 1e-9 && moved.Height >= CropGeometry.MinSize - 1e-9);
    }

    [Fact]
    public void A_corner_drag_anchors_the_opposite_corner()
    {
        var start = new CropRect(0.1, 0.1, 0.1, 0.1);
        CropRect moved = CropGeometry.Drag(start, CropGrip.TopLeft, 0.1, 0.05, null, W, H);
        Assert.Equal(new CropRect(0.2, 0.15, 0.1, 0.1), moved);
    }

    [Fact]
    public void Moving_the_body_stops_at_the_edges()
    {
        var start = new CropRect(0.1, 0.2, 0.3, 0.2);
        CropRect moved = CropGeometry.Drag(start, CropGrip.Body, -0.5, 0.1, null, W, H);
        Assert.Equal(0, moved.Left, 6);
        Assert.Equal(0.4, moved.Right, 6);
        Assert.Equal(0.3, moved.Top, 6);
        Assert.Equal(0.1, moved.Bottom, 6);
    }

    [Fact]
    public void Opposite_sides_that_cross_are_shrunk_together()
    {
        CropRect crop = CropGeometry.Sanitize(new CropRect(0.7, double.NaN, 0.7, -1));
        Assert.Equal(CropGeometry.MinSize, crop.Width, 9);
        Assert.Equal(crop.Left, crop.Right, 9);
        Assert.Equal(1, crop.Height);
    }

    [Fact]
    public void A_monotone_curve_never_goes_down_nor_overshoots_its_points()
    {
        var random = new Random(7);
        for (int trial = 0; trial < 200; trial++)
        {
            int n = random.Next(2, 9);
            var points = new List<Vector2>();
            float y = 0;
            for (int i = 0; i < n; i++)
            {
                y = Math.Min(1, y + ((float)random.NextDouble() * 0.4f * (random.Next(4) == 0 ? 0 : 1)));
                points.Add(new Vector2((float)i / (n - 1), y));
            }

            ToneCurve curve = ToneCurve.From(points);
            double previous = -1;
            for (int s = 0; s <= 1000; s++)
            {
                double v = curve.Evaluate(s / 1000.0);
                Assert.True(v >= previous - 1e-12, $"Baja en x = {s / 1000.0} (prueba {trial}).");
                previous = v;
            }

            for (int k = 0; k + 1 < curve.Points.Count; k++)
            {
                Vector2 a = curve.Points[k], b = curve.Points[k + 1];
                Assert.Equal(a.Y, curve.Evaluate(a.X), 5);
                for (int s = 1; s < 20; s++)
                {
                    Assert.InRange(curve.Evaluate(a.X + ((b.X - a.X) * s / 20)), a.Y - 1e-9, b.Y + 1e-9);
                }
            }
        }
    }

    [Fact]
    public void Hue_curves_are_neutral_without_points_and_wrap_around()
    {
        Assert.Equal(0.5, HueCurve.Parse("", periodic: true).Evaluate(0.3));
        Assert.True(HueCurve.Parse("0.2,0.5;0.7,0.5", periodic: true).IsNeutral);
        Assert.Equal("", HueCurve.Parse("0.2,0.5;0.7,0.5", periodic: true).Format());

        HueCurve hue = HueCurve.Parse("0.9,0.8;0.1,0.2", periodic: true);
        Assert.Equal(hue.Evaluate(0.02), hue.Evaluate(1.02), 9);
        Assert.Equal(0.8, hue.Evaluate(0.9), 5);
        Assert.Equal(0.2, hue.Evaluate(0.1), 5);
        // Entre 0,9 y 1,1 pasa por el corte del tono sin saltar.
        Assert.InRange(hue.Evaluate(0.0), 0.2, 0.8);
        Assert.Equal(hue.Evaluate(0.999999), hue.Evaluate(0.0), 3);

        HueCurve luma = HueCurve.Parse("0.2,0.3;0.8,0.7", periodic: false);
        Assert.Equal(0.3, luma.Evaluate(0.05), 6);
        Assert.Equal(0.7, luma.Evaluate(0.95), 6);
    }

    [Fact]
    public void A_curves_key_with_only_neutral_hue_curves_is_the_identity()
    {
        Assert.True(ToneCurve.IsIdentityKey("||||0.4,0.5||"));
        Assert.False(ToneCurve.IsIdentityKey("|||||0.4,0.2|"));
        Assert.True(ToneCurve.HasHueCurves("||||0.5,0.6||"));
        float[] row = ToneCurve.HueTable("||||||");
        Assert.All(row, v => Assert.Equal(0.5f, v));
    }

    [Fact]
    public void Curves_round_trip_and_identity_is_empty()
    {
        ToneCurve curve = ToneCurve.Parse("0.75,0.8;0,0.1;0.25,0.2;1,1");
        Assert.Equal("0,0.1;0.25,0.2;0.75,0.8;1,1", curve.Format());
        Assert.Equal("", ToneCurve.Parse("0,0;1,1").Format());
        Assert.True(ToneCurve.Parse("no,es").IsIdentity);
        Assert.Equal(0.1, curve.Evaluate(-1), 6);
        Assert.Equal(0.5, ToneCurve.Identity.Evaluate(0.5), 9);
        Assert.True(ToneCurve.IsIdentityKey("|0,0;1,1||"));
    }

    [Fact]
    public void The_table_applies_the_channel_then_the_master()
    {
        float[] table = ToneCurve.Table("0,0;1,0.5|0,1;1,0||");
        // Rojo invertido y después la general a la mitad: entrada 0 → 1 → 0,5.
        Assert.Equal(0.5f, table[0], 4);
        Assert.Equal(0f, table[1], 4);
        Assert.Equal(0.5f, table[((ToneCurve.TableSize - 1) * 3) + 1], 4);
    }
}
