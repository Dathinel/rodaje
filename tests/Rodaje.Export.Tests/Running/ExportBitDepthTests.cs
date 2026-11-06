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
using Rodaje.Render.Export;
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

    public static TheoryData<string, string, SequenceColorSpace> HdrCases => new()
    {
        { "hevc_nvenc", "mp4", SequenceColorSpace.HdrPq },
        { "hevc_nvenc", "mp4", SequenceColorSpace.HdrHlg },
        { "libsvtav1", "mkv", SequenceColorSpace.HdrPq },
        { "prores_ks", "mov", SequenceColorSpace.HdrPq },
        { "libx265", "mp4", SequenceColorSpace.HdrPq },
    };

    [BitDepthExportTheory]
    [MemberData(nameof(Cases))]
    public async Task Each_encoder_keeps_the_levels_of_a_1024_code_ramp(string encoderId, string extension, int bits)
    {
        if (await ExportRampAsync(encoderId, extension, bits, SequenceColorSpace.Sdr709, null) is not (ExportJob job, string path, Project project, VideoExportSettings video))
        {
            return;
        }

        ExportVideoReport report = job.Report!.Video!;
        (int levels, int min, int max, int gap) = await LumaLevelsAsync(Skip.ProxyExecutable!, path);
        (int sourceLevels, _, _, _) = await LumaLevelsAsync(Skip.ProxyExecutable!, project.Media[0].Path);
        output.WriteLine($"Fuente: {sourceLevels} niveles.");
        Assert.Equal(1024, sourceLevels);
        bool hdrSideData = await HasHdrSideDataAsync(path);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{encoderId} {bits} bits → {report.PixelFormat} ({report.Profile}): {levels} niveles de luma (Y {min}..{max} en 10 bits, salto máximo {gap}); curva {report.ColorTransfer}, primarios {report.ColorPrimaries}, matriz {report.ColorSpace}, rango {report.ColorRange}; metadatos HDR: {(hdrSideData ? "sí" : "no")}."));

        int ceiling = bits == 10 ? 877 : 220;
        Assert.True(levels <= ceiling, $"{levels} niveles con un techo de {ceiling}.");
        Assert.True(levels >= (bits == 10 ? 600 : 180), $"Solo {levels} niveles en {bits} bits.");
        if (video.RateControl.Mode == RateControlMode.Lossless)
        {
            Assert.Equal(ceiling, levels);
        }
        Assert.Contains(bits == 10 ? "10" : "p", report.PixelFormat, StringComparison.Ordinal);
        Assert.False(hdrSideData, "Una exportación SDR no lleva metadatos HDR.");
    }

    /// <summary>
    /// Fase C, parte 1: la misma rampa SDR en una secuencia HDR sale etiquetada BT.2020 con PQ o HLG, el blanco cae en el
    /// código de 203 nits (≈ 573 en PQ, 721 en HLG) y PQ lleva la pantalla de masterizado y el MaxCLL/MaxFALL pedidos; HLG, nada.
    /// </summary>
    [BitDepthExportTheory]
    [MemberData(nameof(HdrCases))]
    public async Task An_hdr_sequence_exports_bt2020_with_its_curve_and_metadata(string encoderId, string extension, SequenceColorSpace space)
    {
        bool pq = space == SequenceColorSpace.HdrPq;
        ExportColor? requested = pq ? ExportColor.Hdr10 with { MaxCll = 1000, MaxFall = 400 } : null;
        if (await ExportRampAsync(encoderId, extension, 10, space, requested) is not (ExportJob job, string path, _, _))
        {
            return;
        }

        ExportVideoReport report = job.Report!.Video!;
        (int levels, int min, int max, _) = await LumaLevelsAsync(Skip.ProxyExecutable!, path);
        string probe = await ProbeJsonAsync(path);
        bool mastering = probe.Contains("Mastering display", StringComparison.OrdinalIgnoreCase);
        bool light = probe.Contains("Content light level", StringComparison.OrdinalIgnoreCase);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{encoderId} {space} → {report.PixelFormat}: {levels} niveles (Y {min}..{max}); curva {report.ColorTransfer}, primarios {report.ColorPrimaries}, matriz {report.ColorSpace}; metadatos HDR: {(mastering || light ? "sí" : "no")} (masterizado {mastering}, nivel de luz {light})."));

        Assert.Equal(pq ? "smpte2084" : "arib-std-b67", report.ColorTransfer);
        Assert.Equal("bt2020", report.ColorPrimaries);
        Assert.Equal("bt2020nc", report.ColorSpace);
        int white = pq ? 573 : 721;
        Assert.InRange(max, white - 8, white + 8);
        Assert.InRange(min, 56, 72);
        if (pq)
        {
            Assert.True(mastering && light, "Faltan los metadatos HDR10.");
            Assert.Contains("\"max_content\": 1000", probe, StringComparison.Ordinal);
            Assert.Contains("\"max_average\": 400", probe, StringComparison.Ordinal);
            Assert.Matches("\"max_luminance\": \"(1000/1|10000000/10000|256000/256)\"", probe);
        }
        else
        {
            Assert.False(mastering || light, "HLG no lleva SEI de masterizado ni de nivel de luz.");
        }
    }

    public static TheoryData<string, string> PqPatchCases => new()
    {
        { "hevc_nvenc", "mp4" },
        { "prores_ks", "mov" },
    };

    /// <summary>
    /// Fase C, parte 2 (D-055): el compositor de una secuencia HDR trabaja en lineal sin recortar, así que un clip PQ con un parche de
    /// 1000 nits (código ≈ 723 en 10 bits) llega al archivo entero, el blanco de 203 nits sigue en ≈ 573 y el negro en 64.
    /// </summary>
    [BitDepthExportTheory]
    [MemberData(nameof(PqPatchCases))]
    public async Task A_1000_nit_pq_patch_reaches_the_file_without_clipping(string encoderId, string extension)
    {
        if (await ExportRampAsync(encoderId, extension, 10, SequenceColorSpace.HdrPq, null, pqPatch: true) is not (ExportJob job, string path, _, _))
        {
            return;
        }

        ExportVideoReport report = job.Report!.Video!;
        (int levels, int min, int max, _) = await LumaLevelsAsync(Skip.ProxyExecutable!, path);
        output.WriteLine($"{encoderId} parche PQ → {report.PixelFormat}: {levels} niveles (Y {min}..{max}); curva {report.ColorTransfer}, primarios {report.ColorPrimaries}.");
        Assert.Equal("smpte2084", report.ColorTransfer);
        Assert.InRange(max, 715, 731);
        Assert.InRange(min, 56, 72);
    }

    /// <summary>
    /// Fase C, parte 3 (D-058): con «Medir MaxCLL/MaxFALL» la pasada previa mide la composición lineal y el archivo lleva lo medido en
    /// vez de lo declarado: el parche de 1000 nits (código 723 ≈ 1004 nits) solo en el cuadro 10 y 203 nits en el resto dan un
    /// MaxCLL de ≈ 1000 y un MaxFALL entre 203 y 1000 (la media del cuadro 10: un tercio negro, un tercio a 203 y un tercio a 1000).
    /// </summary>
    [BitDepthExportTheory]
    [MemberData(nameof(PqPatchCases))]
    public async Task Measured_light_levels_reach_the_file(string encoderId, string extension)
    {
        if (await ExportRampAsync(encoderId, extension, 10, SequenceColorSpace.HdrPq, null, pqPatch: true, patchFrame: 10, measureLight: true)
            is not (ExportJob job, string path, _, _))
        {
            return;
        }

        string probe = await ProbeJsonAsync(path);
        int maxContent = ProbeNumber(probe, "max_content");
        int maxAverage = ProbeNumber(probe, "max_average");
        ContentLight measured = Assert.IsType<ContentLight>(job.LightLevels);
        output.WriteLine($"{encoderId} medidos: MaxCLL {measured.MaxCll}, MaxFALL {measured.MaxFall}; en el archivo: {maxContent}/{maxAverage}.");
        Assert.InRange(measured.MaxCll, 995, 1012);
        Assert.InRange(measured.MaxFall, 204, 999);
        Assert.Equal(measured.MaxCll, maxContent);
        Assert.Equal(measured.MaxFall, maxAverage);
    }

    private static int ProbeNumber(string probe, string field)
    {
        string label = $"\"{field}\": ";
        int start = probe.IndexOf(label, StringComparison.Ordinal);
        Assert.True(start >= 0, $"ffprobe no informó {field}.");
        start += label.Length;
        int end = start;
        while (end < probe.Length && char.IsAsciiDigit(probe[end]))
        {
            end++;
        }

        return int.Parse(probe.AsSpan(start, end - start), CultureInfo.InvariantCulture);
    }

    /// <summary>Exporta la rampa (o el parche PQ) con un codificador; <c>null</c> si el codificador no está en este equipo.</summary>
    private async Task<(ExportJob Job, string Path, Project Project, VideoExportSettings Video)?> ExportRampAsync(string encoderId, string extension,
        int bits, SequenceColorSpace space, ExportColor? color, bool pqPatch = false, int? patchFrame = null, bool measureLight = false)
    {
        FfmpegExecutable lgpl = Skip.ProxyExecutable!;
        FfmpegExecutable? gpl = encoderId.StartsWith("libx26", StringComparison.Ordinal) ? GplAddOn() : null;
        if (encoderId.StartsWith("libx26", StringComparison.Ordinal) && gpl is null)
        {
            output.WriteLine("Sin RODAJE_CODIFICADORES_GPL: se omite.");
            return null;
        }

        List<EncoderSpec> encoders = [.. await new CatalogEncoderSource(lgpl).GetAsync()];
        if (gpl is not null)
        {
            encoders.AddRange(await CatalogEncoderSource.ForGplAddOn(gpl).GetAsync());
        }

        if (encoders.FirstOrDefault(e => e.Id == encoderId) is not { } spec)
        {
            output.WriteLine($"{encoderId}: no está en este equipo, se omite.");
            return null;
        }

        string folder = ExportEndToEndTests.Folder();
        Project project = RampProject(pqPatch
            ? await PqPatchAsync(lgpl, Path.Combine(folder, "parche pq.mkv"), patchFrame)
            : await RampAsync(lgpl, Path.Combine(folder, "rampa 10 bits.mkv")));
        project.Sequences[0].ColorSpace = space;
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
            Color = color,
            MeasureLightLevels = measureLight,
        };
        string path = Path.Combine(folder, $"salida {encoderId} {bits} {space}.{extension}");
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = lgpl, GplFfmpeg = gpl });
        ExportJob job = runner.Start(new ExportRequest(project, "sec", path, new ExportSettings
        {
            Container = extension switch { "mkv" => ExportContainer.Mkv, "webm" => ExportContainer.WebM, "mov" => ExportContainer.Mov, _ => ExportContainer.Mp4 },
            Video = video,
        }), encoders);
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
        return (job, path, project, video);
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

        // El rango y la matriz van también en la entrada: sin ellos ffmpeg toma el crudo por limitado BT.601 y lo estira a
        // completo BT.709, y el archivo nace con 877 niveles en vez de 1024.
        string[] color = ["-color_range", "pc", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"];
        await RunAsync(ffmpeg.Path, ["-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "yuv420p10le", "-s", $"{Width}x{Height}",
            "-r", "30", .. color, "-i", raw, "-c:v", "ffv1", .. color, path]);
        File.Delete(raw);
        return path;
    }

    /// <summary>
    /// 1 s de FFV1 yuv420p10le PQ BT.2020 limitado: un tercio negro (64), un tercio a 203 nits (573) y un tercio a 1000 nits (723).
    /// Con <paramref name="patchFrame"/>, el tercio de 1000 nits solo está en ese cuadro; en los demás queda a 203.
    /// </summary>
    private static async Task<string> PqPatchAsync(FfmpegExecutable ffmpeg, string path, int? patchFrame = null)
    {
        byte[] Frame(bool patch)
        {
            var frame = new byte[Width * Height * 3];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    ushort code = x < Width / 3 ? (ushort)64 : x < 2 * Width / 3 || !patch ? (ushort)573 : (ushort)723;
                    BitConverter.TryWriteBytes(frame.AsSpan(((y * Width) + x) * 2), code);
                }
            }

            for (int i = Width * Height * 2; i < frame.Length; i += 2)
            {
                BitConverter.TryWriteBytes(frame.AsSpan(i), (ushort)512);
            }

            return frame;
        }

        byte[] withPatch = Frame(true);
        byte[] withoutPatch = Frame(false);
        string raw = path + ".yuv";
        await using (FileStream file = File.Create(raw))
        {
            for (int i = 0; i < 30; i++)
            {
                await file.WriteAsync(patchFrame is null || patchFrame == i ? withPatch : withoutPatch);
            }
        }

        string[] color = ["-color_range", "tv", "-colorspace", "bt2020nc", "-color_primaries", "bt2020", "-color_trc", "smpte2084"];
        await RunAsync(ffmpeg.Path, ["-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "yuv420p10le", "-s", $"{Width}x{Height}",
            "-r", "30", .. color, "-i", raw, "-c:v", "ffv1", .. color, path]);
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
        string text = await ProbeJsonAsync(path);
        return text.Contains("Mastering display", StringComparison.OrdinalIgnoreCase) || text.Contains("Content light level", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Flujo de video y primer cuadro con sus datos adjuntos (ffprobe en JSON).</summary>
    private static async Task<string> ProbeJsonAsync(string path)
    {
        string ffprobe = Path.Combine(Path.GetDirectoryName(Skip.ProxyExecutable!.Path)!, "ffprobe.exe");
        byte[] json = await RunAsync(ffprobe, ["-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#1", "-show_frames", "-show_streams", "-of", "json", path]);
        return System.Text.Encoding.UTF8.GetString(json);
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
