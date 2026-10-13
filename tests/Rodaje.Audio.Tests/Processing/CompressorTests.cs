using Rodaje.Audio.Metering;
using Rodaje.Audio.Processing;
using Xunit;
using Xunit.Abstractions;

namespace Rodaje.Audio.Tests.Processing;

/// <summary>El compresor completo contra la teoría: curva, tiempos, anticipación, sidechain, multibanda y limitador.</summary>
public sealed class CompressorTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    [Theory]
    [InlineData(-20, 4, 6, LevelDetector.Peak)]
    [InlineData(-10, CompressorSettings.InfiniteRatio, 0, LevelDetector.Peak)]
    [InlineData(-10, CompressorSettings.InfiniteRatio, 6, LevelDetector.Peak)]
    [InlineData(-30, 2, 12, LevelDetector.Rms)]
    public void Static_curve_matches_the_theory_with_knee_and_infinite_ratio(double threshold, double ratio, double knee, LevelDetector detector)
    {
        var settings = new CompressorSettings(threshold, ratio, 1, 50, 0, knee) { Detector = detector, RmsWindowMs = 5 };
        double worst = 0;
        for (double input = -60; input <= 0; input += 0.5)
        {
            float[] signal = Constant((float)Math.Pow(10, input / 20), Rate / 2);
            new AudioEffectChain(new AudioEffectsSettings(null, settings), Rate).Process(signal);
            worst = Math.Max(worst, Math.Abs(Db(signal[^1]) - Reference(input, threshold, ratio, knee)));
        }

        output.WriteLine($"Curva estática (umbral {threshold}, {(ratio >= 100 ? "∞" : $"{ratio}")}:1, rodilla {knee} dB, {detector}) de −60 a 0 dBFS: error {worst:0.00000} dB");
        Assert.True(worst < 0.001, $"Error {worst} dB");
    }

    [Fact]
    public void Automatic_release_recovers_fast_after_a_hit_and_slowly_after_sustained_compression()
    {
        var settings = new CompressorSettings(-20, 4, 1, 60, 0, 0) { AutoRelease = true };
        double afterHit = Recovery(settings, Rate * 30 / 1000);
        double afterLong = Recovery(settings, Rate * 3);
        double plain = Recovery(settings with { AutoRelease = false }, Rate * 3);
        output.WriteLine($"Hasta bajar de 1 dB de reducción: tras 30 ms de golpe {afterHit:0.0} ms; tras 3 s {afterLong:0.0} ms; sin automática {plain:0.0} ms");
        Assert.True(afterLong > 3 * afterHit, "La relajación no depende del programa.");
        Assert.True(afterHit < plain * 1.5);
    }

    [Fact]
    public void Lookahead_delays_the_audio_exactly_and_reduces_before_the_transient()
    {
        const int Look = Rate * 5 / 1000;
        float[] step = Step(Rate / 10, Rate / 20, -40, -6);
        float[] passed = (float[])step.Clone();
        new AudioEffectChain(new AudioEffectsSettings(null, new CompressorSettings(0, 1, 1, 50, 0, 0) { LookaheadMs = 5 }), Rate).Process(passed);
        for (int i = 0; i < step.Length - (2 * Look); i++)
        {
            Assert.Equal(step[i], passed[i + (2 * Look)]);
        }

        var hard = new CompressorSettings(-20, CompressorSettings.InfiniteRatio, 1, 50, 0, 0);
        float[] ahead = (float[])step.Clone();
        new AudioEffectChain(new AudioEffectsSettings(null, hard with { LookaheadMs = 5 }), Rate).Process(ahead);
        float[] late = (float[])step.Clone();
        new AudioEffectChain(new AudioEffectsSettings(null, hard), Rate).Process(late);

        // Lo que pasa de −20 dBFS en el primer milisegundo después del golpe.
        double overAhead = Db(Peak(ahead, (Rate / 20) + Look, Rate / 1000)) + 20;
        double overLate = Db(Peak(late, Rate / 20, Rate / 1000)) + 20;
        output.WriteLine($"Escalón de −40 a −6 dBFS, ∞:1 en −20, ataque 1 ms: sobrepasa {overLate:0.00} dB sin anticipación y {overAhead:0.00} dB con 5 ms (retraso exacto de {Look} muestras)");
        Assert.True(overAhead < 0.2, $"{overAhead} dB");
        Assert.True(overLate > 6);
    }

    [Fact]
    public void Sidechain_high_pass_keeps_bass_from_triggering_the_compressor()
    {
        var settings = new CompressorSettings(-20, 4, 1, 50, 0, 0);
        double plain = SettledReduction(settings, Sine(50, 0.5, 0, Rate));
        double filtered = SettledReduction(settings with { SidechainHighPassHz = 200 }, Sine(50, 0.5, 0, Rate));
        output.WriteLine($"Seno de 50 Hz a −6 dBFS: reducción {plain:0.00} dB sin filtro y {filtered:0.00} dB con pasa altos de 200 Hz en el detector");
        Assert.True(plain > 9);
        Assert.True(filtered < 0.5);
    }

    [Fact]
    public void An_external_key_drives_the_reduction_of_another_signal()
    {
        var settings = new CompressorSettings(-30, 10, 1, 80, 0, 0);
        float[] music = Sine(200, 0.25, 0, Rate);
        float[] silentKey = new float[music.Length];
        float[] voiceKey = Constant(0.5f, Rate);
        float[] free = (float[])music.Clone();
        new AudioEffectChain(new AudioEffectsSettings(null, settings), Rate).Process(free, silentKey);
        float[] ducked = (float[])music.Clone();
        new AudioEffectChain(new AudioEffectsSettings(null, settings), Rate).Process(ducked, voiceKey);

        double expected = -6.02 - Reference(-6.02, -30, 10, 0);
        double measured = Db(Peak(music, Rate / 2, Rate / 4)) - Db(Peak(ducked, Rate / 2, Rate / 4));
        output.WriteLine($"Llave de −6 dBFS sobre una música a −12: la música baja {measured:0.00} dB (curva: {expected:0.00}); con la llave en silencio, {Db(Peak(music, Rate / 2, Rate / 4)) - Db(Peak(free, Rate / 2, Rate / 4)):0.000} dB");
        Assert.Equal(expected, measured, 0.3);
        Assert.Equal(Peak(music, Rate / 2, Rate / 4), Peak(free, Rate / 2, Rate / 4), 1e-6);
    }

    [Fact]
    public void Unlinked_channels_compress_each_on_its_own()
    {
        float[] signal = new float[Rate * 2];
        for (int i = 0; i < Rate; i++)
        {
            signal[2 * i] = 0.5f;
            signal[(2 * i) + 1] = 0.05f;
        }

        var settings = new CompressorSettings(-20, 4, 1, 50, 0, 0);
        float[] linked = (float[])signal.Clone();
        new AudioEffectChain(new AudioEffectsSettings(null, settings), Rate).Process(linked);
        float[] dual = (float[])signal.Clone();
        new AudioEffectChain(new AudioEffectsSettings(null, settings with { Linked = false }), Rate).Process(dual);
        output.WriteLine($"Izquierdo a −6, derecho a −26 dBFS: el derecho baja {Db(0.05) - Db(linked[^1]):0.00} dB enlazado y {Db(0.05) - Db(dual[^1]):0.00} dB por canal");
        double reduction = Db(0.5) - Reference(Db(0.5), -20, 4, 0);
        Assert.Equal(Db(0.05) - reduction, Db(linked[^1]), 0.01);
        Assert.Equal(0.05f, dual[^1], 1e-6);
        Assert.Equal(Db(0.5) - reduction, Db(dual[^2]), 0.01);
    }

    [Fact]
    public void Parallel_mix_and_automatic_makeup_follow_their_formulas()
    {
        var settings = new CompressorSettings(-20, 4, 1, 50, 0, 0);
        float[] half = Constant(0.5f, Rate / 2);
        new AudioEffectChain(new AudioEffectsSettings(null, settings with { Mix = 0.5 }), Rate).Process(half);
        double wet = Math.Pow(10, Reference(Db(0.5), -20, 4, 0) / 20);
        Assert.Equal((0.5 * 0.5) + (0.5 * wet), half[^1], 1e-5);

        float[] full = Constant(1f, Rate / 2);
        new AudioEffectChain(new AudioEffectsSettings(null, settings with { AutoMakeup = true }), Rate).Process(full);
        output.WriteLine($"Mezcla 50 %: {half[^1]:0.00000} (fórmula {(0.25) + (0.5 * wet):0.00000}); compensación automática a 0 dBFS: {Db(full[^1]):0.000} dB (−15 + 7,5)");
        Assert.Equal(-7.5, Db(full[^1]), 0.001);
    }

    [Fact]
    public void Multiband_crossovers_sum_flat_within_five_hundredths_of_a_db()
    {
        var bands = new CompressorSettings(0, 1, 1, 50, 0, 0) { Multiband = new MultibandSettings(200, 3000) };
        double worst = 0;
        for (int i = 0; i <= 30; i++)
        {
            double hz = 20 * Math.Pow(1000, i / 30.0);
            float[] signal = Sine(hz, 0.1, 0, Rate);
            new AudioEffectChain(new AudioEffectsSettings(null, bands), Rate).Process(signal);
            worst = Math.Max(worst, Math.Abs(Db(Rms(signal, Rate / 2, Rate / 2) / Rms(Sine(hz, 0.1, 0, Rate), Rate / 2, Rate / 2))));
        }

        output.WriteLine($"Suma de las tres bandas (cruces en 200 y 3000 Hz, Linkwitz-Riley de 4.º orden) de 20 Hz a 20 kHz: error máximo {worst:0.0000} dB");
        Assert.True(worst <= 0.05, $"{worst} dB");
    }

    [Fact]
    public void Multiband_compresses_only_the_band_over_its_threshold()
    {
        var settings = new CompressorSettings(-20, 8, 1, 50, 0, 0) { Multiband = new MultibandSettings(300, 4000, MidThresholdDb: 30, HighThresholdDb: 30) };
        float[] bass = Sine(80, 0.4, 0, Rate);
        float[] treble = Sine(8000, 0.4, 0, Rate);
        float[] both = new float[bass.Length];
        for (int i = 0; i < both.Length; i++)
        {
            both[i] = bass[i] + treble[i];
        }

        new AudioEffectChain(new AudioEffectsSettings(null, settings), Rate).Process(both);
        double low = Tone(both, 80, Rate / 2, Rate / 2) / 0.4;
        double high = Tone(both, 8000, Rate / 2, Rate / 2) / 0.4;
        output.WriteLine($"80 Hz y 8 kHz a −8 dBFS, solo los graves sobre su umbral: graves {Db(low):0.00} dB, agudos {Db(high):0.00} dB");
        Assert.True(Db(low) < -6);
        Assert.Equal(0, Db(high), 0.1);
    }

    [Fact]
    public void True_peak_limiter_never_passes_the_ceiling()
    {
        var limiter = new CompressorSettings(0, 1, 1, 50, 0, 0) { CeilingDb = -1 };
        var signals = new (string Name, float[] Samples)[]
        {
            ("fs/4 a 45°, +6 dBTP", Phased(Rate / 4, 2.0, Math.PI / 4)),
            ("fs/4 a 45°, 0 dBTP", Phased(Rate / 4, 1.0, Math.PI / 4)),
            ("cuadrada de 1 kHz a 0 dBFS", Square(1000, 1.0)),
            ("ruido a +6 dBFS", Noise(2.0)),
            ("impulsos sueltos", Impulses()),
            ("barrido 20 Hz–20 kHz a +3 dBFS", Sweep(1.41)),
        };

        double worst = double.NegativeInfinity;
        foreach ((string name, float[] samples) in signals)
        {
            float[] padded = new float[samples.Length + (2 * 4800)];
            samples.CopyTo(padded, 0);
            new AudioEffectChain(new AudioEffectsSettings(null, limiter), Rate).Process(padded);
            double tp = TruePeakDb(padded);
            worst = Math.Max(worst, tp);
            output.WriteLine($"{name}: pico verdadero de salida {tp:0.000} dBTP");
        }

        Assert.True(worst <= -1.0, $"{worst} dBTP");

        float[] quiet = Sine(997, 0.5, 0, Rate / 4);
        float[] limited = (float[])quiet.Clone();
        new AudioEffectChain(new AudioEffectsSettings(null, limiter), Rate).Process(limited);
        for (int i = 0; i < quiet.Length - (2 * limiter.LatencyFrames(Rate)); i++)
        {
            Assert.Equal(quiet[i], limited[i + (2 * limiter.LatencyFrames(Rate))]);
        }
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void A_full_block_costs_a_fraction_of_real_time_and_allocates_nothing()
    {
        var full = new CompressorSettings(-24, 4, 5, 150, 3, 6)
        {
            Detector = LevelDetector.Rms,
            AutoRelease = true,
            AutoMakeup = true,
            LookaheadMs = 10,
            Mix = 0.8,
            SidechainHighPassHz = 100,
            Multiband = new MultibandSettings(200, 3000, -3, 0, 2, 1, 0, -1),
            CeilingDb = -1,
        };
        var cases = new (string Name, AudioEffectsSettings Settings)[]
        {
            ("una banda, pico", new(null, new CompressorSettings(-24, 4, 5, 150, 6, 6))),
            ("una banda, anticipación 10 ms y limitador", new(null, new CompressorSettings(-24, 4, 5, 150, 6, 6) { LookaheadMs = 10, CeilingDb = -1 })),
            ("limitador con la señal siempre por encima", new(null, new CompressorSettings(0, 1, 5, 150, 12, 0) { CeilingDb = -1 })),
            ("multibanda sin limitador", new(null, full with { CeilingDb = null })),
            ("multibanda completo", new(null, full)),
            ("5 bandas de ecualizador y multibanda completo", new(new EqualizerSettings(80, 2, 120, -3, 400, 1.2, 4, 3500, 1.5, 2.5, 10000), full)),
        };

        float[] block = Sine(440, 0.5, 0, 1024);
        float[] key = Sine(1000, 0.5, 0, 1024);

        // El criterio vale para código optimizado (Release y Native AOT); en Debug solo se exige quedar lejos de los 21 ms del bloque.
        bool optimized = typeof(AudioEffectChain).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            .Cast<System.Diagnostics.DebuggableAttribute>().All(a => !a.IsJITOptimizerDisabled);
        double limit = optimized ? 1 : 8;
        foreach ((string name, AudioEffectsSettings settings) in cases)
        {
            var chain = new AudioEffectChain(settings, Rate);

            // Hasta que el JIT por niveles deje el código optimizado, como lo deja Native AOT desde el principio.
            var warmUp = System.Diagnostics.Stopwatch.StartNew();
            for (int warm = 0; warm < 20 || warmUp.ElapsedMilliseconds < 800; warm++)
            {
                chain.Process(block, key);
            }

            // El mejor de cinco tandas: la máquina puede estar ocupada con otros procesos.
            double best = double.PositiveInfinity;
            // La tanda que menos reserva: una reserva fija sale en todas, y con otras pruebas en paralelo una tanda suelta llegó a
            // contar 8160 B que no se repiten (no salen en 20 000 lecturas seguidas).
            long allocated = long.MaxValue;
            for (int run = 0; run < 5; run++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++)
                {
                    chain.Process(block, key);
                }

                allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds / 100);
            }

            output.WriteLine($"Bloque de 1024 cuadros, {name}: {best:0.000} ms ({1024.0 / Rate * 1000 / best:0}× tiempo real{(optimized ? "" : ", Debug")}), {allocated} bytes reservados");
            Assert.Equal(0, allocated);
            Assert.True(best < limit, $"{best} ms (límite {limit})");
        }
    }

    private static double Recovery(CompressorSettings settings, int loudFrames)
    {
        var chain = new AudioEffectChain(new AudioEffectsSettings(null, settings), Rate);
        chain.Process(Constant(0.316f, loudFrames));
        float[] quiet = Constant(0.01f, Rate * 4);
        chain.Process(quiet);
        for (int i = 0; i < quiet.Length / 2; i++)
        {
            if (Db(0.01) - Db(quiet[2 * i]) < 1)
            {
                return i * 1000.0 / Rate;
            }
        }

        return double.PositiveInfinity;
    }

    private static double SettledReduction(CompressorSettings settings, float[] signal)
    {
        float[] copy = (float[])signal.Clone();
        new AudioEffectChain(new AudioEffectsSettings(null, settings), Rate).Process(copy);
        return Db(Peak(signal, Rate / 2, Rate / 4)) - Db(Peak(copy, Rate / 2, Rate / 4));
    }

    private static double Reference(double x, double threshold, double ratio, double knee)
    {
        double slope = ratio >= CompressorSettings.InfiniteRatio ? 0 : 1 / ratio;
        double over = x - threshold;
        if (over <= -knee / 2)
        {
            return x;
        }

        if (over >= knee / 2)
        {
            return threshold + (over * slope);
        }

        return x + ((slope - 1) * Math.Pow(over + (knee / 2), 2) / (2 * knee));
    }

    private static double TruePeakDb(float[] interleaved)
    {
        var left = new TruePeakDetector();
        var right = new TruePeakDetector();
        return Db(Math.Max(left.Process(interleaved, 0, 2, interleaved.Length / 2), right.Process(interleaved, 1, 2, interleaved.Length / 2)));
    }

    private static double Db(double linear) => 20 * Math.Log10(Math.Max(Math.Abs(linear), 1e-12));

    private static float Peak(float[] interleaved, int firstFrame, int frames)
    {
        float peak = 0;
        for (int i = firstFrame * 2; i < (firstFrame + frames) * 2; i++)
        {
            peak = Math.Max(peak, Math.Abs(interleaved[i]));
        }

        return peak;
    }

    private static double Rms(float[] interleaved, int firstFrame, int frames)
    {
        double sum = 0;
        for (int i = firstFrame; i < firstFrame + frames; i++)
        {
            sum += interleaved[2 * i] * (double)interleaved[2 * i];
        }

        return Math.Sqrt(sum / frames);
    }

    /// <summary>Amplitud del tono <paramref name="hz"/> en el canal izquierdo (correlación con seno y coseno).</summary>
    private static double Tone(float[] interleaved, double hz, int firstFrame, int frames)
    {
        double s = 0, c = 0;
        for (int i = firstFrame; i < firstFrame + frames; i++)
        {
            double w = 2 * Math.PI * hz * i / Rate;
            s += interleaved[2 * i] * Math.Sin(w);
            c += interleaved[2 * i] * Math.Cos(w);
        }

        return 2 * Math.Sqrt((s * s) + (c * c)) / frames;
    }

    private static float[] Step(int frames, int at, double beforeDb, double afterDb)
    {
        float[] signal = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            double db = i < at ? beforeDb : afterDb;
            signal[2 * i] = signal[(2 * i) + 1] = (float)Math.Pow(10, db / 20);
        }

        return signal;
    }

    private static float[] Sine(double hz, double amplitude, int first, int frames)
    {
        float[] signal = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            signal[2 * i] = signal[(2 * i) + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * (first + i) / Rate));
        }

        return signal;
    }

    private static float[] Phased(double hz, double amplitude, double phase)
    {
        float[] signal = new float[Rate];
        for (int i = 0; i < Rate / 2; i++)
        {
            signal[2 * i] = signal[(2 * i) + 1] = (float)(amplitude * Math.Sin((2 * Math.PI * hz * i / Rate) + phase));
        }

        return signal;
    }

    private static float[] Square(double hz, double amplitude)
    {
        float[] signal = new float[Rate];
        for (int i = 0; i < Rate / 2; i++)
        {
            signal[2 * i] = signal[(2 * i) + 1] = (float)(Math.Sin(2 * Math.PI * hz * i / Rate) >= 0 ? amplitude : -amplitude);
        }

        return signal;
    }

    private static float[] Noise(double amplitude)
    {
        var random = new Random(7);
        float[] signal = new float[Rate];
        for (int i = 0; i < signal.Length; i++)
        {
            signal[i] = (float)(amplitude * ((random.NextDouble() * 2) - 1));
        }

        return signal;
    }

    private static float[] Impulses()
    {
        float[] signal = new float[Rate];
        for (int i = 1000; i < signal.Length; i += 4801)
        {
            signal[i] = i % 2 == 0 ? 1.5f : -1.5f;
        }

        return signal;
    }

    private static float[] Sweep(double amplitude)
    {
        float[] signal = new float[Rate * 2];
        double phase = 0;
        for (int i = 0; i < Rate; i++)
        {
            double hz = 20 * Math.Pow(1000, (double)i / Rate);
            phase += 2 * Math.PI * hz / Rate;
            signal[2 * i] = signal[(2 * i) + 1] = (float)(amplitude * Math.Sin(phase));
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
