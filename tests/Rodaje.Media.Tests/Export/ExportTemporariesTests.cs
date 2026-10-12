using System.Buffers.Binary;
using System.Text;
using Rodaje.Media.Export;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests.Export;

/// <summary>WAV temporal, registro de temporales tras un apagón y normalización de sonoridad.</summary>
public sealed class ExportTemporariesTests(ITestOutputHelper output)
{
    [Fact]
    public void A_float_wave_keeps_every_sample()
    {
        using var scratch = new ExportScratch();
        string path = scratch.Combine("mezcla.wav");
        float[] samples = [0f, 0.5f, -0.25f, 1f, -1f, 0.125f];
        using (var writer = new WaveFileWriter(path, ExportAudioFormat.Default))
        {
            writer.Write(samples);
            writer.Complete();
            Assert.Equal(3, writer.SamplesPerChannel);
            Assert.False(writer.IsRf64);
        }

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal((uint)bytes.Length - 8, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        int data = IndexOf(bytes, "data");
        Assert.Equal((uint)(samples.Length * 4), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(data + 4)));
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i], BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(data + 8 + (i * 4))));
        }
    }

    [Fact]
    public void Integer_waves_saturate_instead_of_wrapping()
    {
        using var scratch = new ExportScratch();
        string path = scratch.Combine("mezcla16.wav");
        using (var writer = new WaveFileWriter(path, new ExportAudioFormat(48_000, 1, ExportSampleFormat.Pcm16)))
        {
            writer.Write([2f, -2f, 0.5f, float.NaN]);
            writer.Complete();
        }

        byte[] bytes = File.ReadAllBytes(path);
        int data = IndexOf(bytes, "data") + 8;
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(data)));
        Assert.Equal(short.MinValue + 1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(data + 2)));
        Assert.Equal(16384, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(data + 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(data + 6)));
    }

    [Fact]
    public void A_wave_without_complete_is_deleted()
    {
        using var scratch = new ExportScratch();
        string path = scratch.Combine("a medias.wav");
        using (var writer = new WaveFileWriter(path, ExportAudioFormat.Default))
        {
            writer.Write([0.1f, 0.1f]);
        }

        Assert.False(File.Exists(path));
    }

    [ProxyTheory]
    [InlineData(ExportSampleFormat.PcmFloat, 2, "pcm_f32le")]
    [InlineData(ExportSampleFormat.Pcm24, 6, "pcm_s24le")]
    [InlineData(ExportSampleFormat.Pcm16, 1, "pcm_s16le")]
    public void Ffmpeg_reads_rf64_past_the_threshold(ExportSampleFormat sampleFormat, int channels, string codec)
    {
        using var scratch = new ExportScratch();
        string path = scratch.Combine("grande.wav");
        var format = new ExportAudioFormat(48_000, channels, sampleFormat);
        using (var writer = new WaveFileWriter(path, format, rf64Threshold: 1000))
        {
            writer.Write(new float[48_000 * 2 * channels]);
            writer.Complete();
            Assert.True(writer.IsRf64);
        }

        Assert.Equal("RF64", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4));
        ExportReport report = ExportVerifier.For(SinkRuns.Ffmpeg).Inspect(path);
        Assert.Equal(codec, report.Audio!.Codec);
        Assert.Equal(channels, report.Audio.Channels);
        Assert.Equal(2.0, report.Audio.DurationSeconds, 3);
    }

    [Fact]
    public void Stale_temporaries_are_cleaned_after_a_power_cut()
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        string output1 = scratch.Combine("video.mp4");
        string temporary = ExportOutputGuard.TemporaryPath(output1);
        string passLog = temporary + ".pasadas";
        string unrelated = scratch.Combine("mis fotos.png");
        File.WriteAllText(temporary, "a medias");
        File.WriteAllText(passLog + "-0.log", "registro");
        File.WriteAllText(unrelated, "no se toca");
        journal.Register("vivo", scratch.Combine("otra.mp4"), [ExportOutputGuard.TemporaryPath(scratch.Combine("otra.mp4"))]);
        journal.Register("muerto", output1, [temporary, passLog, unrelated]);
        string json = File.ReadAllText(journal.Path);
        long start = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        int last = json.LastIndexOf(start.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        File.WriteAllText(journal.Path, string.Concat(json.AsSpan(0, last), "1", json.AsSpan(last + start.ToString(System.Globalization.CultureInfo.InvariantCulture).Length)));

        IReadOnlyList<string> deleted = journal.CleanupStale();

        Assert.Equal([temporary], deleted);
        Assert.False(File.Exists(temporary));
        Assert.False(File.Exists(passLog + "-0.log"));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(["vivo"], journal.Entries().Select(e => e.Id));
        journal.Remove("vivo");
        Assert.False(File.Exists(journal.Path));
    }

    [Fact]
    public void A_temporary_still_open_keeps_its_entry_for_the_next_start()
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        string temporary = ExportOutputGuard.TemporaryPath(scratch.Combine("video.mp4"));
        File.WriteAllText(temporary, "a medias");
        journal.Register("muerto", scratch.Combine("video.mp4"), [temporary]);
        string json = File.ReadAllText(journal.Path);
        string start = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(journal.Path, json.Replace(start, "1", StringComparison.Ordinal));

        using (new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(journal.CleanupStale());
            Assert.Equal(["muerto"], journal.Entries().Select(e => e.Id));
        }

        Assert.Equal([temporary], journal.CleanupStale());
        Assert.False(File.Exists(temporary));
        Assert.Empty(journal.Entries());
    }

    [Fact]
    public void A_corrupt_journal_does_not_block_exports()
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        Directory.CreateDirectory(journal.Directory);
        File.WriteAllText(journal.Path, "{\"exportaciones\": [ {\"id\": ");

        Assert.Empty(journal.CleanupStale());
        journal.Register("nueva", scratch.Combine("x.mp4"), []);
        Assert.Single(journal.Entries());
    }

    [ProxyFact]
    public void Loudness_is_measured_and_normalized_in_two_passes()
    {
        using var scratch = new ExportScratch();
        string quiet = scratch.Tone("bajo.rodaje-tmp.wav", 6, amplitude: 0.02);
        LoudnormMeasurement measured = FfmpegLoudness.Measure(SinkRuns.Ffmpeg, quiet, -14, -1, CancellationToken.None);
        string outputPath = scratch.Combine("normalizado.flac");
        var request = new ExportSinkRequest(outputPath, ExportContainer.Flac, null, null, [], ExportAudioFormat.Default, quiet,
            SinkEncoders.For("flac"), ["-af", FfmpegLoudness.Filter(-14, -1, measured), "-ar", "48000"], 0);
        using (var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg))
        {
            SinkRuns.Run(sink, request, null);
        }

        LoudnormMeasurement after = FfmpegLoudness.Measure(SinkRuns.Ffmpeg, outputPath, -14, -1, CancellationToken.None);
        output.WriteLine($"Antes {measured.IntegratedLufs:0.0} LUFS, después {after.IntegratedLufs:0.0} LUFS, pico {after.TruePeakDbtp:0.0} dBTP.");
        Assert.InRange(measured.IntegratedLufs, -40, -30);
        Assert.InRange(after.IntegratedLufs, -14.5, -13.5);
        Assert.True(after.TruePeakDbtp <= -0.9);
        Assert.Equal(6.0, ExportVerifier.For(SinkRuns.Ffmpeg).Inspect(outputPath).Audio!.DurationSeconds, 2);
    }

    [Fact]
    public void Loudnorm_json_is_read_with_silence_as_minus_infinity()
    {
        const string stderr = "[Parsed_loudnorm_0 @ 0x1]\n{\n\t\"input_i\" : \"-inf\",\n\t\"input_tp\" : \"-inf\",\n\t\"input_lra\" : \"0.00\"," +
            "\n\t\"input_thresh\" : \"-70.00\",\n\t\"target_offset\" : \"0.00\"\n}\n";

        LoudnormMeasurement measured = FfmpegLoudness.Parse(stderr);

        Assert.True(measured.IsSilent);
        Assert.Throws<ExportException>(() => FfmpegLoudness.Parse("sin medición"));
    }

    [Fact]
    public void Moov_position_is_read_from_the_boxes()
    {
        using var scratch = new ExportScratch();
        string fast = scratch.Combine("rápido.mp4");
        string slow = scratch.Combine("lento.mp4");
        string other = scratch.Combine("otro.mkv");
        File.WriteAllBytes(fast, [.. Box("ftyp", 8), .. Box("moov", 16), .. Box("mdat", 32)]);
        File.WriteAllBytes(slow, [.. Box("ftyp", 8), .. Box("mdat", 32), .. Box("moov", 16)]);
        File.WriteAllBytes(other, [0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0]);

        Assert.True(ExportVerifier.MoovBeforeMdat(fast));
        Assert.False(ExportVerifier.MoovBeforeMdat(slow));
        Assert.Null(ExportVerifier.MoovBeforeMdat(other));
    }

    [Fact]
    public void The_check_lists_every_difference_in_spanish()
    {
        var report = new ExportReport("mov,mp4", 2, 1000, false,
            new ExportVideoReport("h264", "High", 1920, 1080, "yuv420p", "bt709", "bt709", "bt709", "pc", "center",
                Rodaje.Core.Timing.Rational.Fps30, 59, 0.0, 2, 120, 1, false),
            new ExportAudioReport("aac", "LC", 44_100, 2, 0.05, 2), 0, new Dictionary<string, string>());

        IReadOnlyList<string> problems = ExportVerifier.Check(report, new ExportExpectation
        {
            FrameCount = 60,
            Color = ExportColor.Bt709Limited,
            MaxGop = 60,
            SampleRate = 48_000,
            FastStart = true,
            Chapters = 2,
        });

        Assert.Equal(
        [
            "Cuadros: 59; se esperaba 60.",
            "Rango de color: «pc»; se esperaba «tv».",
            "Posición del croma: «center»; se esperaba «left».",
            "Hay 120 cuadros entre dos cuadros clave y el máximo es 60.",
            "Frecuencia: 44100; se esperaba 48000.",
            "El audio empieza en 0.05 s y no en 0.",
            "Audio y video empiezan separados 50 ms.",
            "El índice (moov) está al final: no se puede reproducir mientras descarga.",
            "Capítulos: 0; se esperaba 2.",
        ], problems);
    }

    private static byte[] Box(string type, int size)
    {
        byte[] box = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)size);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        return box;
    }

    private static int IndexOf(byte[] bytes, string tag)
    {
        byte[] needle = Encoding.ASCII.GetBytes(tag);
        return bytes.AsSpan().IndexOf(needle);
    }
}
