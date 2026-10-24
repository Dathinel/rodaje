using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.Running;

/// <summary>
/// Profundidad real de cada códec disponible (docs/hdr.md): un FFV1 de 10 bits con una rampa de 1024 códigos pasa por la
/// exportación entera (decodificador, compositor, conversor, FFmpeg) y el archivo se vuelve a leer con el ffmpeg.exe del
/// entorno para contar los niveles de luma que quedan. También se anota la curva, los primarios y si lleva metadatos HDR.
/// </summary>
[Collection(ExportGpuGroup.Name)]
public sealed class ExportBitDepthTests(ITestOutputHelper output) : IDisposable
{
    private const int Width = 2048;
    private const int Height = 128;

    public static TheoryData<string, string, int> Cases => new()
    {
        { "h264_nvenc", "mp4", 8 },
        { "hevc_nvenc", "mp4", 8 },
        { "hevc_nvenc", "mp4", 10 },
        { "av1_nvenc", "mp4", 10 },
        { "h264_mf", "mp4", 8 },
        { "libsvtav1", "mkv", 10 },
        { "libvpx-vp9", "webm", 8 },
        { "prores_ks", "mov", 10 },
        { "dnxhd", "mov", 10 },
        { "ffv1", "mkv", 10 },
        { "libx264", "mp4", 10 },
        { "libx265", "mp4", 10 },
    };

    [BitDepthExportTheory]
    [MemberData(nameof(Cases))]
    public async Task Each_encoder_keeps_the_levels_of_a_1024_code_ramp(string encoderId, string extension, int bits)
    {
        FfmpegExecutable lgpl = Skip.ProxyExecutable!;
        FfmpegExecutable? gpl = encoderId.StartsWith("libx26", StringComparison.Ordinal) ? GplAddOn() : null;
        if (encoderId.StartsWith("libx26", StringComparison.Ordinal) && gpl is null)
        {
            output.WriteLine("Sin RODAJE_CODIFICADORES_GPL: se omite.");
            return;
        }

        List<EncoderSpec> encoders = [.. await new CatalogEncoderSource(lgpl).GetAsync()];
        if (gpl is not null)
        {
            encoders.AddRange(await CatalogEncoderSource.ForGplAddOn(gpl).GetAsync());
        }

        if (encoders.FirstOrDefault(e => e.Id == encoderId) is not { } spec)
        {
            output.WriteLine($"{encoderId}: no está en este equipo, se omite.");
            return;
        }

        string folder = ExportEndToEndTests.Folder();
        Project project = RampProject(await RampAsync(lgpl, Path.Combine(folder, "rampa 10 bits.mkv")));
        var video = new VideoExportSettings
        {
            Codec = spec.Codec,
            EncoderId = encoderId,
            BitDepth = bits,
            Chroma = spec.Codec is ExportCodec.ProRes or ExportCodec.DnxHr ? ChromaSubsampling.Chroma422 : ChromaSubsampling.Chroma420,
            Profile = encoderId switch { "prores_ks" => "hq", "dnxhd" => "dnxhr_hqx", _ => null },
            RateControl = spec.Codec switch
            {
                ExportCodec.ProRes or ExportCodec.DnxHr => RateControl.ProfileDefined,
                ExportCodec.Ffv1 => RateControl.Lossless,
                _ => RateControl.ConstantQuality(),
            },
        };
        string path = Path.Combine(folder, $"salida {encoderId} {bits}.{extension}");
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = lgpl, GplFfmpeg = gpl });
        ExportJob job = runner.Start(new ExportRequest(project, "sec", path, new ExportSettings
        {
            Container = extension switch { "mkv" => ExportContainer.Mkv, "webm" => ExportContainer.WebM, "mov" => ExportContainer.Mov, _ => ExportContainer.Mp4 },
            Video = video,
        }), encoders);
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());

        ExportVideoReport report = job.Report!.Video!;
        (int levels, int min, int max, int gap) = await LumaLevelsAsync(lgpl, path);
        (int sourceLevels, _, _, _) = await LumaLevelsAsync(lgpl, project.Media[0].Path);
        output.WriteLine($"Fuente: {sourceLevels} niveles.");
        bool hdrSideData = await HasHdrSideDataAsync(path);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{encoderId} {bits} bits → {report.PixelFormat} ({report.Profile}): {levels} niveles de luma (Y {min}..{max} en 10 bits, salto máximo {gap}); curva {report.ColorTransfer}, primarios {report.ColorPrimaries}, matriz {report.ColorSpace}, rango {report.ColorRange}; metadatos HDR: {(hdrSideData ? "sí" : "no")}."));

        int ceiling = bits == 10 ? 877 : 220;
        Assert.True(levels <= ceiling, $"{levels} niveles con un techo de {ceiling}.");
        Assert.True(levels >= (bits == 10 ? 600 : 180), $"Solo {levels} niveles en {bits} bits.");
        Assert.Contains(bits == 10 ? "10" : "p", report.PixelFormat, StringComparison.Ordinal);
    }

    public void Dispose() => ExportEndToEndTests.DeleteFolders();

    private static FfmpegExecutable? GplAddOn()
    {
        if (Environment.GetEnvironmentVariable("RODAJE_CODIFICADORES_GPL") is not { Length: > 0 } package || !File.Exists(package))
        {
            return null;
        }

        string folder = Path.Combine(ExportEndToEndTests.Folder(), "complemento");
        ZipFile.ExtractToDirectory(package, folder);
        return FfmpegExecutable.OpenGplAddOn(Path.Combine(folder, "bin", "ffmpeg.exe"));
    }

    /// <summary>1 s de FFV1 yuv420p10le de rango completo: el código de luma es x/2 (0..1023), croma neutro.</summary>
    private static async Task<string> RampAsync(FfmpegExecutable ffmpeg, string path)
    {
        var frame = new byte[Width * Height * 3];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                BitConverter.TryWriteBytes(frame.AsSpan(((y * Width) + x) * 2), (ushort)(x / 2));
            }
        }

        for (int i = Width * Height * 2; i < frame.Length; i += 2)
        {
            BitConverter.TryWriteBytes(frame.AsSpan(i), (ushort)512);
        }

        string raw = path + ".yuv";
        await using (FileStream file = File.Create(raw))
        {
            for (int i = 0; i < 30; i++)
            {
                await file.WriteAsync(frame);
            }
        }

        await RunAsync(ffmpeg.Path, ["-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "yuv420p10le", "-s", $"{Width}x{Height}",
            "-r", "30", "-i", raw, "-c:v", "ffv1", "-color_range", "pc", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", path]);
        File.Delete(raw);
        return path;
    }

    private static Project RampProject(string media)
    {
        var sequence = new Sequence { Id = "sec", Name = "Rampa", FrameRate = Rational.Fps30, Width = Width, Height = Height };
        var project = new Project { Id = "p", Name = "Rampa", Sequences = [sequence] };
        var reference = new MediaRef { Id = "m", Path = media };
        MediaProbe.Probe(media).ApplyTo(reference);
        project.Media.Add(reference);
        var track = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        track.Clips.Add(new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromSeconds(1) });
        sequence.VideoTracks.Add(track);
        return project;
    }

    /// <summary>Luma del cuadro 10 leído en yuv420p10le (8 bits se amplía sin juntar niveles): distintos, mínimo y máximo de la fila central.</summary>
    private static async Task<(int Levels, int Min, int Max, int MaxGap)> LumaLevelsAsync(FfmpegExecutable ffmpeg, string path)
    {
        byte[] data = await RunAsync(ffmpeg.Path, ["-hide_banner", "-loglevel", "error", "-i", path, "-vf", "select=eq(n\\,10)", "-frames:v", "1",
            "-f", "rawvideo", "-pix_fmt", "yuv420p10le", "pipe:1"]);
        Assert.True(data.Length >= Width * Height * 2, $"{data.Length} bytes leídos.");
        int row = Height / 2 * Width * 2;
        ushort[] luma = [.. Enumerable.Range(0, Width).Select(x => BitConverter.ToUInt16(data, row + (x * 2)))];
        ushort[] sorted = [.. luma.Distinct().Order()];
        int gap = sorted.Zip(sorted.Skip(1), (a, b) => b - a).DefaultIfEmpty(0).Max();
        return (sorted.Length, sorted[0], sorted[^1], gap);
    }

    private static async Task<bool> HasHdrSideDataAsync(string path)
    {
        string ffprobe = Path.Combine(Path.GetDirectoryName(Skip.ProxyExecutable!.Path)!, "ffprobe.exe");
        byte[] json = await RunAsync(ffprobe, ["-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#1", "-show_frames", "-show_streams", "-of", "json", path]);
        string text = System.Text.Encoding.UTF8.GetString(json);
        return text.Contains("Mastering display", StringComparison.OrdinalIgnoreCase) || text.Contains("Content light level", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> RunAsync(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var memory = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memory);
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(exe)} salió con {process.ExitCode}: {await error}");
        return memory.ToArray();
    }
}

/// <summary>Necesita el <c>ffmpeg.exe</c> LGPL y la GPU con texturas (no NVENC: cada códec se omite si falta). Larga: solo con RODAJE_PRUEBAS_LARGAS=1.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BitDepthExportTheoryAttribute : TheoryAttribute
{
    public BitDepthExportTheoryAttribute() => Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.Proxies, Rodaje.TestMedia.Skip.GpuTextures,
        Rodaje.TestMedia.Skip.LongRun);
}
