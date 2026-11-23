using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.Cpu;

/// <summary>
/// Lo que el diálogo deja elegir con secuencias de celular, de captura de juego o de tamaño impar, exportado de verdad por
/// CPU unos pocos cuadros: el plan (<see cref="ExportPlan.Create"/>) con «como la secuencia» y la tasa elegida, sus
/// argumentos de ffmpeg y el archivo comprobado con ffprobe. Si ffmpeg rechaza algo, el diálogo no debe dejar elegirlo.
/// </summary>
public sealed class CpuOddFormatExportTests(ITestOutputHelper output)
{
    private const int Frames = 8;

    [CpuExportTheory]
    [CpuOddCase("h264_mf", ExportContainer.Mp4, 1080, 2400, 30000, 1001)]
    [CpuOddCase("h264_mf", ExportContainer.Mp4, 2340, 1080, 60, 1)]
    [CpuOddCase("h264_mf", ExportContainer.Mp4, 1366, 768, 144, 1)]
    [CpuOddCase("h264_mf", ExportContainer.Mov, 1281, 721, 120, 1)]
    [CpuOddCase("h264_mf", ExportContainer.Mp4, 1920, 1200, 2997003, 100000)]
    [CpuOddCase("h264_mf", ExportContainer.Mp4, 4096, 2160, 60, 1)]
    [CpuOddCase("libsvtav1", ExportContainer.Mp4, 1080, 2400, 144, 1)]
    [CpuOddCase("libsvtav1", ExportContainer.Mkv, 853, 480, 60000, 1001)]
    [CpuOddCase("libsvtav1", ExportContainer.Mp4, 16, 16, 30, 1)]
    [CpuOddCase("libvpx-vp9", ExportContainer.WebM, 2560, 1080, 120, 1)]
    [CpuOddCase("libvpx-vp9", ExportContainer.WebM, 16, 16, 5994, 100)]
    [CpuOddCase("prores_ks", ExportContainer.Mov, 1281, 721, 2997003, 100000)]
    [CpuOddCase("prores_ks", ExportContainer.Mov, 3440, 1440, 24000, 1001)]
    public void Odd_sizes_and_rates_export_as_planned(string encoder, ExportContainer container, int width, int height, long num, long den)
    {
        var rate = new Rational(num, den);
        EncoderSpec spec = CpuExportArguments.Spec(encoder);
        ExportSettings settings = CpuExportArguments.Settings(container, encoder, spec.PixelFormats[0], null);
        settings = settings with { Video = settings.Video! with { FrameRate = rate } };
        using var scratch = new ExportScratch();
        string outputPath = scratch.Combine($"raro {width}x{height}.{container.Extension()}");
        ExportPlan plan = ExportPlan.Create(new ExportRequest(Project(width, height, rate), "sec", outputPath, settings,
            new ExportRange(Time.Zero, Time.FromFrames(Frames, rate))), [spec]);
        ExportVideoFormat format = plan.Video!;
        Assert.Equal(Frames, plan.FrameCount);
        Assert.True(format.Width % 2 == 0 && format.Height % 2 == 0, $"{format.Width}×{format.Height} no es par.");

        var request = new ExportSinkRequest(outputPath, container, format, spec, plan.Arguments.Video, null, null, null, [], plan.FrameCount);
        using var sink = new FfmpegPipeSink(CpuExportEnvironment.Ffmpeg);
        (ExportSinkResult result, TimeSpan elapsed) = LavfiFrames.Synthetic(format, distinct: 4).Run(sink, request);

        output.WriteLine($"{encoder} {format.Width}×{format.Height} a {rate}: {elapsed.TotalMilliseconds:0} ms; {string.Join(' ', sink.Arguments)}");
        Assert.Equal(Frames, result.FramesWritten);
        ExportReport report = new ExportVerifier(CpuExportEnvironment.FfprobePath).Inspect(outputPath);
        output.WriteLine(report.Video?.ToString());
        Assert.Equal((format.Width, format.Height), (report.Video!.Width, report.Video.Height));
        Assert.Equal((long)Frames, report.Video.FrameCount);
    }

    private static Project Project(int width, int height, Rational rate)
    {
        var track = new Track { Id = "v", Kind = TrackKind.Video, Name = "Video 1" };
        track.Clips.Add(new Clip { Id = "a", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromSeconds(2) });
        var sequence = new Sequence { Id = "sec", Name = "Celular", FrameRate = rate, Width = width, Height = height };
        sequence.VideoTracks.Add(track);
        var project = new Project { Id = "p", Name = "Raro", Sequences = [sequence] };
        project.Media.Add(new MediaRef { Id = "m", Path = @"C:\medios\clip.mp4" });
        return project;
    }
}

/// <summary>Una fila de <see cref="CpuOddFormatExportTests"/>; se omite si este ffmpeg.exe no trae el codificador.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class CpuOddCaseAttribute : Xunit.Sdk.DataAttribute
{
    private readonly object[] _row;

    public CpuOddCaseAttribute(string encoder, ExportContainer container, int width, int height, long num, long den)
    {
        _row = [encoder, container, width, height, num, den];
        // En macOS el ffmpeg LGPL sí está, pero esta prueba tumbó el proceso de pruebas en la CI: solo en Windows, que es donde falló el usuario.
        Skip = OperatingSystem.IsWindows() ? CpuExportEnvironment.Encoders(encoder) : "Solo en Windows.";
    }

    public override IEnumerable<object[]> GetData(System.Reflection.MethodInfo testMethod) => [_row];
}
