using System.Globalization;
using Rodaje.Engine.Offline;
using Xunit.Abstractions;

namespace Rodaje.Engine.Tests.Offline;

/// <summary>
/// Propiedades de <see cref="RateConverter"/> con semilla fija: la mezcla de 48 kHz pasada a 44,1/96 kHz y a frecuencias
/// raras sale igual en bloques de cualquier tamaño que de una vez, sin muestra perdida ni repetida en los bordes de bloque
/// y sin deriva aunque se empiece horas dentro.
/// </summary>
public sealed class RateConverterEdgeTests(ITestOutputHelper output)
{
    private const int Mix = 48_000;

    /// <summary>Las estándar, una de 1 Hz de diferencia (más fases que la tabla), primas y muy bajas o muy altas.</summary>
    public static TheoryData<int> Rates => [44_100, 96_000, 32_000, 22_050, 88_200, 192_000, 11_025, 8_000, 44_101, 47_999, 48_001, 37_337, 7];

    [Theory]
    [MemberData(nameof(Rates))]
    public void Blocks_of_any_size_give_the_same_samples_as_one_read(int rate)
    {
        var converter = new RateConverter(Mix, rate);
        var random = new Random(21_700 + rate);
        long first = random.NextInt64(0, 10L * rate);
        int total = Math.Max(64, rate / 10);
        Func<long, float> noise = Noise(rate);

        (float[] left, float[] right) whole = Convert(converter, noise, first, [total]);
        var sizes = new List<int>();
        for (int left = total; left > 0;)
        {
            int n = Math.Min(left, random.Next(4) switch { 0 => 1, 1 => random.Next(1, 8), 2 => random.Next(1, 200), _ => random.Next(1, 5000) });
            sizes.Add(n);
            left -= n;
        }

        (float[] left, float[] right) blocks = Convert(converter, noise, first, sizes);

        Assert.Equal(whole.left, blocks.left);
        Assert.Equal(whole.right, blocks.right);
        Assert.All(whole.left, v => Assert.True(float.IsFinite(v)));
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Each_output_reads_a_window_that_advances_without_gaps_or_steps_back(int rate)
    {
        var converter = new RateConverter(Mix, rate);
        var random = new Random(21_701 + rate);
        long first = random.NextInt64(0, 3600L * rate);
        long previous = converter.FirstInput(first - 1);
        for (long n = first; n < first + 5000; n++)
        {
            long from = converter.FirstInput(n);
            Assert.Equal(converter.Taps + 1, converter.LastInput(n) - from + 1);

            // La posición en la mezcla de la salida n es n · 48 000 / R: avanza eso (redondeado) a cada salida.
            long step = from - previous;
            Assert.InRange(step, Mix / rate, (Mix + rate - 1) / rate);
            previous = from;
        }
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void An_impulse_of_the_mix_comes_out_at_its_instant(int rate)
    {
        if (rate < 1000)
        {
            // A 7 Hz el filtro deja pasar menos de 3 Hz: un impulso se reparte entre muchas salidas.
            return;
        }

        var converter = new RateConverter(Mix, rate);
        var random = new Random(21_702 + rate);
        for (int trial = 0; trial < 20; trial++)
        {
            long at = random.NextInt64(0, 3600L * Mix);
            Func<long, float> impulse = i => i == at ? 1f : 0f;
            long center = (long)Math.Floor((double)at * rate / Mix);
            long first = center - 200;
            float[] left = Convert(converter, impulse, first, [401]).left;

            // El pico cae en la salida más cercana al instante at / 48 000 (o en su vecina si el impulso queda en medio).
            int peak = Array.IndexOf(left, left.Max());
            double ideal = ((double)at * rate / Mix) - first;
            Assert.True(Math.Abs(peak - ideal) <= 1, string.Create(CultureInfo.InvariantCulture,
                $"A {rate} Hz el impulso de la muestra {at} sale en {first + peak} y debía salir hacia {first + ideal:F2}."));
        }
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(96_000)]
    [InlineData(44_101)]
    [InlineData(47_999)]
    [InlineData(37_337)]
    [InlineData(22_050)]
    public void A_sine_three_hours_in_stays_in_phase(int rate)
    {
        const double Hz = 997;
        var converter = new RateConverter(Mix, rate);
        Func<long, float> sine = i => (float)(0.5 * Math.Sin(2 * Math.PI * Hz * (i % (48L * 1000)) / Mix));
        long first = 3L * 3600 * rate;
        float[] left = Convert(converter, sine, first, [rate / 5]).left;

        double worst = 0;
        for (int n = 0; n < left.Length; n++)
        {
            double t = (double)(first + n) / rate;
            double ideal = 0.5 * Math.Sin(2 * Math.PI * Hz * (t - Math.Floor(t)));
            worst = Math.Max(worst, Math.Abs(left[n] - ideal));
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"48 kHz → {rate} Hz a las 3 h: peor muestra {worst:E2}"));
        Assert.True(worst < 1e-3, string.Create(CultureInfo.InvariantCulture, $"A {rate} Hz el seno se aparta {worst:E2} tras 3 horas."));
    }

    /// <summary>Ruido blanco con semilla fija: la muestra i sale igual se pida como se pida; L y R distintos.</summary>
    private static Func<long, float> Noise(int seed) => i =>
    {
        ulong x = (ulong)(i * 2) ^ ((ulong)seed * 0x9E3779B97F4A7C15UL);
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCDUL;
        x ^= x >> 33;
        return (float)((x >> 40) / (double)(1UL << 24) * 2 - 1) * 0.5f;
    };

    /// <summary>Como <c>OfflineAudio.WriteWave</c>: cada bloque lee de la mezcla lo que piden sus salidas y lo convierte.</summary>
    private static (float[] left, float[] right) Convert(RateConverter converter, Func<long, float> signal, long first, IReadOnlyList<int> sizes)
    {
        int total = sizes.Sum();
        var left = new float[total];
        var right = new float[total];
        int done = 0;
        foreach (int frames in sizes)
        {
            long at = first + done;
            long from = converter.FirstInput(at);
            long to = converter.LastInput(at + frames - 1);
            var inLeft = new float[to - from + 1];
            var inRight = new float[inLeft.Length];
            for (int i = 0; i < inLeft.Length; i++)
            {
                long s = from + i;
                inLeft[i] = s < 0 ? 0 : signal(s);
                inRight[i] = s < 0 ? 0 : -0.5f * signal(s);
            }

            converter.Convert(at, left.AsSpan(done, frames), right.AsSpan(done, frames), inLeft, inRight, from);
            done += frames;
        }

        return (left, right);
    }
}
