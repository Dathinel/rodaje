using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Timing;

/// <summary>«Ir a tiempo»: lo que se escribe en el cuadro y a dónde salta el cursor.</summary>
public sealed class TimeInputTests
{
    private static readonly Rational Rate = Rational.Fps25;

    private static Time Parse(string text, double currentSeconds = 0, Rational? rate = null)
    {
        Assert.True(TimeInput.TryParse(text, rate ?? Rate, Time.FromSeconds(currentSeconds), out Time result), text);
        return result;
    }

    [Theory]
    [InlineData("1:23", 83)]
    [InlineData("1:23.5", 83.5)]
    [InlineData("1:23,5", 83.5)]
    [InlineData("0:01:23.5", 83.5)]
    [InlineData("1:02:03", 3723)]
    [InlineData("83", 83)]
    [InlineData("83.52", 83.52)]
    [InlineData("  83  ", 83)]
    [InlineData("2s", 2)]
    [InlineData("1.5m", 90)]
    [InlineData("1h", 3600)]
    [InlineData("0", 0)]
    public void TryParse_EntiendeTiemposAbsolutos(string text, double seconds)
    {
        Assert.Equal(Time.FromSeconds(seconds).SnapToFrame(Rate), Parse(text));
    }

    [Theory]
    [InlineData("120f", 120)]
    [InlineData("0f", 0)]
    [InlineData("25 f", 25)]
    public void TryParse_EntiendeFotogramasConF(string text, long frame)
    {
        Assert.Equal(Time.FromFrames(frame, Rate), Parse(text));
        Assert.Equal(Time.FromFrames(frame, Rational.Ntsc30), Parse(text, rate: Rational.Ntsc30));
    }

    [Theory]
    [InlineData("+10", 60, 70)]
    [InlineData("-2s", 60, 58)]
    [InlineData("+1:00", 60, 120)]
    [InlineData("- 5", 60, 55)]
    [InlineData("-2m", 60, 0)]
    public void TryParse_ConSignoSaltaDesdeElCursorYNuncaAntesDeCero(string text, double current, double expected)
    {
        Assert.Equal(Time.FromSeconds(expected).SnapToFrame(Rate), Parse(text, current));
    }

    [Fact]
    public void TryParse_ConSignoYFotogramasMueveEsosFotogramas()
    {
        Time current = Time.FromFrames(100, Rate);

        Assert.True(TimeInput.TryParse("+24f", Rate, current, out Time forward));
        Assert.True(TimeInput.TryParse("-30f", Rate, current, out Time back));

        Assert.Equal(Time.FromFrames(124, Rate), forward);
        Assert.Equal(Time.FromFrames(70, Rate), back);
    }

    [Fact]
    public void TryParse_DejaElResultadoAlInicioDelFotograma()
    {
        Time result = Parse("1.03", rate: Rational.Fps25);

        Assert.Equal(result, result.SnapToFrame(Rational.Fps25));
        Assert.Equal(Time.FromFrames(26, Rational.Fps25), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1:")]
    [InlineData(":30")]
    [InlineData("1:2:3:4")]
    [InlineData("1.5:00")]
    [InlineData("+")]
    [InlineData("--5")]
    [InlineData("5x")]
    [InlineData("1e5")]
    [InlineData("99999999999")]
    [InlineData("999999999h")]
    public void TryParse_RechazaLoQueNoEsUnTiempo(string text)
    {
        Assert.False(TimeInput.TryParse(text, Rate, Time.Zero, out Time result));
        Assert.Equal(Time.Zero, result);
    }

    [Fact]
    public void TryParse_RechazaUnaTasaInvalida()
    {
        Assert.False(TimeInput.TryParse("10", default, Time.Zero, out _));
    }
}
