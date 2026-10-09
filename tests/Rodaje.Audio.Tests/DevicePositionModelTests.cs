namespace Rodaje.Audio.Tests;

/// <remarks>Marcas de tiempo en unidades de 100 ns (como la QPC de GetPosition) y 48 kHz: 1 ms = 10 000 marcas = 48 cuadros.</remarks>
public class DevicePositionModelTests
{
    private const int Rate = 48_000;
    private const long Ms = 10_000;
    private const long Frequency = 10_000_000;

    [Fact]
    public void InterpolaDesdeLaUltimaLecturaConSuMarcaQpc()
    {
        var model = Running(maxExtrapolation: 4_800);

        model.Observe(480, 10 * Ms);
        Assert.Equal(480, model.Read(10 * Ms));
        Assert.Equal(720, model.Read(15 * Ms));

        // La lectura llega tarde, pero su marca dice cuándo se tomó: la interpolación no acumula el retraso.
        model.Observe(960, 20 * Ms);
        Assert.Equal(1_200, model.Read(25 * Ms));
    }

    [Fact]
    public void AlArrancarNoInterpolaHastaQueElDispositivoSeMueve()
    {
        var model = Running(maxExtrapolation: 4_800);

        model.Observe(0, 1 * Ms);
        Assert.Equal(0, model.Read(5 * Ms));
        model.Observe(48, 7 * Ms);
        Assert.Equal(96, model.Read(8 * Ms));
    }

    [Fact]
    public void ConLecturasConJitterEsMonotonoYCercanoALaVerdad()
    {
        var random = new Random(1234);
        var model = Running(maxExtrapolation: 4_800);
        const double StartupMs = 6;
        long readings = 0;
        long previous = 0;
        double maxError = 0;
        for (long t = 0; t <= 3_000 * Ms; t += Ms / 4)
        {
            if (t % (10 * Ms) == 0)
            {
                // El dispositivo muestrea su posición en un instante algo desplazado; la pareja (posición, QPC) sigue siendo coherente.
                long sampledAt = Math.Max(0, t - (long)(random.NextDouble() * 0.6 * Ms));
                long frames = TrueFrames(sampledAt, StartupMs);
                // Ruido de ±0,3 ms en la marca, como el residuo medido en el sondeo.
                long noisyTimestamp = sampledAt + (long)((random.NextDouble() - 0.5) * 0.6 * Ms);
                model.Observe(frames, noisyTimestamp);
                readings++;
            }

            long value = model.Read(t);
            Assert.True(value >= previous, $"El reloj retrocedió en t={t / (double)Ms} ms: {previous} → {value}.");
            previous = value;
            if (t > 20 * Ms)
            {
                maxError = Math.Max(maxError, Math.Abs(value - TrueFrames(t, StartupMs)));
            }
        }

        Assert.Equal(301, readings);
        Assert.True(maxError <= 0.35 * 48, $"Error máximo {maxError} cuadros: más de lo que explica el ruido de la marca.");
    }

    [Fact]
    public void UnDispositivoAtascadoNoArrastraAlRelojMasAllaDelTope()
    {
        var model = Running(maxExtrapolation: 960);
        model.Observe(480, 10 * Ms);
        Assert.Equal(480 + 960, model.Read(1_000 * Ms));
        Assert.Equal(480 + 960, model.Read(2_000 * Ms));
    }

    [Fact]
    public void NuncaPasaDeLoEscrito()
    {
        var model = Running(maxExtrapolation: 48_000, written: 1_000);
        model.Observe(900, 10 * Ms);
        Assert.Equal(1_000, model.Read(500 * Ms));
        model.AddWritten(500);
        Assert.Equal(1_500, model.Read(600 * Ms));
    }

    [Fact]
    public void UnaLecturaQueRetrocedeSostieneElValor()
    {
        var model = Running(maxExtrapolation: 4_800);
        model.Observe(960, 20 * Ms);
        Assert.Equal(1_200, model.Read(25 * Ms));
        model.Observe(1_000, 25 * Ms);
        Assert.Equal(1_200, model.Read(25 * Ms));
        Assert.Equal(1_200, model.Read(29 * Ms));
        Assert.Equal(1_240, model.Read(30 * Ms));
    }

    [Fact]
    public void EnPausaSeCongelaYAlReanudarNoRetrocede()
    {
        var model = Running(maxExtrapolation: 4_800);
        model.Observe(480, 10 * Ms);
        model.Stop(12 * Ms);
        long frozen = model.Read(12 * Ms);
        Assert.Equal(576, frozen);
        Assert.False(model.IsRunning);

        // El dispositivo se detuvo un poco antes de lo interpolado y repite esa posición mientras está parado.
        model.Observe(560, 500 * Ms);
        Assert.Equal(frozen, model.Read(900 * Ms));

        model.Start(1_000 * Ms);
        model.Observe(560, 1_001 * Ms);
        Assert.Equal(frozen, model.Read(1_003 * Ms));
        // La posición real del dispositivo manda: 48 cuadros después de donde de verdad se detuvo.
        model.Observe(608, 1_005 * Ms);
        Assert.Equal(608, model.Read(1_005 * Ms));
        Assert.Equal(608 + 240, model.Read(1_010 * Ms));
    }

    [Fact]
    public void VaciarParaUnSeekSaltaALoEscritoYSigueDesdeCero()
    {
        var model = Running(maxExtrapolation: 4_800, written: 2_000);
        model.Observe(960, 20 * Ms);
        Assert.Equal(1_200, model.Read(25 * Ms));

        model.BeginSegment(model.Written, discontinuity: true);
        model.Start(25 * Ms);
        Assert.Equal(2_000, model.Read(26 * Ms));

        model.AddWritten(1_000);
        model.Observe(0, 27 * Ms);
        Assert.Equal(2_000, model.Read(30 * Ms));
        model.Observe(96, 32 * Ms);
        Assert.Equal(2_000 + 96 + 48, model.Read(33 * Ms));
    }

    [Fact]
    public void VaciarEnPausaDejaElValorEnLoEscrito()
    {
        var model = Running(maxExtrapolation: 4_800, written: 2_000);
        model.Observe(480, 10 * Ms);
        model.Stop(10 * Ms);
        model.BeginSegment(model.Written, discontinuity: true);
        Assert.Equal(2_000, model.Read(50 * Ms));
        Assert.Equal(2_000, model.Read(500 * Ms));
    }

    [Fact]
    public void AlReabrirOtroDispositivoElRelojSigueSinSaltar()
    {
        var model = Running(maxExtrapolation: 4_800, written: 10_000);
        model.Observe(4_800, 100 * Ms);
        long heard = model.ReadHeard(105 * Ms);
        Assert.Equal(5_040, heard);

        // El dispositivo nuevo cuenta desde 0, pero su cuadro 0 es lo primero que no se oyó en el anterior.
        model.BeginSegment(heard, discontinuity: false);
        model.Start(150 * Ms);
        Assert.Equal(heard, model.Read(160 * Ms));
        model.Observe(480, 170 * Ms);
        Assert.Equal(heard + 480, model.Read(170 * Ms));
        Assert.Equal(heard + 480 + 480, model.Read(180 * Ms));
    }

    [Fact]
    public void SinDispositivoCorreLibreConElRelojDePared()
    {
        var model = Running(maxExtrapolation: 4_800, written: 10_000);
        model.Observe(4_800, 100 * Ms);
        model.EnterFreeRun(105 * Ms);
        Assert.True(model.IsFreeRunning);
        Assert.Equal(5_040, model.Read(105 * Ms));
        model.Observe(1, 106 * Ms);
        Assert.Equal(5_040 + 4_800, model.Read(205 * Ms));

        model.Stop(205 * Ms);
        Assert.Equal(9_840, model.Read(1_000 * Ms));
        model.Start(1_000 * Ms);
        Assert.Equal(9_840 + 480, model.Read(1_010 * Ms));

        model.AddWritten(5_000);
        model.RebaseFreeRun(1_010 * Ms);
        Assert.Equal(15_000, model.Read(1_010 * Ms));
        Assert.Equal(15_048, model.Read(1_011 * Ms));

        model.BeginSegment(model.Written, discontinuity: false);
        model.AddWritten(10_000);
        Assert.False(model.IsFreeRunning);
        model.Start(1_020 * Ms);
        Assert.Equal(15_048, model.Read(1_025 * Ms));
        model.Observe(480, 1_030 * Ms);
        Assert.Equal(15_480, model.Read(1_030 * Ms));
    }

    [Fact]
    public void LaLatenciaExtraRetrasaElRelojSinHacerloSaltar()
    {
        var model = new DevicePositionModel(Rate, Frequency, maxExtrapolationFrames: 4_800, latencyFrames: 480);
        model.AddWritten(100_000);
        model.Start(0);
        model.Observe(240, 5 * Ms);
        Assert.Equal(0, model.Read(5 * Ms));
        Assert.Equal(240, model.ReadHeard(5 * Ms));
        Assert.Equal(480, model.Read(20 * Ms));

        // Tras un seek, el reloj espera en el destino hasta que lo que salió por el dispositivo lo alcance.
        model.BeginSegment(model.Written, discontinuity: true);
        model.AddWritten(10_000);
        model.Start(20 * Ms);
        model.Observe(240, 25 * Ms);
        Assert.Equal(100_000, model.Read(25 * Ms));
        Assert.Equal(100_000 + 480, model.Read(40 * Ms));

        // Al cambiar de dispositivo se conserva el retraso: no hay salto.
        long before = model.Read(40 * Ms);
        model.BeginSegment(model.ReadHeard(40 * Ms), discontinuity: false);
        model.Start(40 * Ms);
        Assert.Equal(before, model.Read(41 * Ms));
        model.Observe(48, 41 * Ms);
        Assert.Equal(before + 48, model.Read(41 * Ms));
    }

    [Fact]
    public void ValidaSusArgumentos()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DevicePositionModel(0, Frequency, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DevicePositionModel(Rate, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DevicePositionModel(Rate, Frequency, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DevicePositionModel(Rate, Frequency, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DevicePositionModel(Rate, Frequency, 1).AddWritten(-1));
    }

    [Fact]
    public void SecuenciasAleatoriasRespetanElContrato()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            var random = new Random(seed);
            var model = new DevicePositionModel(Rate, Frequency, maxExtrapolationFrames: 1 + random.Next(4_800), latencyFrames: random.Next(960));
            long now = 0;
            long device = 0;
            long last = 0;
            bool running = false;
            for (int step = 0; step < 400; step++)
            {
                now += random.Next(0, 3 * (int)Ms);
                switch (random.Next(12))
                {
                    case 0:
                        model.Start(now);
                        running = true;
                        break;
                    case 1:
                        model.Stop(now);
                        running = false;
                        break;
                    case 2:
                        model.BeginSegment(model.Written, discontinuity: true);
                        device = 0;
                        last = model.Read(now);
                        break;
                    case 3:
                        model.BeginSegment(model.ReadHeard(now), discontinuity: false);
                        device = 0;
                        break;
                    case 4:
                        model.EnterFreeRun(now);
                        break;
                    case 5:
                        model.AddWritten(random.Next(2_000));
                        break;
                    default:
                        device = Math.Max(0, device + random.Next(-50, 500));
                        model.Observe(device, now - random.Next(0, (int)Ms));
                        break;
                }

                long value = model.Read(now);
                Assert.True(value >= last, $"Semilla {seed}, paso {step}: retrocedió de {last} a {value}.");
                if (!running)
                {
                    Assert.Equal(value, model.Read(now + (100 * Ms)));
                }

                last = value;
            }
        }
    }

    private static DevicePositionModel Running(long maxExtrapolation, long written = 1_000_000)
    {
        var model = new DevicePositionModel(Rate, Frequency, maxExtrapolation);
        model.AddWritten(written);
        model.Start(0);
        return model;
    }

    private static long TrueFrames(long timestamp, double startupMs) =>
        Math.Max(0, (long)Math.Round(((timestamp / (double)Ms) - startupMs) * 48));
}
