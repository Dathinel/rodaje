using System.Globalization;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Format;

/// <summary>
/// El formato común de los números que ve el usuario: coma decimal aunque la cultura del hilo sea la invariante, como en la
/// app publicada con <c>InvariantGlobalization</c>.
/// </summary>
public class SpanishNumbersTests
{
    [Fact]
    public void DaComaDecimalYPuntoDeMiles() => WithInvariantCulture(() =>
    {
        Assert.Equal("0,5", 0.5.ToString("0.0", SpanishNumbers.Format));
        Assert.Equal("1.234,5", 1234.5.ToString("#,##0.0", SpanishNumbers.Format));
        Assert.Equal("-3 dB", string.Create(SpanishNumbers.Format, $"{-3.2:0} dB"));
        Assert.Equal("+12 %", string.Create(SpanishNumbers.Format, $"{0.12 * 100:+0;-0;0} %"));
        Assert.Equal("Tempo: 120,5 BPM", string.Create(SpanishNumbers.Format, $"Tempo: {120.5:0.0} BPM"));
        Assert.Equal("1,25×", ((FormattableString)$"{1.25:0.00}×").ToString(SpanishNumbers.Format));
    });

    [Fact]
    public void EsDeSoloLectura()
    {
        Assert.True(SpanishNumbers.Format.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => SpanishNumbers.Format.NumberDecimalSeparator = ".");
    }

    [Fact]
    public void VelocidadYTitulosUsanElFormatoComun() => WithInvariantCulture(() =>
    {
        Assert.Equal("1,5×", SpeedMath.FormatRate(1.5));
        Assert.Equal("0,25×", SpeedMath.FormatRate(0.25));
        Assert.Equal("1:05,3", SpeedMath.FormatDuration(Rodaje.Core.Timing.Time.FromSeconds(65.3)));
        Assert.Equal("Negrita", TitleEditor.WeightName(700));
        Assert.Equal("350", TitleEditor.WeightName(350));
    });

    [Fact]
    public void ElAvisoDeContrasteDeLosSubtitulosLlevaComa() => WithInvariantCulture(() =>
    {
        TextStyle grey = SubtitleStyles.Default();
        grey.Fill.Color = "#555555";
        string? warning = SubtitleStyles.Legibility(grey, 1920, 1080);
        Assert.NotNull(warning);
        Assert.Contains("hace falta 4,5:1", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("4.5", warning, StringComparison.Ordinal);
    });

    private static void WithInvariantCulture(Action action)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
