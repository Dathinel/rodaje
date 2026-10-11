using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests.Export;

/// <summary>Exportaciones cortas de verdad por la tubería, verificadas con ffprobe.</summary>
public sealed class FfmpegPipeSinkTests(ITestOutputHelper output)
{
    public static TheoryData<string, ExportContainer, ExportPixelFormat, string, string, string> Encoders => new()
    {
        { "h264_nvenc", ExportContainer.Mp4, ExportPixelFormat.Nv12, "-preset p4 -rc vbr -cq 23 -g 15 -bf 2", "aac_mf", "-b:a 192k" },
        { "hevc_nvenc", ExportContainer.Mp4, ExportPixelFormat.Nv12, "-preset p4 -rc vbr -cq 25 -g 15", "aac", "-b:a 160k" },
        { "hevc_nvenc", ExportContainer.Mkv, ExportPixelFormat.P010, "-preset p4 -profile:v main10 -g 15", "flac", "" },
        { "h264_mf", ExportContainer.Mp4, ExportPixelFormat.Nv12, "-rate_control quality -quality 70 -g 15 -hw_encoding 1", "aac_mf", "-b:a 128k" },
        { "libsvtav1", ExportContainer.Mp4, ExportPixelFormat.Yuv420P, "-preset 10 -crf 35 -g 15", "libopus", "-b:a 128k" },
        { "libsvtav1", ExportContainer.Mkv, ExportPixelFormat.Yuv420P10, "-preset 10 -crf 35 -g 15", "libopus", "-b:a 96k" },
        { "libvpx-vp9", ExportContainer.WebM, ExportPixelFormat.Yuv420P, "-deadline realtime -cpu-used 8 -crf 33 -b:v 0 -g 15 -row-mt 1", "libopus", "-b:a 128k" },
        { "prores_ks", ExportContainer.Mov, ExportPixelFormat.Yuv422P10, "-profile:v hq -vendor apl0", "pcm_s24le", "" },
        { "dnxhd", ExportContainer.Mov, ExportPixelFormat.Yuv422P, "-profile:v dnxhr_sq", "pcm_s16le", "" },
        { "dnxhd", ExportContainer.Mxf, ExportPixelFormat.Yuv422P10, "-profile:v dnxhr_hqx", "pcm_s24le", "" },
        { "ffv1", ExportContainer.Mkv, ExportPixelFormat.Yuv420P, "-level 3 -slicecrc 1 -g 1", "flac", "" },
        { "h264_nvenc", ExportContainer.Mov, ExportPixelFormat.Nv12, "-preset p1 -g 30", "alac", "" },
        { "h264_nvenc", ExportContainer.MpegTs, ExportPixelFormat.Nv12, "-preset p1 -g 30", "aac_mf", "-b:a 192k" },
        { "h264_nvenc", ExportContainer.Mp4, ExportPixelFormat.Nv12, "-preset p1 -g 30", "libmp3lame", "-b:a 192k" },
    };

    [ProxyTheory]
    [MemberData(nameof(Encoders))]
    public void A_short_export_is_what_was_asked(string encoder, ExportContainer container, ExportPixelFormat pixels, string videoArgs,
        string audioEncoder, string audioArgs)
    {
        if (IsHardware(encoder) && SinkRuns.Ffmpeg.ProbeEncoder(encoder) is { } missing)
        {
            output.WriteLine("Sin probar en este equipo: " + missing);
            return;
        }

        using var scratch = new ExportScratch();
        const int frames = 45;
        ExportVideoFormat format = SinkRuns.Format(640, 360, pixels);
        string wav = scratch.Tone("mezcla.rodaje-tmp.wav", frames / 30.0);
        string outputPath = scratch.Combine($"salida {encoder}.{container.Extension()}");
        bool isoBmff = container is ExportContainer.Mp4 or ExportContainer.Mov;
        IReadOnlyList<string> containerArgs = isoBmff ? ["-movflags", "+faststart"] : [];
        var request = new ExportSinkRequest(outputPath, container, format, SinkEncoders.For(encoder),
            [.. Split(videoArgs), .. containerArgs], ExportAudioFormat.Default, wav, SinkEncoders.For(audioEncoder), [.. Split(audioArgs), "-ar", "48000", "-ac", "2"], frames);
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg);

        (ExportSinkResult result, TimeSpan elapsed) = SinkRuns.Run(sink, request, new SyntheticFrames(format));

        output.WriteLine($"{encoder} → {container}: {elapsed.TotalMilliseconds:0} ms, {result.Bytes / 1024} KB");
        Assert.Equal(outputPath, result.OutputPath);
        Assert.Equal(frames, result.FramesWritten);
        Assert.Equal(frames, sink.FramesEncoded);
        Assert.False(SinkRuns.IsAlive(sink.ProcessId));
        Assert.Equal([outputPath], scratch.Leftovers(wav));
        ExportReport report = ExportVerifier.For(SinkRuns.Ffmpeg).Inspect(outputPath);
        output.WriteLine($"{report.Video} / {report.Audio}");
        IReadOnlyList<string> problems = ExportVerifier.Check(report, new ExportExpectation
        {
            Width = 640,
            Height = 360,
            FrameCount = frames,
            FrameRate = Rational.Fps30,
            PixelFormat = pixels switch
            {
                ExportPixelFormat.Nv12 => "yuv420p",
                ExportPixelFormat.P010 => "yuv420p10le",
                _ => pixels.FfmpegName(),
            },
            Color = format.Color,
            AudioCodec = report.Audio?.Codec,
            AudioDurationSeconds = frames / 30.0,
            SampleRate = 48_000,
            Channels = 2,
            FastStart = isoBmff,
        });
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [ProxyFact]
    public void An_audio_only_export_needs_no_frames()
    {
        using var scratch = new ExportScratch();
        string wav = scratch.Tone("mezcla.rodaje-tmp.wav", 1.5);
        string outputPath = scratch.Combine("solo audio.m4a");
        var request = new ExportSinkRequest(outputPath, ExportContainer.M4a, null, null, [], ExportAudioFormat.Default, wav,
            SinkEncoders.For("aac"), ["-b:a", "128k", "-movflags", "+faststart"], 0);
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg);

        SinkRuns.Run(sink, request, null);

        ExportReport report = ExportVerifier.For(SinkRuns.Ffmpeg).Inspect(outputPath);
        Assert.Null(report.Video);
        Assert.Empty(ExportVerifier.Check(report, new ExportExpectation { AudioCodec = "aac", AudioDurationSeconds = 1.5, FastStart = true }));
    }

    [ProxyFact]
    public void Metadata_chapters_and_cover_reach_the_file()
    {
        using var scratch = new ExportScratch();
        ExportVideoFormat format = SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P);
        string cover = scratch.Combine("portada.png");
        File.WriteAllBytes(cover, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
        string outputPath = scratch.Combine("capítulos.mp4");
        var request = new ExportSinkRequest(outputPath, ExportContainer.Mp4, format, SinkEncoders.For("libsvtav1"),
            ["-preset", "12", "-crf", "50"], null, null, null, [], 60)
        {
            Metadata = new ExportMetadata
            {
                Title = "Viaje; con = signos",
                Author = "Daniel",
                Comment = "Exportado por Rodaje",
                Chapters = [new(Time.Zero, Time.FromSeconds(1), "Inicio"), new(Time.FromSeconds(1), Time.FromSeconds(2), "Final #2")],
                ThumbnailPath = cover,
            },
        };
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg);

        SinkRuns.Run(sink, request, new SyntheticFrames(format));

        ExportReport report = ExportVerifier.For(SinkRuns.Ffmpeg).Inspect(outputPath);
        Assert.Equal(2, report.Chapters);
        Assert.Equal("Viaje; con = signos", report.Tags["title"]);
        Assert.Equal("Daniel", report.Tags["artist"]);
        Assert.Equal(60, report.Video!.FrameCount);
    }

    [ProxyFact]
    public void An_image_sequence_lands_in_its_folder()
    {
        using var scratch = new ExportScratch();
        ExportVideoFormat format = SinkRuns.Format(64, 36, ExportPixelFormat.Rgb24);
        string folder = scratch.Combine("cuadros");
        var request = new ExportSinkRequest(folder, ExportContainer.ImageSequence, format, SinkEncoders.For("png"), [], null, null, null, [], 5);
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg);

        ExportSinkResult result = SinkRuns.Run(sink, request, new SyntheticFrames(format)).Result;

        string[] files = [.. Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order()!];
        Assert.Equal(["cuadros_000000.png", "cuadros_000001.png", "cuadros_000002.png", "cuadros_000003.png", "cuadros_000004.png"], files);
        Assert.True(result.Bytes > 0);
        Assert.Equal([folder], scratch.Leftovers());
    }

    [ProxyFact]
    public void Two_passes_share_the_sink_and_leave_no_log()
    {
        using var scratch = new ExportScratch();
        ExportVideoFormat format = SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P);
        string outputPath = scratch.Combine("dos pasadas.webm");
        string passLog = ExportOutputGuard.TemporaryPath(outputPath) + ".pasadas";
        var first = new ExportSinkRequest(outputPath, ExportContainer.WebM, format, SinkEncoders.For("libvpx-vp9"),
            ["-b:v", "300k", "-deadline", "good", "-cpu-used", "8"], null, null, null, [], 30) { Pass = 1, PassCount = 2, PassLogPath = passLog };
        var source = new SyntheticFrames(format);
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg);

        ExportSinkResult analysis = SinkRuns.Run(sink, first, source).Result;
        Assert.False(File.Exists(outputPath));
        Assert.NotEmpty(Directory.EnumerateFiles(scratch.Directory, Path.GetFileName(passLog) + "*"));
        ExportSinkResult final = SinkRuns.Run(sink, first with { Pass = 2 }, source).Result;

        Assert.Equal(passLog, analysis.OutputPath);
        Assert.Equal(outputPath, final.OutputPath);
        Assert.Equal([outputPath], scratch.Leftovers());
        Assert.Equal(30, ExportVerifier.For(SinkRuns.Ffmpeg).Inspect(outputPath).Video!.FrameCount);
    }

    [NvencFact]
    public void Throughput_of_the_pipe_to_nvenc()
    {
        foreach ((int width, int height, int frames) in new[] { (1920, 1080, 300), (3840, 2160, 120) })
        {
            using var scratch = new ExportScratch();
            ExportVideoFormat format = SinkRuns.Format(width, height, ExportPixelFormat.Nv12);
            string outputPath = scratch.Combine("caudal.mp4");
            var request = new ExportSinkRequest(outputPath, ExportContainer.Mp4, format, SinkEncoders.For("h264_nvenc"),
                ["-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "23"], null, null, null, [], frames);
            using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg, new FfmpegPipeSinkOptions { Priority = FfmpegPipeSinkOptions.PriorityFor(fastest: true) });

            TimeSpan elapsed = SinkRuns.Run(sink, request, new SyntheticFrames(format, padding: 0)).Elapsed;

            double fps = frames / elapsed.TotalSeconds;
            output.WriteLine($"{width}×{height} NV12 → h264_nvenc p4: {fps:0} fps, {fps * format.FrameBytes / 1e6:0} MB/s");
            Assert.Equal(frames, sink.FramesEncoded);
        }
    }

    private static bool IsHardware(string encoder) => encoder.EndsWith("_nvenc", StringComparison.Ordinal) || encoder.EndsWith("_mf", StringComparison.Ordinal);

    private static string[] Split(string arguments) => arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
