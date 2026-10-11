using Rodaje.App.Services.Inspector;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Inspector;

public class SpeedMathTests
{
    [Theory]
    [InlineData(0.1, 0)]
    [InlineData(1, 1000.0 / 3)]
    [InlineData(10, 2000.0 / 3)]
    [InlineData(100, 1000)]
    [InlineData(0.01, 0)]
    [InlineData(500, 1000)]
    public void Deslizador_LogaritmicoEnTresDecadas(double rate, double position) =>
        Assert.Equal(position, SpeedMath.ToSlider(rate), 6);

    [Fact]
    public void Deslizador_RedondeaYSeQuedaEnLosImanes()
    {
        Assert.Equal(new Rational(1, 10), SpeedMath.FromSlider(0));
        Assert.Equal(new Rational(100, 1), SpeedMath.FromSlider(1000));
        Assert.Equal(new Rational(1, 1), SpeedMath.FromSlider(SpeedMath.ToSlider(1.03)));
        Assert.Equal(new Rational(2, 1), SpeedMath.FromSlider(SpeedMath.ToSlider(1.95)));
        Assert.Equal(new Rational(137, 100), SpeedMath.FromSlider(SpeedMath.ToSlider(1.37)));
        Assert.Equal(new Rational(137, 10), SpeedMath.FromSlider(SpeedMath.ToSlider(13.71)));
        foreach (Rational detent in SpeedTabValues.Detents)
        {
            Assert.Equal(detent, SpeedMath.FromSlider(SpeedMath.ToSlider(detent.ToDouble())));
        }

        for (int position = 0; position <= 1000; position++)
        {
            Assert.True(RetimeMath.IsValidRate(SpeedMath.FromSlider(position)));
        }
    }

    [Fact]
    public void Duracion_DaLaVelocidadExacta_OAcotada()
    {
        Time source = Time.FromSeconds(5);
        Rational rate = SpeedMath.RateForDuration(source, Time.FromSeconds(3.3));
        Assert.Equal(new Rational(50, 33), rate);
        Assert.Equal(new Rational(1, 10), SpeedMath.RateForDuration(source, Time.FromSeconds(60)));
        Assert.Equal(new Rational(100, 1), SpeedMath.RateForDuration(source, Time.FromSeconds(0.01)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedMath.RateForDuration(source, Time.Zero));

        // Un tramo con ticks sueltos da una fracción que el núcleo admite y que no se aparta ni un tick en 10 s.
        Time odd = Time.FromTicks(Time.FromSeconds(7).Ticks + 12_345);
        Time target = Time.FromTicks(Time.FromSeconds(10).Ticks - 777);
        Rational approx = SpeedMath.RateForDuration(odd, target);
        Assert.True(RetimeMath.IsValidRate(approx));
        double duration = odd.Ticks / approx.ToDouble();
        Assert.True(Math.Abs(duration - target.Ticks) < Time.FromSeconds(0.001).Ticks, $"{duration} frente a {target.Ticks}");
    }

    [Fact]
    public void Aproximacion_ConvergenteOSemiconvergente()
    {
        Assert.Equal(new Rational(2, 3), SpeedMath.Approximate(2, 3, 100));
        Assert.Equal(new Rational(355, 113), SpeedMath.Approximate(3_141_592_653_589_793, 1_000_000_000_000_000, 400));
        Assert.Equal(new Rational(1000, 1), SpeedMath.Approximate(1_000_001, 1, 1000));
        Assert.Equal(new Rational(1, 2), SpeedMath.Approximate(1, 3, 2));
    }

    [Theory]
    [InlineData(1.5, "1,5×")]
    [InlineData(0.25, "0,25×")]
    [InlineData(2, "2,0×")]
    [InlineData(1.515151, "1,52×")]
    [InlineData(13.7, "13,7×")]
    [InlineData(100, "100×")]
    public void Texto_DeLaVelocidad(double rate, string text) => Assert.Equal(text, SpeedMath.FormatRate(rate));

    [Theory]
    [InlineData(3.3, "3,3 s")]
    [InlineData(5, "5,0 s")]
    [InlineData(59.96, "1:00,0")]
    [InlineData(65.3, "1:05,3")]
    public void Texto_DeLaDuracion(double seconds, string text) => Assert.Equal(text, SpeedMath.FormatDuration(Time.FromSeconds(seconds)));

    [Theory]
    [InlineData("1,5×", 3, 2)]
    [InlineData("1.5", 3, 2)]
    [InlineData(" x2 ", 2, 1)]
    [InlineData("150 %", 3, 2)]
    [InlineData("1000", 100, 1)]
    [InlineData("0,01", 1, 10)]
    [InlineData("1,37x", 137, 100)]
    public void Lee_LaVelocidad(string text, long num, long den) => Assert.Equal(new Rational(num, den), SpeedMath.ParseRate(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-2")]
    [InlineData("0")]
    [InlineData(null)]
    public void Lee_LaVelocidad_Invalida(string? text) => Assert.Null(SpeedMath.ParseRate(text));

    [Theory]
    [InlineData("3,3 s", 3.3)]
    [InlineData("3.3s", 3.3)]
    [InlineData("4", 4)]
    [InlineData("1:05,3", 65.3)]
    [InlineData("0:01:05", 65)]
    public void Lee_LaDuracion(string text, double seconds) => Assert.Equal(Time.FromSeconds(seconds), SpeedMath.ParseDuration(text));

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("1:2:3:4")]
    [InlineData("1,5:30")]
    [InlineData("tres")]
    [InlineData(null)]
    public void Lee_LaDuracion_Invalida(string? text) => Assert.Null(SpeedMath.ParseDuration(text));

    [Theory]
    [InlineData(8, true, null)]
    [InlineData(8.1, true, "Por encima de 8×")]
    [InlineData(100, true, "Por encima de 8×")]
    [InlineData(0.25, true, null)]
    [InlineData(0.24, true, "Por debajo de 0,25×")]
    [InlineData(0.1, true, "Por debajo de 0,25×")]
    [InlineData(1, true, null)]
    [InlineData(50, false, null)]
    [InlineData(0.1, false, null)]
    public void Aviso_DeLosLimitesDelSonido(double rate, bool hasAudio, string? start)
    {
        string? warning = SpeedTabValues.AudioWarning(rate, hasAudio);
        if (start is null)
        {
            Assert.Null(warning);
        }
        else
        {
            Assert.StartsWith(start, warning);
            Assert.Contains("Sigue sonando", warning, StringComparison.Ordinal);
        }
    }
}
