using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Engine.Timeline.Retime;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.AudioSignals;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>Criterios de audio de la fase 5c: WSOLA, remuestreador y su paso por la mezcla.</summary>
public sealed class AudioRetimeTests(ITestOutputHelper output)
{
    public static TheoryData<int, int> PitchRates => new() { { 1, 2 }, { 3, 4 }, { 3, 2 }, { 2, 1 }, { 4, 1 } };

    public static TheoryData<int, int> QualityRates => new() { { 1, 2 }, { 3, 4 }, { 3, 2 }, { 2, 1 } };

    [Theory]
    [MemberData(nameof(PitchRates))]
    public void Keeping_pitch_a_440_Hz_tone_stays_at_440_Hz(int num, int den)
    {
        float[] left = Left(Render(Sine(440), new Rational(num, den), preservePitch: true, 0, 3 * Rate));

        double hz = Frequency(left.AsSpan(Rate / 2));

        output.WriteLine(Invariant($"MEDIDA tono_440_a_{num}/{den} = {hz:F3} Hz"));
        Assert.InRange(hz, 439.5, 440.5);
    }

    [Theory]
    [MemberData(nameof(QualityRates))]
    public void Keeping_pitch_the_thd_n_of_1_kHz_is_at_most_minus_35_dB(int num, int den)
    {
        // 997 Hz y no 1000: con un periodo de 48 muestras justas cualquier empalme cae en fase.
        float[] left = Left(Render(Sine(997), new Rational(num, den), preservePitch: true, 0, 3 * Rate));

        double thdN = ThdN(left.AsSpan(Rate / 2), 997);

        output.WriteLine(Invariant($"MEDIDA thdn_1k_a_{num}/{den} = {thdN:F1} dB"));
        Assert.True(thdN <= -35, Invariant($"THD+N de {thdN:F1} dB a {num}/{den}."));
    }

    [Theory]
    [InlineData(2, 1, 2000)]
    [InlineData(1, 2, 500)]
    [InlineData(3, 2, 1500)]
    public void Without_keeping_pitch_the_tone_moves_with_the_speed_and_aliasing_stays_under_minus_60_dB(int num, int den, double expected)
    {
        float[] left = Left(Render(Sine(1000), new Rational(num, den), preservePitch: false, 0, 2 * Rate));

        double hz = Frequency(left.AsSpan(Rate / 4));
        double spurious = ThdN(left.AsSpan(Rate / 4), expected);

        output.WriteLine(Invariant($"MEDIDA varispeed_1k_a_{num}/{den} = {hz:F3} Hz; espurias {spurious:F1} dB"));
        Assert.InRange(hz, expected - 1, expected + 1);
        Assert.True(spurious <= -60, Invariant($"Espurias de {spurious:F1} dB."));
    }

    [Fact]
    public void Without_keeping_pitch_content_above_the_new_nyquist_is_filtered_out()
    {
        // 18 kHz a 2× sería 36 kHz: se plegaría a 12 kHz si el filtro no bajara el corte.
        float[] left = Left(Render(Sine(18_000), new Rational(2, 1), preservePitch: false, 0, Rate));

        double level = Db(Rms(left.AsSpan(Rate / 4)) / (0.5 / Math.Sqrt(2)));

        output.WriteLine(Invariant($"MEDIDA varispeed_18k_a_2x = {level:F1} dB"));
        Assert.True(level <= -60, Invariant($"Lo plegado queda a {level:F1} dB."));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    [InlineData(2, 1)]
    public void Keeping_pitch_pink_noise_keeps_its_rms_within_half_a_db(int num, int den)
    {
        float[] noise = PinkNoise(7, 30 * Rate);
        float[] left = Left(Render(i => i >= 0 && i < noise.Length ? noise[i] : 0, new Rational(num, den), preservePitch: true, 0, 8 * Rate,
            anchor: S(1)));

        double input = Rms(noise.AsSpan(Rate, 10 * Rate));
        double change = Db(Rms(left.AsSpan(Rate / 2, 7 * Rate)) / input);

        output.WriteLine(Invariant($"MEDIDA rms_rosa_a_{num}/{den} = {change:+0.00;-0.00} dB"));
        Assert.InRange(change, -0.5, 0.5);
    }

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(3, 2, true)]
    [InlineData(2, 1, true)]
    [InlineData(4, 1, true)]
    [InlineData(8, 1, true)]
    [InlineData(3, 2, false)]
    [InlineData(2, 1, false)]
    public void There_are_no_clicks_the_largest_step_is_close_to_the_ideal_sine(int num, int den, bool preservePitch)
    {
        float[] left = Left(Render(Sine(440), new Rational(num, den), preservePitch, 0, 3 * Rate));
        double hz = preservePitch ? 440 : 440.0 * num / den;
        ReadOnlySpan<float> steady = left.AsSpan(Rate / 2);

        // La entrada es un seno de amplitud 0,5 y los dos caminos la conservan.
        double ratio = MaxStep(steady) / (2 * 0.5 * Math.Sin(Math.PI * hz / Rate));
        output.WriteLine(Invariant($"MEDIDA salto_max_a_{num}/{den}_{(preservePitch ? "tono" : "cinta")} = {ratio:F3} × el ideal"));
        Assert.True(ratio <= 1.1, Invariant($"El mayor salto es {ratio:F3} veces el del seno ideal."));
    }

    [Fact]
    public void A_click_train_at_1_5x_keeps_every_click_within_5_ms_and_does_not_drift()
    {
        const int Spacing = Rate / 10;
        const int Anchor = Rate;
        Func<long, float> clicks = i => i >= Anchor && (i - Anchor) % Spacing == 0 ? 1f : 0f;
        int frames = 60 * Rate;

        float[] left = Left(Render(clicks, new Rational(3, 2), preservePitch: true, 0, frames, anchor: S(1)));

        List<int> found = Peaks(left, 0.2f, Rate / 50);
        int expected = (int)Math.Ceiling(frames * 1.5 / Spacing);
        double worst = 0;
        double last = 0;
        for (int j = 0; j < Math.Min(found.Count, expected); j++)
        {
            double error = (found[j] - (j * Spacing / 1.5)) * 1000.0 / Rate;
            worst = Math.Max(worst, Math.Abs(error));
            last = error;
        }

        output.WriteLine(Invariant($"MEDIDA clics_1_5x = {found.Count} de {expected}; error máximo {worst:F2} ms; último {last:F2} ms"));
        Assert.Equal(expected, found.Count);
        Assert.True(worst <= 5, Invariant($"Un clic se movió {worst:F2} ms."));
    }

    /// <summary>
    /// Como la aceptación de deriva de 10 min, en corto: el chirp del inicio de cada segundo del origen tiene que sonar
    /// donde manda el mapeo, buscado con la plantilla del chirp tal como debe sonar.
    /// </summary>
    [Theory]
    [InlineData(3, 2, true)]
    [InlineData(1, 2, true)]
    [InlineData(3, 4, true)]
    [InlineData(1, 4, true)]
    [InlineData(3, 2, false)]
    [InlineData(1, 2, false)]
    public void Each_second_chirp_sounds_where_the_map_says(int num, int den, bool preservePitch)
    {
        double rate = (double)num / den;
        const int Seconds = 12;
        float[] template = RetimedAudioCode.ChirpTemplate(Rate, rate, preservePitch);
        var errors = new List<double>();
        var perPhase = new List<double>();
        for (int phase = 0; phase < 8; phase++)
        {
            double anchor = 0.5 + (phase * rate * 64 / Rate);
            float[] mono = Left(Render(i => i >= 0 ? AudioCode.SampleAt(i, Rate) : 0, new Rational(num, den), preservePitch, 0,
                (int)(Seconds / rate * Rate), anchor: Time.FromSamples((long)Math.Round(anchor * Rate), Rate)));
            var mine = new List<double>();
            for (int second = 1; second < Seconds; second++)
            {
                double expected = ((second * (double)Rate) - Math.Round(anchor * Rate)) / rate;
                if (expected + template.Length + (Rate / 20) >= mono.Length)
                {
                    break;
                }

                AudioMark? found = RetimedAudioCode.FindChirp(mono, template, (int)expected - (Rate / 20), (int)expected + (Rate / 20))
                    ?? RetimedAudioCode.FindChirp(mono, template, (int)expected - (Rate / 20), (int)expected + (Rate / 20), 0.5);
                Assert.True(found.HasValue, Invariant($"No se encontró el chirp del segundo {second} (fase {phase})."));
                mine.Add((found.Value.Sample - expected) * 1000.0 / Rate);
            }

            errors.AddRange(mine);
            perPhase.Add(mine.Average());
        }

        double mean = errors.Average();
        double max = errors.Max(Math.Abs);
        output.WriteLine(Invariant($"MEDIDA chirp_a_{num}/{den}_{(preservePitch ? "tono" : "cinta")} = error medio {mean:+0.000;-0.000} ms, máximo {max:0.000} ms, {errors.Count} marcas; por fase {string.Join(" ", perPhase.Select(p => p.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)))}"));
        Assert.True(Math.Abs(mean) <= 1, Invariant($"Error medio de {mean:0.000} ms."));
    }

    [Fact]
    public void After_a_seek_the_output_matches_continuous_playback_once_past_the_warm_up()
    {
        Func<long, float> voice = Syllables(3);
        var rate = new Rational(3, 2);
        float[] continuous = Left(Render(voice, rate, preservePitch: true, 0, 6 * Rate));
        double worstAtSeek = 1;
        double worstAfter = 1;
        foreach (double at in new[] { 2.0, 2.37, 3.11, 4.05 })
        {
            long from = (long)(at * Rate);
            float[] sought = Left(Render(voice, rate, preservePitch: true, from, Rate));
            worstAtSeek = Math.Min(worstAtSeek, Correlation(sought.AsSpan(0, Rate / 4), continuous.AsSpan((int)from, Rate / 4)));
            worstAfter = Math.Min(worstAfter, Correlation(sought.AsSpan(Rate / 4), continuous.AsSpan((int)from + (Rate / 4), 3 * Rate / 4)));
        }

        output.WriteLine(Invariant($"MEDIDA seek_correlacion = {worstAtSeek:F5} al saltar; {worstAfter:F5} pasados 250 ms"));
        Assert.True(worstAfter >= 0.999, Invariant($"Correlación de {worstAfter:F5} pasados 250 ms."));
    }

    [Fact]
    public void Mixing_again_what_was_just_heard_repeats_it_exactly()
    {
        var map = Map(new Rational(3, 2), preservePitch: true);
        var reader = new RetimedReader(new AudioRetime(map, true), new FunctionPcm(Sine(440), long.MaxValue / 4), -1);
        float[] first = Read(reader, 0, 3 * Rate);

        // Una edición rebobina la mezcla 50 ms: lo ya oído se vuelve a calcular con la historia de trozos.
        float[] again = Read(reader, (3 * Rate) - 2400, 2400);

        Assert.Equal(first.AsSpan((3 * Rate - 2400) * 2).ToArray(), again);
        Assert.Equal(reader.Wsola!.WarmUpSteps, (Wsola.WarmUp + reader.Wsola.Hop - 1) / reader.Wsola.Hop);
    }

    [Fact]
    public void Mixing_again_across_an_attack_repeats_it_exactly()
    {
        var map = Map(new Rational(1, 2), preservePitch: true, S(0.5));
        var reader = new RetimedReader(new AudioRetime(map, true), new FunctionPcm(i => i >= 0 ? AudioCode.SampleAt(i, Rate) : 0, long.MaxValue / 4), -1);
        float[] first = Read(reader, 0, 50_000);

        // El chirp del segundo 1 del origen suena en la muestra 48 000: se rebobina a justo antes de él.
        float[] again = Read(reader, 47_000, 3_000);

        Assert.Equal(first.AsSpan(47_000 * 2).ToArray(), again);
    }

    [Fact]
    public void Above_four_times_the_grains_are_2048_samples_and_above_eight_it_still_sounds()
    {
        var at4 = new RetimedReader(new AudioRetime(Map(new Rational(4, 1), true), true), new FunctionPcm(Sine(440), long.MaxValue / 4), -1);
        var at16 = new RetimedReader(new AudioRetime(Map(new Rational(16, 1), true), true), new FunctionPcm(Sine(440), long.MaxValue / 4), -1);

        float[] left = Left(Read(at16, 0, 2 * Rate));

        Assert.Equal(512, at4.Wsola!.Hop);
        Assert.Equal(1024, at16.Wsola!.Hop);
        Assert.InRange(Frequency(left.AsSpan(Rate / 2)), 439.5, 440.5);
        Assert.True(Rms(left) > 0.3, "A 16× el audio tiene que seguir sonando.");
    }

    [Fact]
    public void A_speed_curve_follows_the_map_without_clicks()
    {
        var curve = new SpeedCurve { Span = S(4), Points = [new SpeedPoint(0, 100), new SpeedPoint(5000, 300), new SpeedPoint(10_000, 50)] };
        ClipTimeMap map = ClipTimeMap.Create(Time.Zero, S(4), S(1), new ClipSpeed { Curve = curve });
        float[] left = Left(Read(new RetimedReader(new AudioRetime(map, true), new FunctionPcm(Sine(440), long.MaxValue / 4), -1), 0, 4 * Rate));

        double ratio = MaxStep(left.AsSpan(Rate / 10)) / (2 * 0.5 * Math.Sin(Math.PI * 440 / Rate));

        output.WriteLine(Invariant($"MEDIDA curva_salto_max = {ratio:F3} × el ideal"));
        Assert.InRange(Frequency(left.AsSpan(Rate / 2)), 439.5, 440.5);
        Assert.True(ratio <= 1.1, Invariant($"El mayor salto es {ratio:F3} veces el del seno ideal."));
    }

    [Fact]
    public void Reverse_reads_the_source_backwards()
    {
        Func<long, float> ramp = i => i is >= 0 and < 4 * Rate ? (float)(Math.Sin(2 * Math.PI * 50 * i / Rate) * (0.2 + (0.1 * i / Rate))) : 0;
        ClipTimeMap map = ClipTimeMap.Create(Time.Zero, S(2), S(3), new ClipSpeed { Reverse = true, PreservePitch = false });

        float[] left = Left(Read(new RetimedReader(new AudioRetime(map, false), new FunctionPcm(ramp, 4 * Rate), -1), 0, 2 * Rate));

        double worst = 0;
        for (int s = 100; s < (2 * Rate) - 100; s++)
        {
            worst = Math.Max(worst, Math.Abs(left[s] - ramp((3 * Rate) - 1 - s)));
        }

        Assert.True(worst < 2e-3, Invariant($"La reversa se aparta {worst:E2}."));
    }

    [Fact]
    public void Freeze_is_silence_and_identity_speed_takes_the_plain_path()
    {
        var b = Builder();
        Clip frozen = b.AddAudio(0, "tono", Time.Zero, S(1), S(1));
        frozen.Speed = new ClipSpeed { Freeze = true };
        frozen.SourceOut = frozen.SourceIn + Time.FromTicks(1);
        frozen.Duration = S(1);
        Clip plain = b.AddAudio(0, "tono", S(1), S(1), S(1));
        plain.Speed = new ClipSpeed();
        plain.Duration = S(1);

        AudioPlan plan = b.Plan().Audio;
        float[] mix = Mix(b, 0, 2 * Rate);

        Assert.Null(plan.Tracks[0].Clips[1].Retime);
        Assert.All(mix.AsSpan(0, 2 * Rate).ToArray(), s => Assert.Equal(0f, s));
        Assert.InRange(Frequency(Left(mix.AsSpan(2 * Rate))), 439.5, 440.5);
    }

    [Theory]
    [InlineData(true, 440)]
    [InlineData(false, 880)]
    public void A_clip_at_2x_sounds_for_half_its_source_through_the_mixer(bool preservePitch, double hz)
    {
        var b = Builder();
        Clip clip = b.AddAudio(0, "tono", Time.Zero, S(1), S(4));
        SetSpeed(clip, new Rational(2, 1), preservePitch);

        float[] mix = Mix(b, 0, 3 * Rate);

        Assert.Equal(S(2), clip.Duration);
        Assert.InRange(Frequency(Left(mix.AsSpan(Rate / 2, 3 * Rate))), hz - 0.5, hz + 0.5);
        Assert.All(mix.AsSpan(4 * Rate).ToArray(), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Splitting_a_clip_with_speed_does_not_change_a_single_sample()
    {
        var whole = Builder();
        SetSpeed(whole.AddAudio(0, "tono", Time.Zero, S(1), S(4), id: "a"), new Rational(3, 2), preservePitch: true);
        var split = Builder();
        Clip left = split.AddAudio(0, "tono", Time.Zero, S(1), S(4), id: "a");
        SetSpeed(left, new Rational(3, 2), preservePitch: true);
        (ClipTimeMap first, ClipTimeMap second) = left.TimeMap.SplitAt(S(1.2345));
        Clip right = left.Clone();
        right.Id = "b";
        first.ApplyTo(left);
        second.ApplyTo(right);
        split.Sequence.AudioTracks[0].Clips.Add(right);
        split.Sequence.AudioTracks[0].SortClips();

        float[] expected = Mix(whole, 0, 3 * Rate);
        float[] actual = Mix(split, 0, 3 * Rate);

        Assert.Equal(2, split.Plan().Audio.Tracks[0].Clips.Count);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Clip_gain_keyframes_are_read_at_the_source_instant_of_the_map()
    {
        var b = Builder();
        Clip clip = b.AddAudio(0, "dc", Time.Zero, S(1), S(4));
        clip.Effects.Add(new EffectInstance
        {
            Type = BuiltInEffects.Audio,
            Parameters = { [BuiltInEffects.AudioParameters.GainDb] = new Parameter { Keyframes = [new Keyframe(S(1), 0), new Keyframe(S(5), -20)] } },
        });
        SetSpeed(clip, new Rational(2, 1), preservePitch: true);

        float[] mix = Mix(b, 0, 2 * Rate);

        // A 1 s de la línea de tiempo suena el segundo 3 del origen: a mitad de la rampa, −10 dB.
        Assert.Equal(0.5 * Math.Pow(10, -10 / 20.0), mix[2 * Rate], 2e-3);
    }

    [Fact]
    public void A_change_of_speed_or_of_keeping_pitch_changes_what_sounds()
    {
        TimelineBuilder Make(Rational rate, bool preservePitch)
        {
            var b = Builder();
            SetSpeed(b.AddAudio(0, "tono", Time.Zero, S(1), S(4), id: "c"), rate, preservePitch);
            return b;
        }

        AudioPlan a = Make(new Rational(2, 1), true).Plan().Audio;

        Assert.True(a.SoundsLike(Make(new Rational(2, 1), true).Plan().Audio));
        Assert.False(a.SoundsLike(Make(new Rational(2, 1), false).Plan().Audio));
        Assert.False(a.SoundsLike(Make(new Rational(3, 1), true).Plan().Audio));
    }

    [Theory]
    [Trait("Categoria", "TiempoReal")]
    [InlineData(3, 2, true)]
    [InlineData(2, 1, true)]
    [InlineData(1, 2, true)]
    [InlineData(8, 1, true)]
    [InlineData(2, 1, false)]
    [InlineData(1, 2, false)]
    public void A_block_of_1024_stereo_frames_takes_at_most_0_15_ms(int num, int den, bool preservePitch)
    {
        // El medio sale de una tabla: se mide el cambio de velocidad, no el cálculo del seno.
        float[] table = [.. Enumerable.Range(0, 1 << 20).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / Rate)))];
        var reader = new RetimedReader(new AudioRetime(Map(new Rational(num, den), preservePitch), preservePitch),
            new FunctionPcm(i => table[i & ((1 << 20) - 1)], long.MaxValue / 4), -1);
        var block = new float[2048];
        long next = 0;

        // Hasta que el JIT por niveles deje el código optimizado, como lo deja Native AOT desde el principio.
        var warmUp = Stopwatch.StartNew();
        while (warmUp.ElapsedMilliseconds < 1500)
        {
            reader.Read(next, block, reader.DecoderNext);
            next += 1024;
        }

        // Tiempo de CPU del hilo, no de reloj: la máquina puede estar ocupada con otros procesos. Se mide hasta medio
        // segundo de CPU, así la resolución de 15,6 ms de Windows queda en un 3 %.
        TimeSpan cpuStart = ThreadCpu.Current();
        var wall = Stopwatch.StartNew();
        int blocks = 0;
        while (ThreadCpu.Current() - cpuStart < TimeSpan.FromMilliseconds(500) && wall.Elapsed < TimeSpan.FromSeconds(10))
        {
            for (int i = 0; i < 100; i++)
            {
                reader.Read(next, block, reader.DecoderNext);
                next += 1024;
            }

            blocks += 100;
        }

        double perBlock = (ThreadCpu.Current() - cpuStart).TotalMilliseconds / blocks;

        // El criterio vale para código optimizado (Release y Native AOT); en Debug el JIT no optimiza y solo se exige
        // que quede lejos de los 21 ms que dura el bloque.
        bool optimized = typeof(RetimedReader).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .Cast<DebuggableAttribute>().All(a => !a.IsJITOptimizerDisabled);
        double limit = optimized ? 0.15 : 3;
        output.WriteLine(Invariant($"MEDIDA bloque_1024_a_{num}/{den}_{(preservePitch ? "tono" : "cinta")} = {perBlock:F4} ms ({(optimized ? "optimizado" : "Debug")})"));
        Assert.True(perBlock <= limit, Invariant($"{perBlock:F4} ms por bloque (límite {limit} ms)."));
    }

    [Fact]
    public void Quality_is_at_least_that_of_ffmpeg_atempo()
    {
        if (FfmpegTool.Default is not { } ffmpeg)
        {
            output.WriteLine($"Sin referencia: {FfmpegTool.UnavailableReason}");
            return;
        }

        foreach ((double rate, Rational speed) in new[] { (0.5, new Rational(1, 2)), (0.75, new Rational(3, 4)), (1.5, new Rational(3, 2)), (2.0, new Rational(2, 1)) })
        {
            int frames = 3 * Rate;
            float[] ours = Left(Render(Sine(997), speed, preservePitch: true, 0, frames));
            float[] theirs = Atempo(ffmpeg, Sine(997), (int)(frames * rate) + Rate, rate);
            double oursDb = ThdN(ours.AsSpan(Rate / 2, 2 * Rate), 997);
            double theirsDb = ThdN(theirs.AsSpan(Rate / 2, 2 * Rate), 997);
            output.WriteLine(Invariant($"MEDIDA thdn_1k_a_{rate}: Rodaje {oursDb:F1} dB, atempo {theirsDb:F1} dB"));
            Assert.True(oursDb <= theirsDb + 1, Invariant($"A {rate}× Rodaje da {oursDb:F1} dB y atempo {theirsDb:F1} dB."));
        }
    }

    private static float[] Atempo(FfmpegTool ffmpeg, Func<long, float> signal, int frames, double rate)
    {
        float[] result = [];
        ffmpeg.Run(
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", "pipe:0",
                "-af", Invariant($"atempo={rate}"), "-f", "f32le", "-ar", "48000", "-ac", "2", "pipe:1",
            ],
            writeInput: input =>
            {
                var bytes = new byte[8];
                for (long i = 0; i < frames; i++)
                {
                    float v = signal(Rate + i);
                    BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), v);
                    BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), v);
                    input.Write(bytes);
                }
            },
            readOutput: stream =>
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var samples = new float[memory.Length / sizeof(float)];
                Buffer.BlockCopy(memory.GetBuffer(), 0, samples, 0, samples.Length * sizeof(float));
                result = Left(samples);
            });
        return result;
    }

    private static ClipTimeMap Map(Rational rate, bool preservePitch, Time? anchor = null) =>
        ClipTimeMap.Create(Time.Zero, S(600), anchor ?? S(1), new ClipSpeed { Rate = rate, PreservePitch = preservePitch });

    private static float[] Render(Func<long, float> signal, Rational rate, bool preservePitch, long from, int frames, Time? anchor = null)
    {
        var reader = new RetimedReader(new AudioRetime(Map(rate, preservePitch, anchor), preservePitch), new FunctionPcm(signal, long.MaxValue / 4), -1);
        return Read(reader, from, frames);
    }

    private static float[] Read(RetimedReader reader, long from, int frames)
    {
        var all = new float[frames * 2];
        for (int done = 0; done < frames;)
        {
            int n = Math.Min(1024, frames - done);
            reader.Read(from + done, all.AsSpan(done * 2, n * 2), reader.DecoderNext);
            done += n;
        }

        return all;
    }

    /// <summary>Picos por encima de <paramref name="threshold"/>; los que caen a menos de <paramref name="merge"/> muestras cuentan como uno (el mayor).</summary>
    private static List<int> Peaks(float[] x, float threshold, int merge)
    {
        var peaks = new List<int>();
        int i = 0;
        while (i < x.Length)
        {
            if (Math.Abs(x[i]) < threshold)
            {
                i++;
                continue;
            }

            int best = i;
            int lastAbove = i;
            int j = i;
            while (j < x.Length && j - lastAbove < merge)
            {
                if (Math.Abs(x[j]) >= threshold)
                {
                    lastAbove = j;
                    if (Math.Abs(x[j]) > Math.Abs(x[best]))
                    {
                        best = j;
                    }
                }

                j++;
            }

            peaks.Add(best);
            i = j;
        }

        return peaks;
    }

    private static TimelineBuilder Builder()
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("tono", Rational.Fps30, 30 * 60);
        b.AddMedia("dc", Rational.Fps30, 30 * 60);
        return b;
    }

    private static void SetSpeed(Clip clip, Rational rate, bool preservePitch)
    {
        clip.Speed = new ClipSpeed { Rate = rate, PreservePitch = preservePitch };
        ClipTimeMap.Fit(clip.Start, clip.SourceIn, clip.SourceOut, clip.Speed).ApplyTo(clip);
    }

    private static float[] Mix(TimelineBuilder b, long from, int frames)
    {
        using var mixer = new MixRenderer(b.Plan().Audio, path => path.Contains("dc", StringComparison.Ordinal)
            ? new FunctionPcm(_ => 0.5f, 60 * Rate)
            : new FunctionPcm(Sine(440), 60 * Rate), TimeSpan.FromSeconds(5), TimeProvider.System);
        mixer.Seek(Time.FromSamples(from, Rate));
        var all = new float[frames * 2];
        for (int done = 0; done < frames;)
        {
            int n = Math.Min(1024, frames - done);
            Assert.Equal(n, mixer.Read(all.AsSpan(done * 2, n * 2)));
            done += n;
        }

        return all;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Tiempo de CPU (usuario + núcleo) del hilo actual.</summary>
internal static class ThreadCpu
{
    public static TimeSpan Current()
    {
        Assert.True(GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user));
        return TimeSpan.FromTicks(kernel + user);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern nint GetCurrentThread();
}
