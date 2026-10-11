using System.Buffers.Binary;
using Rodaje.Audio;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media;
using Rodaje.Media.Export;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Offline;

public sealed class OfflineAudioTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-audio-").FullName;

    public static TheoryData<long, long, int> RatesAndFrequencies()
    {
        var data = new TheoryData<long, long, int>();
        foreach ((long num, long den) in new (long, long)[] { (24, 1), (25, 1), (30000, 1001), (30, 1), (50, 1), (60000, 1001), (60, 1) })
        {
            foreach (int frequency in new[] { 48_000, 44_100, 96_000 })
            {
                data.Add(num, den, frequency);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RatesAndFrequencies))]
    public void The_wave_has_exactly_the_samples_of_the_video_frames_even_in_parts_and_hours_in(long num, long den, int frequency)
    {
        var rate = new Rational(num, den);
        using OfflineAudio audio = Open(SineBackend.Project(Time.FromSeconds(3 * 3600), out SineBackend backend), backend);
        var format = new ExportAudioFormat(frequency, 2, ExportSampleFormat.Pcm16);
        long hour = Time.FromSeconds(3600).ToFrames(rate);
        long[] cuts = [2 * hour, (2 * hour) + 7, (2 * hour) + 31, (2 * hour) + 100];

        long sum = 0;
        for (int i = 0; i + 1 < cuts.Length; i++)
        {
            Time start = Time.FromFrames(cuts[i], rate);
            Time end = Time.FromFrames(cuts[i + 1], rate);
            string path = Path.Combine(_folder, $"parte{i}.wav");
            long written = audio.WriteWave(path, start, end, format, 0, null, CancellationToken.None);
            Assert.Equal(end.ToSamples(frequency) - start.ToSamples(frequency), written);
            Assert.Equal(written, Wave.Read(path).Frames);
            sum += written;
        }

        Time whole = Time.FromFrames(cuts[^1], rate) - Time.FromFrames(cuts[0], rate);
        double expected = whole.ToSeconds() * frequency;
        Assert.InRange(sum, Math.Floor(expected) - 1, Math.Ceiling(expected) + 1);
        Assert.Equal(Time.FromFrames(cuts[^1], rate).ToSamples(frequency) - Time.FromFrames(cuts[0], rate).ToSamples(frequency), sum);
    }

    [Fact]
    public void At_48_kHz_the_wave_is_the_mix_sample_for_sample_with_the_gain()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(0, 2);
        b.AddMedia("a", Rational.Fps30, 900, video: false, backend: backend);
        b.AddMedia("b", Rational.Fps30, 900, video: false, backend: backend);
        b.AddAudio(0, "a", S(0.5), S(1), S(10));
        b.AddAudio(1, "b", S(2), S(3), S(5));
        string path = Path.Combine(_folder, "mezcla.wav");
        using OfflineAudio audio = new(b.Project, b.Sequence.Id, @"C:\proyecto", backend);
        using OfflineAudio reference = new(b.Project, b.Sequence.Id, @"C:\proyecto", backend);

        long written = audio.WriteWave(path, S(1), S(9), ExportAudioFormat.Default, -6, null, CancellationToken.None);

        Wave wave = Wave.Read(path);
        Assert.Equal((48_000, 2, 3, 32), (wave.SampleRate, wave.Channels, wave.Format, wave.Bits));
        Assert.Equal(8 * 48_000, written);
        var mix = new float[written * 2];
        reference.Seek(S(1));
        reference.Read(mix);
        float gain = (float)Math.Pow(10, -6 / 20.0);
        for (int i = 0; i < mix.Length; i++)
        {
            Assert.Equal(mix[i] * gain, wave.Samples[i]);
        }

        Assert.Contains(wave.Samples, s => s != 0);
    }

    [Fact]
    public void Mono_lowers_the_mix_and_surround_puts_it_in_front_left_and_right()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("a", Rational.Fps30, 900, video: false, backend: backend);
        b.AddAudio(0, "a", Time.Zero, Time.Zero, S(4));
        using OfflineAudio audio = new(b.Project, b.Sequence.Id, @"C:\proyecto", backend);
        var mix = new float[4800 * 2];
        audio.Seek(S(1));
        audio.Read(mix);

        audio.WriteWave(Path.Combine(_folder, "mono.wav"), S(1), S(1.1), new ExportAudioFormat(48_000, 1), 0, null, CancellationToken.None);
        audio.WriteWave(Path.Combine(_folder, "51.wav"), S(1), S(1.1), new ExportAudioFormat(48_000, 6, ExportSampleFormat.Pcm24), 0, null,
            CancellationToken.None);

        Wave mono = Wave.Read(Path.Combine(_folder, "mono.wav"));
        Wave surround = Wave.Read(Path.Combine(_folder, "51.wav"));
        Assert.Equal((1, 0x4u), (mono.Channels, mono.Mask));
        Assert.Equal((6, 24, 0x3Fu), (surround.Channels, surround.Bits, surround.Mask));
        for (int i = 0; i < 4800; i++)
        {
            Assert.Equal(0.5f * (mix[2 * i] + mix[(2 * i) + 1]), mono.Samples[i]);
            Assert.Equal(mix[2 * i], surround.Samples[6 * i], 1e-6f);
            Assert.Equal(mix[(2 * i) + 1], surround.Samples[(6 * i) + 1], 1e-6f);
            Assert.All(surround.Samples.AsSpan((6 * i) + 2, 4).ToArray(), s => Assert.Equal(0f, s));
        }
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(96_000)]
    [InlineData(32_000)]
    public void Another_frequency_is_the_same_sine_resampled_without_drift(int frequency)
    {
        using OfflineAudio audio = Open(SineBackend.Project(S(30), out SineBackend backend), backend);
        string path = Path.Combine(_folder, "seno.wav");

        audio.WriteWave(path, S(10), S(20), new ExportAudioFormat(frequency), 0, null, CancellationToken.None);

        Wave wave = Wave.Read(path);
        long first = S(10).ToSamples(frequency);
        double error = 0;
        double worst = 0;
        for (long n = 0; n < wave.Frames; n++)
        {
            double ideal = SineBackend.Amplitude * Math.Sin(2 * Math.PI * SineBackend.Hz * (first + n) / frequency);
            double e = wave.Samples[2 * n] - ideal;
            error += e * e;
            worst = Math.Max(worst, Math.Abs(e));
            Assert.Equal(wave.Samples[2 * n], wave.Samples[(2 * n) + 1]);
        }

        double rmsDb = 10 * Math.Log10(error / wave.Frames / (SineBackend.Amplitude * SineBackend.Amplitude / 2));
        output.WriteLine($"48 kHz → {frequency} Hz: error {rmsDb:0.0} dB bajo la señal, peor muestra {worst:E2}.");
        Assert.True(rmsDb < -80, $"{rmsDb:0.0} dB");
    }

    [Theory]
    [InlineData(-23.0)]
    [InlineData(-33.0)]
    public void Loudness_of_the_ebu_sine_is_its_level(double dbfs)
    {
        using OfflineAudio audio = Open(SineBackend.Project(S(20), out SineBackend backend, Math.Pow(10, dbfs / 20)), backend);

        LoudnessMeasurement loudness = audio.MeasureLoudness(Time.Zero, S(20), null, CancellationToken.None);

        output.WriteLine($"{dbfs} dBFS → {loudness.IntegratedLufs:0.00} LUFS, {loudness.TruePeakDbtp:0.00} dBTP, LRA {loudness.RangeLu:0.00} LU");
        Assert.Equal(dbfs, loudness.IntegratedLufs, 0.1);
        Assert.Equal(dbfs, loudness.TruePeakDbtp, 0.2);
        Assert.InRange(loudness.RangeLu, 0, 0.1);
    }

    [Fact]
    public void Loudness_range_of_two_levels_and_normalizing_to_a_target()
    {
        using OfflineAudio audio = Open(SineBackend.TwoLevels(out SineBackend backend), backend);

        LoudnessMeasurement loudness = audio.MeasureLoudness(Time.Zero, S(40), null, CancellationToken.None);
        double gain = loudness.GainTo(-14);
        string path = Path.Combine(_folder, "normalizado.wav");
        audio.WriteWave(path, Time.Zero, S(40), ExportAudioFormat.Default, gain, null, CancellationToken.None);

        var meter = new LoudnessMeter();
        meter.Process(Wave.Read(path).Samples);
        LoudnessMeasurement normalized = meter.Result();
        output.WriteLine($"LRA {loudness.RangeLu:0.00} LU, {loudness.IntegratedLufs:0.00} LUFS → {normalized.IntegratedLufs:0.00} LUFS (pico {normalized.TruePeakDbtp:0.00} dBTP).");
        Assert.Equal(10, loudness.RangeLu, 1);
        Assert.Equal(-14, normalized.IntegratedLufs, 0.1);
        Assert.Equal(0, new LoudnessMeasurement(double.NegativeInfinity, double.NegativeInfinity, 0).GainTo(-14));
    }

    [Fact]
    public void Silence_has_no_loudness()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(0, 1);
        using OfflineAudio audio = new(b.Project, b.Sequence.Id, @"C:\proyecto", backend);

        LoudnessMeasurement loudness = audio.MeasureLoudness(Time.Zero, S(5), null, CancellationToken.None);

        Assert.True(double.IsNegativeInfinity(loudness.IntegratedLufs));
        Assert.True(double.IsNegativeInfinity(loudness.TruePeakDbtp));
    }

    [Fact]
    public void A_retimed_clip_still_gives_exactly_the_samples_of_the_range()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("a", Rational.Fps30, 9000, video: false, backend: backend);
        SpeedClips.WithRate(b.AddAudio(0, "a", S(1), S(10), S(2)), new Rational(3, 2), S(10), S(13));
        using OfflineAudio audio = new(b.Project, b.Sequence.Id, @"C:\proyecto", backend);

        Time start = Time.FromFrames(17, Rational.Ntsc30);
        Time end = Time.FromFrames(131, Rational.Ntsc30);
        long written = audio.WriteWave(Path.Combine(_folder, "velocidad.wav"), start, end, new ExportAudioFormat(44_100), 0, null, CancellationToken.None);

        Assert.Equal(end.ToSamples(44_100) - start.ToSamples(44_100), written);
        Assert.Contains(Wave.Read(Path.Combine(_folder, "velocidad.wav")).Samples, s => s != 0);
        Assert.Equal(0, audio.Errors);
    }

    [Fact]
    public void A_missing_audio_file_fails_before_writing_anything()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("a", Rational.Fps30, 900, video: false, backend: backend);
        b.AddAudio(0, "a", S(5), Time.Zero, S(2));
        backend.FailOpen.Add(TimelineBuilder.PathOf("a"));
        using OfflineAudio audio = new(b.Project, b.Sequence.Id, @"C:\proyecto", backend);
        string path = Path.Combine(_folder, "falta.wav");

        Assert.Equal(1, audio.WriteWave(path, Time.Zero, Time.FromSamples(1, 48_000), ExportAudioFormat.Default, 0, null, CancellationToken.None));
        ExportException error = Assert.Throws<ExportException>(() =>
            audio.WriteWave(path + "2", Time.Zero, S(6), ExportAudioFormat.Default, 0, null, CancellationToken.None));

        Assert.Equal((ExportErrorCode.MediaMissing, TimelineBuilder.PathOf("a")), (error.Code, error.Culprit));
        Assert.Equal(ExportErrorCode.MediaMissing, Assert.Throws<ExportException>(() =>
            audio.MeasureLoudness(Time.Zero, S(6), null, CancellationToken.None)).Code);
        Assert.Equal(["falta.wav"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Cancelling_leaves_no_file_and_keeps_the_previous_one()
    {
        using OfflineAudio audio = Open(SineBackend.Project(S(60), out SineBackend backend), backend);
        string path = Path.Combine(_folder, "cancelado.wav");
        File.WriteAllText(path, "anterior");
        using var cancel = new CancellationTokenSource();
        var reports = new List<double>();
        var progress = new SyncProgress(p =>
        {
            reports.Add(p);
            if (p > 0.3)
            {
                cancel.Cancel();
            }
        });

        Assert.Throws<OperationCanceledException>(() =>
            audio.WriteWave(path, Time.Zero, S(60), ExportAudioFormat.Default, 0, progress, cancel.Token));

        Assert.Equal("anterior", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_folder));
        Assert.True(reports.Count is > 10 and < 300, $"{reports.Count} avisos");

        long written = audio.WriteWave(path, Time.Zero, S(1), ExportAudioFormat.Default, 0, progress, CancellationToken.None);
        Assert.Equal(48_000, Wave.Read(path).Frames);
        Assert.Equal(48_000, written);
        Assert.Equal(1, reports[^1]);
    }

    [Fact]
    public void An_unwritable_destination_is_reported_as_such()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(0, 1);
        using OfflineAudio audio = new(b.Project, b.Sequence.Id, @"C:\proyecto", backend);

        ExportException error = Assert.Throws<ExportException>(() => audio.WriteWave(Path.Combine(_folder, "no", "existe.wav"), Time.Zero, S(1),
            ExportAudioFormat.Default, 0, null, CancellationToken.None));

        Assert.Equal(ExportErrorCode.OutputNotWritable, error.Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.WriteWave("x.wav", S(1), S(1), ExportAudioFormat.Default, 0, null, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.WriteWave("x.wav", Time.Zero, S(1), ExportAudioFormat.Default, double.NaN, null,
            CancellationToken.None));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static OfflineAudio Open(Project project, SineBackend backend) => new(project, "sec", @"C:\proyecto", backend);

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Un WAV leído de vuelta: formato y muestras normalizadas a float.</summary>
    private sealed record Wave(int SampleRate, int Channels, int Bits, int Format, uint Mask, float[] Samples)
    {
        public long Frames => Samples.Length / Channels;

        public static Wave Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
            Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
            int at = 12;
            int rate = 0, channels = 0, bits = 0, format = 0;
            uint mask = 0;
            while (at + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
                ReadOnlySpan<byte> body = bytes.AsSpan(at + 8, size);
                if (id == "fmt ")
                {
                    Assert.Equal(0xFFFE, BinaryPrimitives.ReadUInt16LittleEndian(body));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                    rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                    mask = BinaryPrimitives.ReadUInt32LittleEndian(body[20..]);
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
                }
                else if (id == "data")
                {
                    int width = bits / 8;
                    var samples = new float[size / width];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        ReadOnlySpan<byte> s = body.Slice(i * width, width);
                        samples[i] = (format, bits) switch
                        {
                            (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
                            (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32767f,
                            (1, 24) => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8_388_607f,
                            (1, 32) => (float)(BinaryPrimitives.ReadInt32LittleEndian(s) / (double)int.MaxValue),
                            _ => throw new InvalidDataException(),
                        };
                    }

                    return new Wave(rate, channels, bits, format, mask, samples);
                }

                at += 8 + size + (size & 1);
            }

            throw new InvalidDataException("Sin datos.");
        }
    }

    /// <summary>Audio de prueba: un seno de 1 kHz igual en los dos canales, con la amplitud que dice la ruta.</summary>
    private sealed class SineBackend : IClipBackend
    {
        public const double Hz = 1000;
        public const double Amplitude = 0.5;

        private readonly Dictionary<string, double> _amplitudes = new(StringComparer.OrdinalIgnoreCase);

        public static Project Project(Time length, out SineBackend backend, double amplitude = Amplitude)
        {
            backend = new SineBackend();
            var b = new TimelineBuilder(0, 1);
            b.AddMedia("seno", Rational.Fps30, length.ToFrames(Rational.Fps30) + 30, video: false);
            backend._amplitudes[TimelineBuilder.PathOf("seno")] = amplitude;
            b.AddAudio(0, "seno", Time.Zero, Time.Zero, length);
            return b.Project;
        }

        /// <summary>20 s a −20 dBFS y 20 s a −30 dBFS (EBU Tech 3342, caso 1: LRA 10 LU).</summary>
        public static Project TwoLevels(out SineBackend backend)
        {
            backend = new SineBackend();
            var b = new TimelineBuilder(0, 1);
            b.AddMedia("alto", Rational.Fps30, 700, video: false);
            b.AddMedia("bajo", Rational.Fps30, 700, video: false);
            backend._amplitudes[TimelineBuilder.PathOf("alto")] = Math.Pow(10, -20 / 20.0);
            backend._amplitudes[TimelineBuilder.PathOf("bajo")] = Math.Pow(10, -30 / 20.0);
            b.AddAudio(0, "alto", Time.Zero, Time.Zero, S(20));
            b.AddAudio(0, "bajo", S(20), Time.Zero, S(20));
            return b.Project;
        }

        public IClipVideo OpenVideo(string path, VideoDecoderOptions options) => throw new MediaException("Sin video.");

        public IPcmSource OpenAudio(string path) => new SinePcm(_amplitudes[path]);

        public IAudioOutput OpenOutput(IAudioSampleSource source, AudioOutputOptions options) => throw new NotSupportedException();

        public IDecodeExecutor CreateExecutor() => throw new NotSupportedException();

        private sealed class SinePcm(double amplitude) : IPcmSource
        {
            private long _position;

            public int Read(Span<float> interleaved)
            {
                int frames = interleaved.Length / 2;
                for (int i = 0; i < frames; i++)
                {
                    float v = (float)(amplitude * Math.Sin(2 * Math.PI * Hz * (_position + i) / 48_000));
                    interleaved[2 * i] = v;
                    interleaved[(2 * i) + 1] = v;
                }

                _position += frames;
                return frames;
            }

            public void Seek(Time position) => _position = position.ToSamples(48_000);

            public void Dispose()
            {
            }
        }
    }
}
