using System.Globalization;
using Rodaje.App.Services.Format;

namespace Rodaje.App.Tests.Format;

/// <summary>
/// Los porcentajes con <c>P</c> en el formato común: número, espacio y signo («35 %»), con coma decimal, aunque la cultura
/// del hilo sea la invariante, como en la app publicada con <c>InvariantGlobalization</c>.
/// </summary>
public class SpanishPercentTests
{
    [Fact]
    public void LosPorcentajesLlevanEspacioYComa()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("35 %", 0.35.ToString("P0", SpanishNumbers.Format));
            Assert.Equal("-35 %", (-0.35).ToString("P0", SpanishNumbers.Format));
            Assert.Equal("12,5 %", 0.125.ToString("P1", SpanishNumbers.Format));
            Assert.Equal("1.234 %", 12.34.ToString("P0", SpanishNumbers.Format));
            Assert.Equal("(35 % menos)", string.Create(SpanishNumbers.Format, $"({0.35:P0} menos)"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void LosPatronesDePorcentajeEstanFijados()
    {
        Assert.Equal(0, SpanishNumbers.Format.PercentPositivePattern);
        Assert.Equal(0, SpanishNumbers.Format.PercentNegativePattern);
        Assert.Equal(",", SpanishNumbers.Format.PercentDecimalSeparator);
        Assert.Equal(".", SpanishNumbers.Format.PercentGroupSeparator);
    }
}
