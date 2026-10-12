using Rodaje.Audio.Processing;
using Xunit;
using Xunit.Abstractions;

namespace Rodaje.Audio.Tests.Processing;

/// <summary>Respuesta en frecuencia del ecualizador y curva y tiempos del compresor contra sus referencias.</summary>
public sealed class AudioEffectChainTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    private static readonly EqualizerSettings Flat = new(0, 0, 120, 0, 500, 1, 0, 3000, 1, 0, 8000);

    [Fact]
    public void Each_band_hits_its_textbook_reference()
    {
        // Campana de +6 dB en 1 kHz, Q 1: +6 en el centro.
        Assert.Equal(6, Measure(Flat with { MidGainDb = 6, MidHz = 1000 }, 1000), 0.02);
        Assert.Equal(0, Measure(Flat with { MidGainDb = 6, MidHz = 1000 }, 50), 0.05);

        // Pasa altos de Butterworth en 100 Hz: −3,01 dB en el corte, −12 dB por octava debajo y plano arriba.
        Assert.Equal(-3.01, Measure(Flat with { LowCutHz = 100 }, 100), 0.03);
        Assert.Equal(-12.3, Measure(Flat with { LowCutHz = 100 }, 50), 0.1);
        Assert.Equal(0, Measure(Flat with { LowCutHz = 100 }, 2000), 0.02);

        // Estantes con pendiente 1: toda la ganancia lejos, la mitad (en dB) en su frecuencia.
        Assert.Equal(6, Measure(Flat with { LowGainDb = 6, LowHz = 200 }, 20), 0.05);
        Assert.Equal(3, Measure(Flat with { LowGainDb = 6, LowHz = 200 }, 200), 0.03);
        Assert.Equal(-6, Measure(Flat with { HighGainDb = -6, HighHz = 4000 }, 20000), 0.1);
        Assert.Equal(-3, Measure(Flat with { HighGainDb = -6, HighHz = 4000 }, 4000), 0.03);
    }

    [Fact]
    public void Five_band_response_matches_the_transfer_function_from_30_hz_to_16_khz()
    {
        var eq = new EqualizerSettings(80, 2, 120, -3, 400, 1.2, 4, 3500, 1.5, 2.5, 10000);
        double worst = 0;
        for (int i = 0; i <= 24; i++)
        {
            double hz = 30 * Math.Pow(16000.0 / 30, i / 24.0);
            double measured = Measure(eq, hz);
            double expected = eq.MagnitudeDb(Rate, hz);
            worst = Math.Max(worst, Math.Abs(measured - expected));
        }

        output.WriteLine($"Error máximo de la respuesta medida frente a la teórica en 25 frecuencias: {worst:0.0000} dB");
        Assert.True(worst < 0.03, $"Error {worst:0.0000} dB");
    }

    [Fact]
    public void Compressor_static_curve_matches_the_soft_knee_reference()
    {
        var settings = new CompressorSettings(-20, 4, 5, 50, 3, 6);
        double worst = 0;
        for (double input = -50; input <= 0; input += 1)
        {
            // Una continua no tiene ondulación: la envolvente llega exactamente a la reducción de la curva.
            float[] signal = Constant((float)Math.Pow(10, input / 20), Rate);
            var chain = new AudioEffectChain(new AudioEffectsSettings(null, settings), Rate);
            chain.Process(signal);
            double output = 20 * Math.Log10(signal[^1]);
            worst = Math.Max(worst, Math.Abs(output - (Reference(input, -20, 4, 6) + 3)));
        }

        output.WriteLine($"Error máximo de la curva estática (umbral −20, 4:1, rodilla 6 dB) de −50 a 0 dBFS: {worst:0.00000} dB");
        Assert.True(worst < 0.001, $"Error {worst} dB");
    }

    [Fact]
    public void Attack_and_release_follow_their_time_constants()
    {
        var settings = new CompressorSettings(-20, 4, 10, 100, 0, 0);
        var chain = new AudioEffectChain(new AudioEffectsSettings(null, settings), Rate);
        float[] quiet = Constant((float)Math.Pow(10, -40 / 20.0), Rate / 2);
        chain.Process(quiet);
        float[] loud = Constant((float)Math.Pow(10, -10 / 20.0), Rate / 2);
        chain.Process(loud);

        // Pasados 10 ms la reducción llega al 63 % (1 − 1/e) de los 7,5 dB que pide la curva.
        double atAttack = -10 - (20 * Math.Log10(loud[2 * (Rate / 100)]));
        double settled = -10 - (20 * Math.Log10(loud[^1]));
        float[] back = Constant((float)Math.Pow(10, -40 / 20.0), Rate / 2);
        chain.Process(back);
        double atRelease = -40 - (20 * Math.Log10(back[2 * (Rate / 10)]));

        output.WriteLine($"Reducción a los 10 ms: {atAttack:0.000} dB (63 %: {7.5 * (1 - Math.Exp(-1)):0.000}); asentada {settled:0.000}; " +
            $"a los 100 ms de relajar {atRelease:0.000} (37 %: {7.5 * Math.Exp(-1):0.000})");
        Assert.Equal(7.5, settled, 0.001);
        Assert.Equal(7.5 * (1 - Math.Exp(-1)), atAttack, 0.01);
        Assert.Equal(7.5 * Math.Exp(-1), atRelease, 0.01);
    }

    [Fact]
    public void Changing_a_band_while_playing_keeps_the_filter_state()
    {
        var chain = new AudioEffectChain(new AudioEffectsSettings(Flat with { MidGainDb = 3, MidHz = 1000 }, null), Rate);
        float[] first = Sine(440, 0.5, 0, Rate / 10);
        chain.Process(first);
        chain.Configure(new AudioEffectsSettings(Flat with { MidGainDb = 4, MidHz = 1000 }, null));
        float[] second = Sine(440, 0.5, Rate / 10, Rate / 10);
        chain.Process(second);

        double jump = Math.Abs(second[0] - first[^2]);
        double ideal = 2 * Math.PI * 440 / Rate * 0.6;
        output.WriteLine($"Salto al cambiar la campana de +3 a +4 dB: {jump:0.0000} (paso del seno ≈ {ideal:0.0000})");
        Assert.True(jump < ideal * 1.5);
    }

    [Fact]
    public void Block_by_block_processing_matches_one_continuous_pass_without_hanging()
    {
        // Pasada normal: la corrección del resultado y solo una guarda holgada contra cuelgues (1 s por bloque, unas
        // 30 veces lo peor visto en un runner de Windows cargado, 36,5 ms). La cota fina de 1 ms vive en la prueba
        // TiempoReal de abajo, que la CI excluye porque mide reloj de pared.
        (double ms, float[] blockwise) = RunBlocks(out float[] source);
        output.WriteLine($"Bloque de 1024 cuadros estéreo con 5 bandas y compresor: {ms:0.000} ms");

        // Partir en bloques no cambia nada: el estado de los filtros y la envolvente pasan de un bloque al siguiente.
        float[] continuous = (float[])source.Clone();
        new AudioEffectChain(ChainSettings, Rate).Process(continuous);
        int firstMismatch = -1;
        for (int i = 0; i < continuous.Length && firstMismatch < 0; i++)
        {
            if (BitConverter.SingleToInt32Bits(continuous[i]) != BitConverter.SingleToInt32Bits(blockwise[i]))
            {
                firstMismatch = i;
            }
        }

        Assert.True(firstMismatch < 0, firstMismatch < 0 ? "" :
            $"Muestra {firstMismatch}: en bloques {blockwise[firstMismatch]}, de una vez {continuous[firstMismatch]}");

        // Y el resultado es audio: finito, no silencio, y el compresor (umbral −24, 4:1, +6 de compensación) lo deja
        // dentro de ±1 aunque las bandas suban la senoide de 0,5.
        float peak = 0;
        foreach (float v in blockwise)
        {
            Assert.True(float.IsFinite(v));
            peak = Math.Max(peak, Math.Abs(v));
        }

        Assert.InRange(peak, 0.05f, 1f);
        Assert.True(ms < 1000, $"{ms} ms por bloque: más que un cuelgue que una máquina cargada");
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void A_block_with_five_bands_and_the_compressor_costs_well_under_a_millisecond()
    {
        (double ms, _) = RunBlocks(out _);
        output.WriteLine($"Bloque de 1024 cuadros estéreo con 5 bandas y compresor: {ms:0.000} ms ({1024.0 / Rate * 1000 / ms:0}× tiempo real)");
        Assert.True(ms < 1, $"{ms} ms");
    }

    private static readonly AudioEffectsSettings ChainSettings = new(new EqualizerSettings(80, 2, 120, -3, 400, 1.2, 4, 3500, 1.5, 2.5, 10000),
        new CompressorSettings(-24, 4, 5, 150, 6, 6));

    /// <summary>
    /// Pasa por una cadena 50 bloques de calentamiento y 470 medidos de 1024 cuadros (una senoide de 440 Hz repetida) y
    /// devuelve los milisegundos por bloque medido y la señal procesada; <paramref name="source"/> es la de entrada.
    /// </summary>
    private static (double MsPerBlock, float[] Processed) RunBlocks(out float[] source)
    {
        const int frames = 1024, warmup = 50, blocks = 470;
        float[] block = Sine(440, 0.5, 0, frames);
        source = new float[block.Length * (warmup + blocks)];
        for (int i = 0; i < warmup + blocks; i++)
        {
            block.CopyTo(source, i * block.Length);
        }

        float[] signal = (float[])source.Clone();
        var chain = new AudioEffectChain(ChainSettings, Rate);
        for (int i = 0; i < warmup; i++)
        {
            chain.Process(signal.AsSpan(i * block.Length, block.Length));
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = warmup; i < warmup + blocks; i++)
        {
            chain.Process(signal.AsSpan(i * block.Length, block.Length));
        }

        return (watch.Elapsed.TotalMilliseconds / blocks, signal);
    }

    private static double Reference(double x, double threshold, double ratio, double knee)
    {
        double over = x - threshold;
        if (over <= -knee / 2)
        {
            return x;
        }

        if (over >= knee / 2)
        {
            return threshold + (over / ratio);
        }

        return x + ((1 / ratio) - 1) * Math.Pow(over + (knee / 2), 2) / (2 * knee);
    }

    /// <summary>Ganancia en dB de un seno de <paramref name="hz"/> por el ecualizador, medida tras el transitorio.</summary>
    private static double Measure(EqualizerSettings eq, double hz)
    {
        int length = Rate;
        float[] signal = Sine(hz, 0.25, 0, length);
        var chain = new AudioEffectChain(new AudioEffectsSettings(eq, null), Rate);
        chain.Process(signal);

        // Un número entero de periodos en la ventana evita el sesgo del RMS.
        int periods = Math.Max(1, (int)Math.Floor(hz * 0.5));
        int window = (int)Math.Round(periods * Rate / hz);
        double sum = 0;
        for (int i = length - window; i < length; i++)
        {
            sum += (double)signal[2 * i] * signal[2 * i];
        }

        double rms = Math.Sqrt(sum / window);
        double reference = Math.Sqrt(Enumerable.Range(length - window, window).Sum(i => Math.Pow(0.25 * Math.Sin(2 * Math.PI * hz * i / Rate), 2)) / window);
        return 20 * Math.Log10(rms / reference);
    }

    private static float[] Sine(double hz, double amplitude, int first, int frames)
    {
        float[] signal = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * (first + i) / Rate));
            signal[2 * i] = v;
            signal[(2 * i) + 1] = v;
        }

        return signal;
    }

    private static float[] Constant(float value, int frames)
    {
        float[] signal = new float[frames * 2];
        Array.Fill(signal, value);
        return signal;
    }
}
