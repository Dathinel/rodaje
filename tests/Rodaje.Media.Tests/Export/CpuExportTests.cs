using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Exportaciones cortas de verdad con los codificadores que tiene un equipo sin GPU (el runner de Windows de la CI):
/// cuadros de lavfi por <see cref="FfmpegPipeSink"/>, audio de lavfi como WAV y el archivo comprobado con
/// <see cref="ExportVerifier"/>. Solo se omiten si falta <c>ffmpeg.exe</c> o un codificador no está compilado.
/// </summary>
public sealed class CpuExportTests(ITestOutputHelper output)
{
    private const int Frames = 45;

    [CpuExportTheory]
    [CpuExportCase("h264_mf", ExportContainer.Mp4, ExportPixelFormat.Nv12, "aac_mf")]
    [CpuExportCase("h264_mf", ExportContainer.Mov, ExportPixelFormat.Nv12, "aac")]
    [CpuExportCase("libsvtav1", ExportContainer.Mp4, ExportPixelFormat.Yuv420P, "libopus")]
    [CpuExportCase("libsvtav1", ExportContainer.Mkv, ExportPixelFormat.Yuv420P10, "libopus")]
    [CpuExportCase("libvpx-vp9", ExportContainer.WebM, ExportPixelFormat.Yuv420P, "libopus")]
    [CpuExportCase("prores_ks", ExportContainer.Mov, ExportPixelFormat.Yuv422P10, "pcm_s24le")]
    [CpuExportCase("prores_ks", ExportContainer.Mov, ExportPixelFormat.Yuva444P10, "pcm_s16le")]
    [CpuExportCase("dnxhd", ExportContainer.Mov, ExportPixelFormat.Yuv422P, "pcm_s16le")]
    [CpuExportCase("dnxhd", ExportContainer.Mxf, ExportPixelFormat.Yuv422P10, "pcm_s24le")]
    [CpuExportCase("ffv1", ExportContainer.Mkv, ExportPixelFormat.Yuv420P, "flac")]
    public void A_short_cpu_export_is_what_was_asked(string video, ExportContainer container, ExportPixelFormat pixels, string audio)
    {
        EncoderSpec videoSpec = CpuExportArguments.Spec(video);
        EncoderSpec audioSpec = CpuExportArguments.Spec(audio);
        string? profile = CpuExportArguments.Profile(video, pixels);
        Assert.Empty(ExportCompatibility.Check(container, videoSpec, pixels, audioSpec, profile).Select(p => p.Message));
        Assert.False(videoSpec.IsHardware);

        using var scratch = new ExportScratch();
        ExportVideoFormat format = CpuExportEnvironment.Format(640, 360, pixels);
        var source = new LavfiFrames(format, distinct: 15);
        string wav = CpuExportEnvironment.SineWav(scratch.Combine("mezcla.rodaje-tmp.wav"), Frames / 30.0);
        string outputPath = scratch.Combine($"salida {video}.{container.Extension()}");
        var request = new ExportSinkRequest(outputPath, container, format, videoSpec,
            [.. CpuExportArguments.Video(video, pixels), .. CpuExportArguments.Container(container)],
            ExportAudioFormat.Default, wav, audioSpec, CpuExportArguments.Audio(audio), Frames);
        using var sink = new FfmpegPipeSink(CpuExportEnvironment.Ffmpeg);

        (ExportSinkResult result, TimeSpan elapsed) = source.Run(sink, request);

        output.WriteLine($"{video} + {audio} → {container}: {elapsed.TotalMilliseconds:0} ms, {result.Bytes / 1024} KB");
        output.WriteLine(string.Join(' ', sink.Arguments));
        Assert.Equal(outputPath, result.OutputPath);
        Assert.Equal(Frames, result.FramesWritten);
        Assert.Equal(Frames, sink.FramesEncoded);
        Assert.False(SinkRuns.IsAlive(sink.ProcessId));
        Assert.Equal([outputPath], scratch.Leftovers(wav));

        ExportReport report = new ExportVerifier(CpuExportEnvironment.FfprobePath).Inspect(outputPath);
        output.WriteLine($"{report.Video} / {report.Audio}");
        bool isoBmff = container is ExportContainer.Mp4 or ExportContainer.Mov;
        // h264_mf no escribe el rango en el VUI del H.264, y la caja «colr» de MOV es «nclc», que no tiene campo de rango
        // (en MP4 es «nclx», que sí lo lleva). Así que en MOV el rango solo existe si lo pone el codificador: ffprobe da «».
        // El resto del color sí llega por «nclc». Es un hallazgo de la tarea 20 (ver la entrega), no algo que el sink arregle.
        bool rangeOnlyInBitstream = video == "h264_mf" && container == ExportContainer.Mov;
        IReadOnlyList<string> problems = ExportVerifier.Check(report, new ExportExpectation
        {
            VideoCodec = CpuExportArguments.ProbeCodec(video),
            Width = 640,
            Height = 360,
            FrameCount = Frames,
            FrameRate = Rational.Fps30,
            // ProRes 4444 se decodifica a 12 bits; se comprueba aparte que lleva alfa.
            PixelFormat = pixels switch
            {
                ExportPixelFormat.Nv12 => "yuv420p",
                ExportPixelFormat.Yuva444P10 => null,
                _ => pixels.FfmpegName(),
            },
            Color = rangeOnlyInBitstream ? null : format.Color,
            // FFV1 figura como intra en el catálogo, pero sin -g FFmpeg reinicia sus contextos (cuadro clave) cada 12 cuadros:
            // ver la entrega docs/tareas/2026-10-11-export-ci.md. ProRes y DNxHR sí son un cuadro clave por cuadro.
            MaxGop = videoSpec.IntraOnly && video != "ffv1" ? 1 : null,
            AudioCodec = CpuExportArguments.ProbeCodec(audio),
            AudioDurationSeconds = Frames / 30.0,
            SampleRate = 48_000,
            Channels = 2,
            FastStart = isoBmff,
        });
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        if (rangeOnlyInBitstream)
        {
            ExportVideoReport v = report.Video!;
            Assert.Equal(format.Color.FfmpegColorspace, v.ColorSpace);
            Assert.Equal(format.Color.FfmpegPrimaries, v.ColorPrimaries);
            Assert.True(string.IsNullOrEmpty(v.ColorRange) || v.ColorRange == format.Color.FfmpegRange,
                $"Rango de color «{v.ColorRange}»: o falta (MOV sin rango en el VUI) o es «{format.Color.FfmpegRange}».");
        }

        if (pixels.HasAlpha())
        {
            Assert.StartsWith("yuva444p", report.Video!.PixelFormat, StringComparison.Ordinal);
        }
    }

    [CpuExportTheory]
    [CpuAudioExportCase(ExportContainer.M4a, "aac")]
    [CpuAudioExportCase(ExportContainer.M4a, "aac_mf")]
    [CpuAudioExportCase(ExportContainer.Ogg, "libopus")]
    [CpuAudioExportCase(ExportContainer.Flac, "flac")]
    [CpuAudioExportCase(ExportContainer.Wav, "pcm_s24le")]
    public void An_audio_only_cpu_export_is_what_was_asked(ExportContainer container, string audio)
    {
        EncoderSpec audioSpec = CpuExportArguments.Spec(audio);
        Assert.Empty(ExportCompatibility.Check(container, null, null, audioSpec).Select(p => p.Message));

        using var scratch = new ExportScratch();
        string wav = CpuExportEnvironment.SineWav(scratch.Combine("mezcla.rodaje-tmp.wav"), 1.5);
        string outputPath = scratch.Combine($"solo audio {audio}.{container.Extension()}");
        var request = new ExportSinkRequest(outputPath, container, null, null, [], ExportAudioFormat.Default, wav, audioSpec,
            [.. CpuExportArguments.Audio(audio), .. CpuExportArguments.Container(container)], 0);
        using var sink = new FfmpegPipeSink(CpuExportEnvironment.Ffmpeg);

        sink.Start(request, CancellationToken.None);
        ExportSinkResult result = sink.Complete(CancellationToken.None);

        output.WriteLine(string.Join(' ', sink.Arguments));
        Assert.Equal(outputPath, result.OutputPath);
        Assert.True(result.Bytes > 0);
        Assert.False(SinkRuns.IsAlive(sink.ProcessId));
        Assert.Equal([outputPath], scratch.Leftovers(wav));
        ExportReport report = new ExportVerifier(CpuExportEnvironment.FfprobePath).Inspect(outputPath);
        output.WriteLine($"{report.FormatName}: {report.Audio}");
        Assert.Null(report.Video);
        IReadOnlyList<string> problems = ExportVerifier.Check(report, new ExportExpectation
        {
            AudioCodec = CpuExportArguments.ProbeCodec(audio),
            AudioDurationSeconds = 1.5,
            SampleRate = 48_000,
            Channels = 2,
            FastStart = container == ExportContainer.M4a,
        });
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
