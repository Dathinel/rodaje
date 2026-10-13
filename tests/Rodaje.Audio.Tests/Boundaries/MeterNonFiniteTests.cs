using Rodaje.Audio.Metering;
using Rodaje.Engine.Tests.Timeline;

namespace Rodaje.Audio.Tests.Boundaries;

/// <summary>
/// Tarea 215: lo que no es un número (NaN, ±∞) o es enorme no llega al medidor ni lo deja atascado, y medir por bloques
/// de cualquier tamaño da lo mismo que medir de una vez. Las semillas salen de <see cref="StressSeeds"/>: el «semilla N»
/// de un fallo reproduce la corrida.
/// </summary>
public sealed class MeterNonFiniteTests
{
    private static readonly float[] Poison = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, -float.MaxValue];

    /// <summary>
    /// Defecto: una sola muestra NaN o ±∞ en la mezcla quedaba en el estado del filtro de ponderación K (y en la historia
    /// del pico real) hasta el siguiente seek: la sonoridad momentánea y corta salía NaN el resto de la reproducción, y el
    /// pico del bloque, NaN.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void UnaMuestraNoNumerica_NoEnvenenaLaSonoridadNiLosPicos(int which)
    {
        int seed = StressSeeds.Seed(215, 1, which);
        var random = new Random(seed);
        var ring = new AudioLevelRing();
        var analyzer = new AudioLevelAnalyzer(ring);
        const int frames = 8192;
        float[] mix = Sine(frames, 0.5f);
        int at = random.Next(0, 256) * 2;
        mix[at] = Poison[which];

        analyzer.Process(mix, 0);

        AudioLevels all = ring.Measure(0, frames, 0);
        AssertFinite(all, $"semilla {seed}, {Poison[which]} en {at}: todo el tramo");
        Assert.True(all.Clipped, $"semilla {seed}: una muestra {Poison[which]} tiene que encender la saturación.");

        // Lo que sigue, lejos del valor malo, mide la señal limpia: 0,5 de pico y −6 LUFS más o menos.
        AudioLevels later = ring.Measure(4096, frames, 4096);
        AssertFinite(later, $"semilla {seed}: después");
        Assert.InRange(later.Left.Peak, 0.49f, 0.5f);
        double power = ring.WeightedPower(4096, frames);
        Assert.True(double.IsFinite(power) && power > 0.01, $"semilla {seed}: potencia ponderada {power}");
    }

    [Fact]
    public void LaSaturacionAntesDelLimitador_CuentaLoQueNoEsUnNumero()
    {
        var ring = new AudioLevelRing();
        var analyzer = new AudioLevelAnalyzer(ring);
        float[] mix = Sine(256, 0.5f);
        float[] raw = (float[])mix.Clone();
        raw[10] = float.NaN;
        analyzer.Process(mix, 0, raw);
        Assert.True(ring.Measure(0, 256, 0).Clipped);
    }

    /// <summary>Defecto: el pico real devolvía NaN y lo dejaba en su historia 16 muestras.</summary>
    [Fact]
    public void ElPicoRealIgnoraLoQueNoEsUnNumero()
    {
        var detector = new TruePeakDetector();
        float[] samples = Sine(64, 0.5f);
        samples[20] = float.NaN;
        samples[40] = float.PositiveInfinity;
        float peak = detector.Process(samples, 0, 2, 64);
        Assert.True(float.IsFinite(peak), $"pico real {peak}");
        float clean = detector.Process(Sine(64, 0.5f), 0, 2, 64);
        Assert.InRange(clean, 0.49f, 0.6f);
    }

    /// <summary>Por bloques de cualquier tamaño (también 0 y 1 cuadro) el anillo recibe los mismos bloques que de una vez.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MedirPorBloques_DaLoMismoQueDeUnaVez(int round)
    {
        int seed = StressSeeds.Seed(215, 2, round);
        var random = new Random(seed);
        const int frames = 10_000;
        float[] mix = new float[frames * 2];
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] = (float)((random.NextDouble() * 2) - 1) * 1.2f;
        }

        var whole = new AudioLevelRing();
        new AudioLevelAnalyzer(whole).Process(mix, 0);
        var pieces = new AudioLevelRing();
        var analyzer = new AudioLevelAnalyzer(pieces);
        int at = 0;
        while (at < frames)
        {
            int n = Math.Min(frames - at, random.Next(0, 300));
            analyzer.Process(mix.AsSpan(at * 2, n * 2), at);
            at += n;
        }

        Assert.Equal(whole.Count, pieces.Count);
        for (long from = 0; from < frames; from += 1000)
        {
            Assert.Equal(whole.Measure(from, from + 1000, from), pieces.Measure(from, from + 1000, from));
            Assert.Equal(whole.WeightedPower(from, from + 1000), pieces.WeightedPower(from, from + 1000));
        }
    }

    /// <summary>Un bloque con NaN o ±∞ escrito por otro medidor (las tiras del mezclador) no deja NaN ni ∞ en la lectura.</summary>
    [Fact]
    public void UnBloqueNoNumericoEnElAnillo_NoLlegaALaLectura()
    {
        var ring = new AudioLevelRing();
        ring.Write(new LevelBlock(0, 128, float.NaN, float.PositiveInfinity, float.NaN, float.PositiveInfinity,
            double.NaN, double.PositiveInfinity, Clipped: false, Weighted: double.NaN));
        ring.Write(new LevelBlock(128, 128, 0.25f, 0.25f, 0.25f, 0.25f, 8, 8, Clipped: false, Weighted: 16));

        AudioLevels levels = ring.Measure(0, 256, 0);
        AssertFinite(levels, "bloque no numérico");
        Assert.True(levels.Clipped, "Lo que no es un número tiene que encender la saturación.");
        Assert.True(double.IsFinite(ring.WeightedPower(0, 256)));
        Assert.Equal(0.25f, ring.Measure(128, 256, 128).Left.Peak);
    }

    /// <summary>
    /// Defecto: un pico de +∞ dB dejaba la barra y el pico retenido llenos para siempre (∞ menos la caída sigue siendo ∞), y
    /// un tiempo transcurrido NaN dejaba la barra en NaN; uno infinito congelaba el pico retenido.
    /// </summary>
    [Theory]
    [InlineData(double.PositiveInfinity, 0.016)]
    [InlineData(double.MaxValue, 0.016)]
    [InlineData(-6, double.NaN)]
    [InlineData(-6, double.PositiveInfinity)]
    public void LaBalisticaNoSeAtascaConValoresNoFinitos(double peakDb, double elapsed)
    {
        var meter = new MeterBallistics();
        meter.Update(peakDb, 0.016);
        meter.Update(peakDb, elapsed);
        meter.Update(-6, elapsed);
        Assert.False(double.IsNaN(meter.Level) || double.IsNaN(meter.PeakHold) || double.IsNaN(meter.MaxPeak), $"{meter.Level} {meter.PeakHold}");
        Assert.True(meter.Level < double.PositiveInfinity && meter.PeakHold < double.PositiveInfinity && meter.MaxPeak < double.PositiveInfinity);

        // Con silencio, en unos segundos todo se apaga.
        for (int i = 0; i < 600; i++)
        {
            meter.Update(double.NegativeInfinity, 0.016);
        }

        Assert.True(meter.IsIdle, $"La barra quedó en {meter.Level} dB y el pico retenido en {meter.PeakHold} dB.");
    }

    private static float[] Sine(int frames, float amplitude)
    {
        float[] samples = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = amplitude * MathF.Sin(2 * MathF.PI * 997 * f / 48_000);
            samples[f * 2] = v;
            samples[(f * 2) + 1] = v;
        }

        return samples;
    }

    private static void AssertFinite(AudioLevels levels, string what)
    {
        float[] values = [levels.Left.Peak, levels.Left.TruePeak, levels.Left.Rms, levels.Right.Peak, levels.Right.TruePeak, levels.Right.Rms];
        Assert.True(Array.TrueForAll(values, float.IsFinite), $"{what}: {levels}");
    }
}
