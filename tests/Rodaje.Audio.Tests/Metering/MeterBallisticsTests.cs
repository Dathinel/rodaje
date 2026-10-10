using Rodaje.Audio.Metering;

namespace Rodaje.Audio.Tests.Metering;

public class MeterBallisticsTests
{
    private const double Frame = 1.0 / 60;

    [Fact]
    public void SubeAlInstanteYBajaVeinteDecibeliosPorSegundo()
    {
        var meter = new MeterBallistics();

        meter.Update(-6, Frame);
        Assert.Equal(-6, meter.Level);

        Run(meter, double.NegativeInfinity, 0.5);
        Assert.Equal(-16, meter.Level, 6);

        // Un pico más bajo que la barra no la mueve; uno más alto, sí.
        meter.Update(-20, Frame);
        Assert.Equal(-16 - (20 * Frame), meter.Level, 6);
        meter.Update(-3, Frame);
        Assert.Equal(-3, meter.Level);
    }

    [Fact]
    public void LaCaidaSeConfiguraYLaBarraSeVaciaBajoElSuelo()
    {
        var meter = new MeterBallistics { DecayDecibelsPerSecond = 40 };

        meter.Update(-10, 0);
        Run(meter, double.NegativeInfinity, 0.25);
        Assert.Equal(-20, meter.Level, 6);

        Run(meter, double.NegativeInfinity, 2);
        Assert.Equal(double.NegativeInfinity, meter.Level);
        Assert.Throws<ArgumentOutOfRangeException>(() => meter.DecayDecibelsPerSecond = 0);
    }

    [Fact]
    public void ElPicoSeRetieneUnSegundoYMedioYLuegoCae()
    {
        var meter = new MeterBallistics();

        meter.Update(-4, 0);
        Run(meter, double.NegativeInfinity, 1.4);
        Assert.Equal(-4, meter.PeakHold);
        Assert.Equal(-4 - (20 * 1.4), meter.Level, 6);

        Run(meter, double.NegativeInfinity, 0.6);
        Assert.Equal(-4 - (20 * 0.5), meter.PeakHold, 6);

        // Un pico nuevo más alto reinicia la retención.
        meter.Update(-2, Frame);
        Run(meter, double.NegativeInfinity, 1.0);
        Assert.Equal(-2, meter.PeakHold);
    }

    [Fact]
    public void ElPicoRetenidoNuncaQuedaDebajoDeLaBarra()
    {
        var meter = new MeterBallistics { HoldSeconds = 0, DecayDecibelsPerSecond = 60 };

        for (int i = 0; i < 120; i++)
        {
            meter.Update(i % 30 == 0 ? -1 : -40, Frame);
            Assert.True(meter.PeakHold >= meter.Level, $"cuadro {i}: retenido {meter.PeakHold} bajo la barra {meter.Level}");
        }
    }

    [Fact]
    public void LaSaturacionQuedaEncendidaHastaBorrarla()
    {
        var meter = new MeterBallistics();

        meter.Update(-0.1, Frame, clipped: true);
        Run(meter, double.NegativeInfinity, 5);
        Assert.True(meter.Clipped);
        Assert.Equal(-0.1, meter.MaxPeak);
        Assert.True(meter.IsIdle);

        meter.ResetClip();
        Assert.False(meter.Clipped);
        Assert.Equal(double.NegativeInfinity, meter.MaxPeak);

        meter.Update(-12, Frame);
        Assert.Equal(-12, meter.MaxPeak);
        meter.Clear();
        Assert.Equal(double.NegativeInfinity, meter.PeakHold);
    }

    private static void Run(MeterBallistics meter, double input, double seconds)
    {
        int frames = (int)Math.Round(seconds / Frame);
        for (int i = 0; i < frames; i++)
        {
            meter.Update(input, seconds / frames);
        }
    }
}
