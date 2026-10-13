using Rodaje.Audio.Processing;
using Xunit.Abstractions;

namespace Rodaje.Audio.Tests.Processing;

/// <summary>Los efectos de inserción nuevos contra sus referencias: la teoría de cada uno, medida sobre su salida.</summary>
public sealed class InsertEffectsTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    [Fact]
    public void Pan_follows_the_minus_3_db_law_and_balance_only_lowers_the_other_side()
    {
        double worst = 0;
        for (int i = -10; i <= 10; i++)
        {
            double pan = i / 10.0;
            (double left, double right) = PanLaw.MonoGains(pan);
            double theta = (pan + 1) * Math.PI / 4;
            worst = Math.Max(worst, Math.Max(Math.Abs(left - (Math.Sqrt(2) * Math.Cos(theta))), Math.Abs(right - (Math.Sqrt(2) * Math.Sin(theta)))));

            // Potencia constante: L² + R² = 2 (el centro en 0 dB por canal).
            Assert.Equal(2, (left * left) + (right * right), 5);
        }

        (double l0, double r0) = PanLaw.MonoGains(0);
        (double lHard, _) = PanLaw.MonoGains(-1);
        output.WriteLine($"Error de la ley de −3 dB en 21 posiciones: {worst:E2}; centro {20 * Math.Log10(l0):0.000} dB, a fondo {20 * Math.Log10(lHard):0.000} dB");
        Assert.True(worst < 1e-6);
        Assert.Equal(PanMatrix.Identity, PanLaw.Matrix(0, 0));
        Assert.Equal(1, l0, 6);
        Assert.Equal(1, r0, 6);

        PanMatrix balance = PanLaw.Matrix(0, 0.5);
        Assert.Equal(new PanMatrix(0.5f, 0, 0, 1), balance);

        // En estéreo, paneado a fondo a la izquierda: el canal derecho entero pasa a la izquierda con la ley.
        PanMatrix hard = PanLaw.Matrix(-1, 0);
        Assert.Equal(1, hard.LeftToLeft, 6);
        Assert.Equal(Math.Sqrt(2) - 1, hard.RightToLeft, 6);
        Assert.Equal(0, hard.RightToRight, 6);
    }

    [Fact]
    public void Gate_static_curve_matches_the_expander_reference()
    {
        var settings = new GateSettings(-40, 30, 2, 1, 0, 20) { HysteresisDb = 0 };
        double worst = 0;
        for (double input = -80; input <= -10; input += 2)
        {
            var stage = (GateStage)((AudioProcessorSettings)settings).CreateStageForTest();
            float[] signal = Constant((float)Math.Pow(10, input / 20), Rate / 2);
            stage.Process(signal, default);
            double gain = Db(signal[^1]) - input;
            worst = Math.Max(worst, Math.Abs(gain - settings.StaticGainDb(input)));
        }

        output.WriteLine($"Error de la curva estática del expansor (umbral −40, 1:2, rango 30 dB) de −80 a −10 dBFS: {worst:0.0000} dB");
        Assert.True(worst < 0.01, $"{worst} dB");
    }

    [Fact]
    public void Gate_holds_then_releases_and_opens_with_its_attack()
    {
        var settings = new GateSettings(-40, 80, GateSettings.GateRatio, 5, 50, 100) { HysteresisDb = 3 };
        IAudioStage stage = ((AudioProcessorSettings)settings).CreateStageForTest();
        stage.Reset();

        // Cerrada (−80 dB): un escalón a −20 dBFS abre con el ataque de 5 ms (63 % a los 5 ms).
        float[] loud = Constant(0.1f, Rate / 2);
        stage.Process(loud, default);
        double atAttack = Db(loud[2 * (Rate / 200)]) + 20;
        double expectedAttack = -80 * Math.Exp(-1);

        // Al bajar a −60 dBFS el detector cruza −43 dB a los 10·ln(10^(23/20)) ms, retiene 50 ms y suelta con 100 ms.
        float[] quiet = Constant(0.001f, Rate / 2);
        stage.Process(quiet, default);
        int below = Array.FindIndex(Enumerable.Range(0, quiet.Length / 2).ToArray(), i => Db(quiet[2 * i]) + 60 < -1);
        double measured = below * 1000.0 / Rate;
        double expected = (10 * Math.Log(Math.Pow(10, 23.0 / 20))) + 50 + (-100 * Math.Log(1 - (1.0 / 80)));
        output.WriteLine($"Ataque: {atAttack:0.00} dB a los 5 ms (teoría {expectedAttack:0.00}); cierre a −1 dB: {measured:0.00} ms (teoría {expected:0.00})");
        Assert.Equal(expectedAttack, atAttack, 0.3);
        Assert.Equal(expected, measured, 0.5);
    }

    [Fact]
    public void DeEsser_lowers_sibilance_and_leaves_the_voice()
    {
        var settings = new DeEsserSettings(6000, -30, 10);
        float[] voice = Sum(Sine(300, 0.3, Rate), Sine(7000, 0.3, Rate));
        float[] processed = (float[])voice.Clone();
        ((AudioProcessorSettings)settings).CreateStageForTest().Process(processed, default);
        int from = Rate / 2;
        double voiceChange = Tone(processed, 300, from) - Tone(voice, 300, from);
        double sibilanceChange = Tone(processed, 7000, from) - Tone(voice, 7000, from);

        // Sin eses no toca nada: la banda alta y la baja suman la señal exacta.
        float[] clean = Sine(300, 0.3, Rate);
        float[] untouched = (float[])clean.Clone();
        ((AudioProcessorSettings)settings).CreateStageForTest().Process(untouched, default);
        output.WriteLine($"De-esser en 6 kHz, umbral −30, 10 dB: 300 Hz {voiceChange:+0.000;-0.000} dB, 7 kHz {sibilanceChange:+0.00;-0.00} dB");
        Assert.True(Math.Abs(voiceChange) < 0.3, $"{voiceChange}");
        Assert.True(sibilanceChange < -6, $"{sibilanceChange}");
        Assert.Equal(clean, untouched);
    }

    [Fact]
    public void Limiter_never_passes_its_ceiling_and_keeps_exact_latency()
    {
        var settings = new LimiterSettings(-1, 12, 60);
        float[] loud = Sine(997, 0.5, Rate);
        ((AudioProcessorSettings)settings).CreateStageForTest().Process(loud, default);
        float peak = loud.Max(MathF.Abs);
        output.WriteLine($"Limitador −1 dB con +12 dB a un seno de −6 dBFS: pico de muestras {Db(peak):0.000} dBFS");
        Assert.True(Db(peak) <= -0.99, $"{Db(peak)}");

        // Por debajo del techo sale igual, con el retraso de la etapa.
        float[] quiet = Sine(440, 0.1, Rate / 10);
        float[] limited = (float[])quiet.Clone();
        ((AudioProcessorSettings)new LimiterSettings(-1, 0, 60)).CreateStageForTest().Process(limited, default);
        int latency = TruePeakLimiter.Latency;
        for (int i = latency * 2; i < limited.Length; i++)
        {
            Assert.Equal(quiet[i - (latency * 2)], limited[i], 5);
        }
    }

    [Fact]
    public void Graphic_equalizer_matches_its_transfer_function()
    {
        var single = new GraphicEqualizerSettings(0, 0, 0, 0, 0, 6, 0, 0, 0, 0);
        Assert.Equal(6, Measure(single, 1000), 0.02);
        var curve = new GraphicEqualizerSettings(4, -3, 2, 0, -2, 1, 3, -4, 2, 5) { OutputDb = -1 };
        double worst = 0;
        for (int i = 0; i <= 29; i++)
        {
            double hz = 25 * Math.Pow(18000.0 / 25, i / 29.0);
            worst = Math.Max(worst, Math.Abs(Measure(curve, hz) - curve.MagnitudeDb(Rate, hz)));
        }

        output.WriteLine($"Gráfico de 10 bandas: +6 en 1 kHz mide {Measure(single, 1000):0.000} dB; error frente a la teoría en 30 frecuencias {worst:0.0000} dB");
        Assert.True(worst < 0.05, $"{worst}");
    }

    [Fact]
    public void Saturation_is_transparent_when_quiet_and_folds_far_less_than_plain_tanh()
    {
        var settings = new SaturationSettings(24, 0, 20000, 0, 1);
        float[] quiet = Sine(1000, 0.001, Rate);
        float[] processed = (float[])quiet.Clone();
        ((AudioProcessorSettings)settings).CreateStageForTest().Process(processed, default);
        double smallSignal = Tone(processed, 1000, Rate / 2) - Tone(quiet, 1000, Rate / 2);

        // Un seno de 7919 Hz (primo: sus armónicos plegados no caen en armónicos) casi a fondo, con 24 dB de empuje.
        const double f0 = 7919;
        float[] loud = Sine(f0, 0.9, Rate);
        float[] adaa = (float[])loud.Clone();
        ((AudioProcessorSettings)settings).CreateStageForTest().Process(adaa, default);
        float[] naive = loud.Select(v => (float)(Math.Tanh(v * Math.Pow(10, 24 / 20.0)) / Math.Pow(10, 24 / 20.0))).ToArray();
        double aliasAdaa = AliasDb(adaa, f0);
        double aliasNaive = AliasDb(naive, f0);
        output.WriteLine($"Saturación: a −60 dBFS cambia {smallSignal:+0.000;-0.000} dB; plegado bajo 7 kHz respecto del fundamental: ADAA {aliasAdaa:0.0} dB, tanh directa {aliasNaive:0.0} dB");
        Assert.True(Math.Abs(smallSignal) < 0.01);
        Assert.True(aliasAdaa < aliasNaive - 6, $"{aliasAdaa} frente a {aliasNaive}");
    }

    [Fact]
    public void Delay_repeats_at_the_exact_time_with_its_feedback()
    {
        var settings = new DelaySettings(250, 0.5, 1);
        float[] impulse = new float[2 * Rate];
        impulse[0] = 1;
        impulse[1] = 1;
        ((AudioProcessorSettings)settings).CreateStageForTest().Process(impulse, default);
        int d = 12_000;
        for (int k = 1; k <= 3; k++)
        {
            Assert.Equal(Math.Pow(0.5, k - 1), impulse[2 * k * d], 6);
            Assert.Equal(0, impulse[(2 * k * d) - 2]);
        }

        Assert.Equal(24_000, new DelaySettings(100, 0.3, 0.3) { Sync = true, Tempo = 120, Division = DelayDivision.Quarter }.DelayFrames(Rate));
        Assert.Equal(18_000, new DelaySettings(100, 0.3, 0.3) { Sync = true, Tempo = 120, Division = DelayDivision.DottedEighth }.DelayFrames(Rate));

        float[] pingPong = new float[2 * Rate];
        pingPong[0] = 1;
        pingPong[1] = 1;
        ((AudioProcessorSettings)(settings with { PingPong = true })).CreateStageForTest().Process(pingPong, default);
        output.WriteLine($"Delay 250 ms, 50 %: repeticiones 1/0,5/0,25 en las muestras 12 000, 24 000 y 36 000; ping-pong L {pingPong[2 * d]:0.00} R {pingPong[(2 * d) + 1]:0.00}, luego L {pingPong[4 * d]:0.00} R {pingPong[(4 * d) + 1]:0.00}");
        Assert.Equal(1, pingPong[2 * d], 6);
        Assert.Equal(0, pingPong[(2 * d) + 1]);
        Assert.Equal(0, pingPong[4 * d]);
        Assert.Equal(0.5, pingPong[(4 * d) + 1], 6);
    }

    [Fact]
    public void Reverb_decays_with_the_requested_rt60_and_mix_zero_is_dry()
    {
        foreach (double decay in new[] { 1.0, 2.5 })
        {
            var settings = new ReverbSettings(0, 0.5, decay, 20000, 1, 0, 1);
            float[] response = new float[(int)(decay * 2.2 * Rate) * 2];
            response[0] = 1;
            response[1] = 1;
            ((AudioProcessorSettings)settings).CreateStageForTest().Process(response, default);
            double rt60 = Rt60(response);
            double correlation = Correlation(response);
            output.WriteLine($"Reverb RT60 pedido {decay:0.0} s: medido {rt60:0.000} s (T30, Schroeder); correlación L/R de la cola {correlation:0.000}");
            Assert.Equal(decay, rt60, decay * 0.08);
            Assert.True(Math.Abs(correlation) < 0.3);
        }

        float[] music = Sine(440, 0.5, Rate / 4);
        float[] dry = (float[])music.Clone();
        ((AudioProcessorSettings)new ReverbSettings(20, 0.6, 2, 6000, 1, 100, 0)).CreateStageForTest().Process(dry, default);
        Assert.Equal(music, dry);
    }

    [Fact]
    public void Pitch_shift_moves_the_pitch_keeps_the_timing_and_is_exact_at_unison()
    {
        foreach ((double semitones, double expected) in new[] { (7.0, 440 * Math.Pow(2, 7.0 / 12)), (-12.0, 220.0), (12.0, 880.0), (-5.0, 440 * Math.Pow(2, -5.0 / 12)) })
        {
            float[] tone = Sine(440, 0.5, 2 * Rate);
            float[] shifted = (float[])tone.Clone();
            ((AudioProcessorSettings)new PitchShiftSettings(semitones, 0, 1)).CreateStageForTest().Process(shifted, default);
            double hz = Frequency(shifted, Rate / 2, Rate);
            double level = Rms(shifted, Rate / 2, Rate) / Rms(tone, Rate / 2, Rate);
            double thd = ThdN(shifted, expected, Rate / 2, Rate);
            output.WriteLine($"Tono {semitones:+0;-0} semitonos: {hz:0.000} Hz (pedido {expected:0.000}), nivel {20 * Math.Log10(level):+0.00;-0.00} dB, THD+N {thd:0.0} dB");
            Assert.Equal(expected, hz, 0.05);
            Assert.True(Math.Abs(20 * Math.Log10(level)) < 1);
            Assert.True(thd < -35, $"THD+N {thd} dB");
        }

        // A razón 1 sale la entrada retrasada lo que dice la etapa.
        float[] music = Sum(Sine(220, 0.3, Rate / 2), Sine(1234, 0.2, Rate / 2));
        float[] same = (float[])music.Clone();
        ((AudioProcessorSettings)new PitchShiftSettings(0, 0, 1)).CreateStageForTest().Process(same, default);
        int latency = PitchShiftStage.Latency48k;
        double error = 0;
        for (int i = (latency + PitchShiftStage.Grain) * 2; i < same.Length; i++)
        {
            error = Math.Max(error, Math.Abs(same[i] - music[i - (latency * 2)]));
        }

        // Un tono modulado en amplitud a 4 Hz: la envolvente sale donde entró (retraso compensado).
        float[] modulated = new float[3 * Rate * 2];
        for (int i = 0; i < modulated.Length / 2; i++)
        {
            float v = (float)(0.4 * (0.6 + (0.4 * Math.Sin(2 * Math.PI * 4 * i / Rate))) * Math.Sin(2 * Math.PI * 330 * i / Rate));
            modulated[2 * i] = v;
            modulated[(2 * i) + 1] = v;
        }

        float[] up = (float[])modulated.Clone();
        ((AudioProcessorSettings)new PitchShiftSettings(5, 0, 1)).CreateStageForTest().Process(up, default);
        double lagMs = EnvelopeLag(modulated, up, latency) * 1000.0 / Rate;
        output.WriteLine($"Unísono: error máximo frente a la entrada retrasada {latency} muestras: {error:E2}; envolvente a +5 semitonos desplazada {lagMs:+0.0;-0.0} ms");
        Assert.True(error < 1e-5, $"{error}");
        Assert.True(Math.Abs(lagMs) < 6, $"{lagMs} ms");
    }

    [Fact]
    public void Every_stage_restores_its_state_exactly()
    {
        AudioProcessorSettings[] all =
        [
            new GateSettings(-40, 40, 4, 2, 30, 80) { SidechainHighPassHz = 100 },
            new DeEsserSettings(6000, -35, 8),
            new ReverbSettings(30, 0.7, 1.5, 7000, 0.8, 120, 0.4),
            new DelaySettings(180, 0.6, 0.5) { PingPong = true, LowCutHz = 200, HighCutHz = 6000 },
            new SaturationSettings(18, 0.6, 9000, -2, 0.8),
            new GraphicEqualizerSettings(3, 0, -2, 0, 4, 0, -3, 0, 2, 0),
            new PitchShiftSettings(-3, 20, 0.7),
            new LimiterSettings(-3, 6, 100),
        ];

        foreach (AudioProcessorSettings settings in all)
        {
            IAudioStage stage = settings.CreateStageForTest();
            stage.Reset();
            float[] first = Noise(Rate / 2, 1);
            stage.Process(first, default);
            double[] saved = new double[stage.StateLength];
            stage.SaveState(saved);
            float[] a = Noise(Rate / 4, 2);
            float[] b = (float[])a.Clone();
            stage.Process(a, default);
            stage.LoadState(saved);
            stage.Process(b, default);
            Assert.True(a.SequenceEqual(b), $"{settings.GetType().Name} no repite lo mismo tras restaurar");
        }

        output.WriteLine($"{all.Length} etapas repiten bit a bit tras restaurar su estado");
    }

    [Fact]
    public void Chain_keeps_a_reverb_tail_when_reordered_and_bypass_stays_aligned()
    {
        var reverb = new AudioInsert("rev", new ReverbSettings(0, 0.5, 2, 20000, 1, 0, 1));
        var eq = new AudioInsert("geq", new GraphicEqualizerSettings(0, 0, 0, 0, 0, 3, 0, 0, 0, 0));
        var chain = new AudioEffectChain(new AudioEffectsSettings([eq, reverb]), Rate);
        float[] burst = Noise(Rate / 10, 3);
        chain.Process(burst);
        chain.Configure(new AudioEffectsSettings([reverb, eq]));
        float[] tail = new float[Rate / 10 * 2];
        chain.Process(tail);
        double tailRms = Rms(tail, 0, tail.Length / 2);
        output.WriteLine($"Cola de la reverb tras reordenar la cadena: {20 * Math.Log10(tailRms):0.0} dBFS (sigue sonando)");
        Assert.True(tailRms > 1e-3);

        var limiter = new AudioInsert("lim", new LimiterSettings(-1, 0, 60));
        var bypassed = new AudioEffectChain(new AudioEffectsSettings([limiter]), Rate) { BypassedInserts = new HashSet<string> { "lim" } };
        float[] loud = Sine(500, 0.99, Rate / 10);
        float[] heard = (float[])loud.Clone();
        bypassed.Process(heard);
        int latency = TruePeakLimiter.Latency;
        for (int i = latency * 2; i < heard.Length; i++)
        {
            Assert.Equal(loud[i - (latency * 2)], heard[i]);
        }
    }

    [Fact]
    public void A_block_with_every_new_effect_costs_little_and_allocates_nothing()
    {
        var chain = new AudioEffectChain(new AudioEffectsSettings(
        [
            new AudioInsert("1", new GateSettings(-50, 40, 4, 1, 30, 100)),
            new AudioInsert("2", new DeEsserSettings(6500, -30, 8)),
            new AudioInsert("3", new GraphicEqualizerSettings(2, 1, 0, -1, 0, 1, 2, 3, 1, 0)),
            new AudioInsert("4", new SaturationSettings(9, 0.4, 12000, 0, 0.7)),
            new AudioInsert("5", new PitchShiftSettings(3, 0, 0.5)),
            new AudioInsert("6", new DelaySettings(300, 0.4, 0.3) { PingPong = true, LowCutHz = 150, HighCutHz = 8000 }),
            new AudioInsert("7", new ReverbSettings(20, 0.6, 1.8, 6000, 1, 100, 0.3)),
            new AudioInsert("8", new LimiterSettings(-1, 3, 60)),
        ]), Rate);
        float[] block = Noise(1024, 4);
        var warmUp = System.Diagnostics.Stopwatch.StartNew();
        while (warmUp.ElapsedMilliseconds < 800)
        {
            chain.Process(block);
        }

        bool optimized = typeof(AudioEffectChain).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            .Cast<System.Diagnostics.DebuggableAttribute>().All(a => !a.IsJITOptimizerDisabled);
        double best = double.PositiveInfinity;
        long allocated = 0;
        for (int run = 0; run < 5; run++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++)
            {
                chain.Process(block);
            }

            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds / 50);
        }

        output.WriteLine($"Bloque de 1024 cuadros con puerta, de-esser, gráfico, saturación, tono, delay, reverb y limitador: {best:0.000} ms ({1024.0 / Rate * 1000 / best:0}× tiempo real{(optimized ? "" : ", Debug")}), {allocated} bytes reservados");
        Assert.Equal(0, allocated);
        // La cota de tiempo vale para código optimizado; en Debug, con la máquina cargada, solo se informa.
        Assert.True(!optimized || best < 2, $"{best} ms");
    }

    private static double Measure(GraphicEqualizerSettings settings, double hz)
    {
        IAudioStage stage = ((AudioProcessorSettings)settings).CreateStageForTest();
        float[] tone = Sine(hz, 0.25, Rate);
        stage.Process(tone, default);
        float[] reference = Sine(hz, 0.25, Rate);
        return Tone(tone, hz, Rate / 2) - Tone(reference, hz, Rate / 2);
    }

    /// <summary>Nivel en dB del tono <paramref name="hz"/> del canal izquierdo desde <paramref name="from"/> (Goertzel con Hann).</summary>
    private static double Tone(float[] interleaved, double hz, int from)
    {
        int n = (interleaved.Length / 2) - from;
        double re = 0, im = 0;
        for (int i = 0; i < n; i++)
        {
            double w = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / n));
            double v = interleaved[2 * (from + i)] * w;
            double angle = 2 * Math.PI * hz * i / Rate;
            re += v * Math.Cos(angle);
            im -= v * Math.Sin(angle);
        }

        return 20 * Math.Log10(Math.Sqrt((re * re) + (im * im)) + 1e-20);
    }

    /// <summary>Energía por debajo de 7 kHz (donde solo puede haber plegado) respecto del fundamental, en dB.</summary>
    private static double AliasDb(float[] interleaved, double fundamental)
    {
        const int n = 8192;
        int from = interleaved.Length / 2 / 2;
        double alias = 0;
        for (int bin = 2; bin < 7000 * n / Rate; bin++)
        {
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double w = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / n));
                double v = interleaved[2 * (from + i)] * w;
                double angle = 2 * Math.PI * bin * i / n;
                re += v * Math.Cos(angle);
                im -= v * Math.Sin(angle);
            }

            alias += (re * re) + (im * im);
        }

        double fundamentalDb = Tone(interleaved[(2 * from)..(2 * (from + n))], fundamental, 0);
        return (10 * Math.Log10(alias + 1e-30)) - fundamentalDb;
    }

    /// <summary>RT60 con la integral de Schroeder (canales sumados): recta entre −5 y −35 dB, extrapolada a 60.</summary>
    private static double Rt60(float[] interleaved)
    {
        int n = interleaved.Length / 2;
        double[] energy = new double[n];
        double sum = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            double v = interleaved[2 * i] + interleaved[(2 * i) + 1];
            sum += v * v;
            energy[i] = sum;
        }

        double total = energy[0];
        int start = Array.FindIndex(energy, e => 10 * Math.Log10(e / total) <= -5);
        int end = Array.FindIndex(energy, e => 10 * Math.Log10(e / total) <= -35);
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int count = end - start;
        for (int i = start; i < end; i++)
        {
            double x = (double)i / Rate;
            double y = 10 * Math.Log10(energy[i] / total);
            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
        }

        double slope = ((count * sxy) - (sx * sy)) / ((count * sxx) - (sx * sx));
        return -60 / slope;
    }

    private static double Correlation(float[] interleaved)
    {
        double lr = 0, ll = 0, rr = 0;
        for (int i = Rate / 10; i < interleaved.Length / 2; i++)
        {
            double l = interleaved[2 * i], r = interleaved[(2 * i) + 1];
            lr += l * r;
            ll += l * l;
            rr += r * r;
        }

        return lr / Math.Sqrt(ll * rr);
    }

    /// <summary>Frecuencia por los cruces por cero ascendentes interpolados del canal izquierdo.</summary>
    private static double Frequency(float[] interleaved, int from, int count)
    {
        double first = double.NaN, last = 0;
        int crossings = 0;
        for (int i = from + 1; i < from + count; i++)
        {
            float a = interleaved[2 * (i - 1)], b = interleaved[2 * i];
            if (a < 0 && b >= 0)
            {
                double at = i - 1 + (a / (a - b));
                if (double.IsNaN(first))
                {
                    first = at;
                }
                else
                {
                    crossings++;
                }

                last = at;
            }
        }

        return crossings * (double)Rate / (last - first);
    }

    /// <summary>Lo que no es el seno de <paramref name="hz"/> (ajustado por mínimos cuadrados) respecto de él, en dB.</summary>
    private static double ThdN(float[] interleaved, double hz, int from, int count)
    {
        double ss = 0, sc = 0, cc = 0, ys = 0, yc = 0, yy = 0;
        for (int i = from; i < from + count; i++)
        {
            double angle = 2 * Math.PI * hz * i / Rate;
            double si = Math.Sin(angle), co = Math.Cos(angle), y = interleaved[2 * i];
            ss += si * si;
            sc += si * co;
            cc += co * co;
            ys += y * si;
            yc += y * co;
            yy += y * y;
        }

        double det = (ss * cc) - (sc * sc);
        double a = ((ys * cc) - (yc * sc)) / det;
        double b = ((yc * ss) - (ys * sc)) / det;
        double fitted = (a * ys) + (b * yc);
        return 10 * Math.Log10(Math.Max(1e-20, yy - fitted) / fitted);
    }

    private static double Rms(float[] interleaved, int from, int count)
    {
        double sum = 0;
        for (int i = from; i < from + count; i++)
        {
            sum += interleaved[2 * i] * interleaved[2 * i];
        }

        return Math.Sqrt(sum / count);
    }

    /// <summary>Desplazamiento (en muestras) de la envolvente de <paramref name="shifted"/> respecto de la de <paramref name="original"/>, ya quitado el retraso de la etapa.</summary>
    private static int EnvelopeLag(float[] original, float[] shifted, int latency)
    {
        double[] a = Envelope(original);
        double[] b = Envelope(shifted);
        int best = 0;
        double bestScore = double.NegativeInfinity;
        for (int lag = -960; lag <= 960; lag += 8)
        {
            double score = 0;
            for (int i = Rate; i < (2 * Rate) + Rate / 2; i += 4)
            {
                score += a[i] * b[i + latency + lag];
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }

        return best;
    }

    private static double[] Envelope(float[] interleaved)
    {
        int n = interleaved.Length / 2;
        double[] envelope = new double[n];
        double e = 0;
        double c = Math.Exp(-1 / (0.004 * Rate));
        for (int i = 0; i < n; i++)
        {
            e = Math.Max(Math.Abs(interleaved[2 * i]), e * c);
            envelope[i] = e;
        }

        double mean = envelope.Average();
        for (int i = 0; i < n; i++)
        {
            envelope[i] -= mean;
        }

        return envelope;
    }

    private static float[] Sine(double hz, double amplitude, int frames)
    {
        float[] samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
            samples[2 * i] = v;
            samples[(2 * i) + 1] = v;
        }

        return samples;
    }

    private static float[] Constant(float value, int frames)
    {
        float[] samples = new float[frames * 2];
        Array.Fill(samples, value);
        return samples;
    }

    private static float[] Noise(int frames, int seed)
    {
        var random = new Random(seed);
        float[] samples = new float[frames * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((random.NextDouble() * 2) - 1) * 0.5f;
        }

        return samples;
    }

    private static float[] Sum(float[] a, float[] b) => a.Zip(b, (x, y) => x + y).ToArray();

    private static double Db(double amplitude) => 20 * Math.Log10(Math.Abs(amplitude) + 1e-30);
}

internal static class StageTestExtensions
{
    public static IAudioStage CreateStageForTest(this AudioProcessorSettings settings)
    {
        IAudioStage stage = settings.CreateStage(48_000);
        stage.Reset();
        return stage;
    }
}
