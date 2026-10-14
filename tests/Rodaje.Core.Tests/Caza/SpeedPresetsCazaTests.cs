using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Caza;

public class SpeedPresetsCazaTests
{
    [Fact]
    public void Points_AreInTenThousandthsAndSpeedInHundredths()
    {
        foreach (SpeedPreset preset in SpeedPresets.All)
        {
            IReadOnlyList<SpeedPoint> points = SpeedPresets.Points(preset);
            Assert.NotEmpty(points);
            Assert.Equal(0, points[0].At);
            Assert.Equal(10_000, points[^1].At);

            for (int i = 0; i < points.Count; i++)
            {
                Assert.InRange(points[i].At, 0, 10_000);
                Assert.True(points[i].Hundredths > 0);

                if (i > 0)
                {
                    Assert.True(points[i].At > points[i - 1].At);
                }
            }
        }
    }

    [Fact]
    public void Montage_MatchesDocumentedShapeAndMeanRate()
    {
        Assert.Equal("Montaje", SpeedPresets.Name(SpeedPreset.Montage));

        IReadOnlyList<SpeedPoint> points = SpeedPresets.Points(SpeedPreset.Montage);
        Assert.Equal(
            [(0, 250), (2000, 250), (2800, 50), (3800, 50), (4600, 250), (6000, 250), (6800, 50), (7800, 50), (8600, 250), (10_000, 250)],
            points.Select(p => (p.At, p.Hundredths)));

        Assert.Equal(1.78, SpeedPresets.MeanRate(points), 0.01);
    }

    [Fact]
    public void Hero_MatchesDocumentedShapeAndMeanRate()
    {
        Assert.Equal("Héroe", SpeedPresets.Name(SpeedPreset.Hero));

        IReadOnlyList<SpeedPoint> points = SpeedPresets.Points(SpeedPreset.Hero);
        Assert.Equal(
            [(0, 100), (3500, 100), (4300, 20), (5700, 20), (6500, 100), (10_000, 100)],
            points.Select(p => (p.At, p.Hundredths)));

        Assert.Equal(0.82, SpeedPresets.MeanRate(points), 0.01);
    }

    [Fact]
    public void Bullet_MatchesDocumentedShapeAndMeanRate()
    {
        Assert.Equal("Bala", SpeedPresets.Name(SpeedPreset.Bullet));

        IReadOnlyList<SpeedPoint> points = SpeedPresets.Points(SpeedPreset.Bullet);
        Assert.Equal(
            [(0, 300), (2500, 300), (3300, 12), (6700, 12), (7500, 300), (10_000, 300)],
            points.Select(p => (p.At, p.Hundredths)));

        Assert.Equal(1.79, SpeedPresets.MeanRate(points), 0.01);
    }

    [Fact]
    public void FastIn_MatchesDocumentedShapeAndMeanRate()
    {
        Assert.Equal("Entrada rápida", SpeedPresets.Name(SpeedPreset.FastIn));

        IReadOnlyList<SpeedPoint> points = SpeedPresets.Points(SpeedPreset.FastIn);
        Assert.Equal(
            [(0, 400), (1500, 400), (4000, 100), (10_000, 100)],
            points.Select(p => (p.At, p.Hundredths)));

        Assert.Equal(1.83, SpeedPresets.MeanRate(points), 0.01);
    }

    [Fact]
    public void FastOut_MatchesDocumentedShapeAndMeanRate()
    {
        Assert.Equal("Salida rápida", SpeedPresets.Name(SpeedPreset.FastOut));

        IReadOnlyList<SpeedPoint> points = SpeedPresets.Points(SpeedPreset.FastOut);
        Assert.Equal(
            [(0, 100), (6000, 100), (8500, 400), (10_000, 400)],
            points.Select(p => (p.At, p.Hundredths)));

        Assert.Equal(1.83, SpeedPresets.MeanRate(points), 0.01);
    }
}
