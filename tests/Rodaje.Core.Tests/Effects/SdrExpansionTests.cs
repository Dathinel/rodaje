using Rodaje.Core.Effects;
using Xunit;

namespace Rodaje.Core.Tests.Effects;

/// <summary>«Expandir a HDR» (fase C, parte 3): la curva sube sin bajar nunca, el gris del 18 % no se mueve y el blanco llega al pico.</summary>
public sealed class SdrExpansionTests
{
    [Theory]
    [InlineData(203)]
    [InlineData(400)]
    [InlineData(1000)]
    public void The_curve_is_monotonic_keeps_the_18_percent_gray_and_takes_white_to_the_peak(double peak)
    {
        double previous = -1;
        for (int i = 0; i <= 4096; i++)
        {
            double y = i / 4096.0;
            double expanded = SdrExpansion.Expand(y, peak);
            Assert.True(expanded >= previous, $"La curva baja en {y}: {expanded} < {previous}.");
            Assert.True(expanded >= y - 1e-9, $"La curva oscurece {y} a {expanded}.");
            previous = expanded;
        }

        Assert.Equal(0, SdrExpansion.Expand(0, peak));
        Assert.InRange(SdrExpansion.Expand(SdrExpansion.Gray, peak), SdrExpansion.Gray * 0.99, SdrExpansion.Gray * 1.01);
        Assert.InRange(SdrExpansion.Expand(1, peak), (peak / 203) - 1e-6, (peak / 203) + 1e-6);
    }

    [Fact]
    public void The_normalized_bt2446_curve_is_continuous_at_its_knees()
    {
        for (int i = 1; i <= 4096; i++)
        {
            double a = SdrExpansion.Normalized((i - 1) / 4096.0, 1000);
            double b = SdrExpansion.Normalized(i / 4096.0, 1000);
            Assert.True(b >= a && b - a < 0.01, $"Salto de {a} a {b} en {i / 4096.0}.");
        }
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(100, 203)]
    [InlineData(600, 600)]
    [InlineData(5000, 1000)]
    [InlineData(double.NaN, 1000)]
    public void The_peak_defaults_to_1000_and_is_clamped(double requested, double expected) =>
        Assert.Equal(expected, SdrExpansion.PeakFor(requested));
}
