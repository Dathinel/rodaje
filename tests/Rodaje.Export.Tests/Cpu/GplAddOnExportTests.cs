using System.IO.Compression;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.Cpu;

/// <summary>
/// x264 y x265 por la misma tubería (<see cref="FfmpegPipeSink"/>) con el <c>ffmpeg.exe</c> GPL del complemento, y el
/// archivo comprobado con el ffprobe LGPL del editor. <c>RODAJE_CODIFICADORES_GPL</c> apunta al <c>.paquete</c> de
/// <c>herramientas\armar-codificadores-gpl.ps1</c>; sin él no se comprueba nada.
/// </summary>
public sealed class GplAddOnExportTests(ITestOutputHelper output)
{
    private const int Frames = 45;

    [CpuExportTheory]
    [InlineData("libx264", ExportContainer.Mp4, ExportPixelFormat.Yuv420P)]
    [InlineData("libx264", ExportContainer.Mov, ExportPixelFormat.Yuv420P10)]
    [InlineData("libx265", ExportContainer.Mp4, ExportPixelFormat.Yuv420P10)]
    public void The_gpl_ffmpeg_exports_through_the_same_pipe(string video, ExportContainer container, ExportPixelFormat pixels)
    {
        if (Environment.GetEnvironmentVariable("RODAJE_CODIFICADORES_GPL") is not { Length: > 0 } package || !File.Exists(package))
        {
            output.WriteLine("Sin RODAJE_CODIFICADORES_GPL: se omite.");
            return;
        }

        using var scratch = new ExportScratch();
        string folder = scratch.Combine("complemento");
        ZipFile.ExtractToDirectory(package, folder);
        FfmpegExecutable gpl = FfmpegExecutable.OpenGplAddOn(Path.Combine(folder, "bin", "ffmpeg.exe"));

        EncoderSpec spec = CpuExportArguments.Spec(video);
        Assert.Equal(EncoderLicense.GplAddOn, spec.License);
        EncoderArguments arguments = CpuExportArguments.Build(container, video, pixels, "aac", Frames);
        ExportVideoFormat format = CpuExportEnvironment.Format(640, 360, pixels);
        string wav = CpuExportEnvironment.SineWav(scratch.Combine("mezcla.rodaje-tmp.wav"), Frames / 30.0);
        string outputPath = scratch.Combine($"salida {video}.{container.Extension()}");
        var request = new ExportSinkRequest(outputPath, container, format, spec, arguments.Video,
            ExportAudioFormat.Default, wav, CpuExportArguments.Spec("aac"), arguments.Audio, Frames);
        using var sink = new FfmpegPipeSink(gpl);
        (ExportSinkResult result, TimeSpan elapsed) = new LavfiFrames(format, distinct: 15).Run(sink, request);
        output.WriteLine($"{video} → {container}: {elapsed.TotalMilliseconds:0} ms, {result.Bytes / 1024} KB; {string.Join(' ', sink.Arguments)}");

        Assert.Equal(Frames, result.FramesWritten);
        ExportReport report = new ExportVerifier(CpuExportEnvironment.FfprobePath).Inspect(outputPath);
        IReadOnlyList<string> problems = ExportVerifier.Check(report, new ExportExpectation
        {
            VideoCodec = CpuExportArguments.ProbeCodec(video),
            Width = 640,
            Height = 360,
            FrameCount = Frames,
            FrameRate = Rational.Fps30,
            PixelFormat = pixels.FfmpegName(),
            Color = format.Color,
            AudioCodec = "aac",
            AudioDurationSeconds = Frames / 30.0,
            SampleRate = 48_000,
            Channels = 2,
            FastStart = true,
        });
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
