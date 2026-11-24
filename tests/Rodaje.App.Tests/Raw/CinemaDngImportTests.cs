using System.Text.Json;
using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Raw;
using Rodaje.Core.Raw;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Modules.Protocol;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Raw;

public sealed class CinemaDngImportTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Frames_are_sorted_by_number_and_a_single_wav_is_the_sound()
    {
        string dir = Path.Combine(_folder.Path, "A001_C002");
        Directory.CreateDirectory(dir);
        foreach (string name in (string[])["A_10.dng", "A_9.dng", "A_100.DNG", "A_0001.dng", "notas.txt"])
        {
            File.WriteAllBytes(Path.Combine(dir, name), [1, 2, 3]);
        }

        File.WriteAllBytes(Path.Combine(dir, "toma.wav"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "vacio.dng"), []);
        CinemaDngFolder found = CinemaDngImport.Scan(Path.Combine(dir, "A_9.dng"))!;
        Assert.Equal(["A_0001.dng", "A_9.dng", "A_10.dng", "A_100.DNG"], found.Frames.Select(Path.GetFileName));
        Assert.Equal("toma.wav", Path.GetFileName(found.Audio));
        Assert.Equal("A001_C002", found.Name);
        Assert.Equal(found.Frames, CinemaDngImport.Scan(dir)!.Frames);

        File.WriteAllBytes(Path.Combine(dir, "otra.wav"), [1]);
        Assert.Null(CinemaDngImport.Scan(dir)!.Audio);
        Assert.Null(CinemaDngImport.Scan(_folder.Path));
    }

    [Theory]
    [InlineData("a2", "a10", -1)]
    [InlineData("a010", "a10", 0)]
    [InlineData("B", "a", 1)]
    [InlineData("clip_1_5", "clip_1_10", -1)]
    public void Natural_order(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(CinemaDngImport.NaturalCompare(a, b)));

    [Fact]
    public void Batches_stay_under_the_shared_memory_budget()
    {
        long frame4k = 4096L * 2160 * 8;
        int batch = CinemaDngImport.BatchSize(frame4k, 18_000_000);
        Assert.InRange(batch, 1, 8);
        Assert.True(batch * (frame4k + 18_000_000) <= CinemaDngImport.MaxBatchBytes);
        Assert.Equal(1, CinemaDngImport.BatchSize(CinemaDngImport.MaxBatchBytes * 2, 0));
    }

    [Fact]
    public void Encoder_arguments_use_prores_hq_in_rec709_and_the_take_sound()
    {
        IReadOnlyList<string> args = CinemaDngImport.EncoderArguments(4096, 2160, 24_000, 1_001, @"C:\toma.wav", @"C:\salida.mov");
        string line = string.Join(' ', args);
        Assert.Contains("-pix_fmt rgbaf16le -s 4096x2160 -framerate 24000/1001 -i pipe:0 -i C:\\toma.wav", line, StringComparison.Ordinal);
        Assert.Contains("-c:v prores_ks -profile:v hq -pix_fmt yuv422p10le", line, StringComparison.Ordinal);
        Assert.Contains("scale=out_color_matrix=bt709", line, StringComparison.Ordinal);
        Assert.Contains("-map 1:a:0? -c:a pcm_s24le", line, StringComparison.Ordinal);
        Assert.DoesNotContain("1:a", string.Join(' ', CinemaDngImport.EncoderArguments(64, 32, 25, 1, null, "x.mov")), StringComparison.Ordinal);
    }

    /// <summary>
    /// Sin vigilante: aquí se prueba la importación, no el ping. Su regla de 10 s de reloj mató al módulo sano en la CI con
    /// la máquina parada (ModuleHungException a los 18 s); el vigilante se prueba sin reloj en BusyModulePingTests.
    /// </summary>
    private static readonly TimeSpan NoWatchdog = Timeout.InfiniteTimeSpan;

    /// <summary>La carpeta entera por el módulo de verdad y el ffmpeg.exe LGPL: el intermedio tiene los cuadros, la cadencia y el gris del 18 %.</summary>
    [FfmpegLibraryFact]
    public async Task A_cinemadng_folder_becomes_a_prores_file_with_the_right_frames_and_gray()
    {
        string? modules = ModuleCatalog.FindDevelopmentModules(AppContext.BaseDirectory);
        await using ModuleManager manager = new(new ModuleManagerOptions { Roots = modules is null ? [] : [new ModuleRoot(modules, ModuleOrigin.Development)], PingInterval = NoWatchdog });
        manager.Refresh();
        if (manager.FindByFunction(CinemaDngImport.Function) is not { } module || !File.Exists(Path.Combine(Path.GetDirectoryName(module.EntryPath)!, "libraw.dll")))
        {
            output.WriteLine("Sin modulo-raw compilado con libraw.dll: se omite.");
            return;
        }

        const int Width = 640, Height = 360, Frames = 12;
        string dir = Path.Combine(_folder.Path, "Toma 1");
        Directory.CreateDirectory(dir);
        byte[] dng = (SyntheticDng.Chart(Width, Height) with { FrameRate = (25, 1) }).Write();
        for (int i = 0; i < Frames; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, $"Toma_{i:00000}.dng"), dng);
        }

        CinemaDngFolder sequence = CinemaDngImport.Scan(dir)!;
        var failures = new List<ModuleFailure>();
        manager.Failed += (_, f) => failures.Add(f);
        string outputFolder = Path.Combine(_folder.Path, "medios");
        CinemaDngResult result = await CinemaDngImport.RunAsync(manager, module, FfmpegExecutable.Locate(), sequence, new CinemaDngOptions(),
            outputFolder, "CinemaDNG de prueba");
        output.WriteLine($"{result}");
        Assert.Empty(failures);
        Assert.Equal(Path.Combine(outputFolder, "Toma 1.mov"), result.Path);
        Assert.Empty(Directory.GetFiles(outputFolder, "*.rodaje-tmp"));

        MediaInfo info = MediaProbe.Probe(result.Path);
        Assert.Equal("prores", info.Video!.Codec);
        Assert.Equal((Width, Height), (info.Video.Width, info.Video.Height));
        Assert.Equal(Frames, info.Video.FrameCount);
        Assert.Equal(25.0, info.Video.FrameRate.ToDouble(), 3);

        using ThumbnailDecoder decoder = ThumbnailDecoder.Open(result.Path);
        BgraImage image = decoder.Decode(Time.FromSeconds(0.2), Height)!;
        (int x, int y) = SyntheticDng.PatchCenter(0, Width, Height);
        int at = ((y * image.Width) + x) * 4;
        double expected = 1.099 * Math.Pow(0.18, 0.45) - 0.099;
        output.WriteLine($"Gris 18 %: BGRA {image.Pixels[at]} {image.Pixels[at + 1]} {image.Pixels[at + 2]}; esperado {expected * 255:0}");
        for (int c = 0; c < 3; c++)
        {
            Assert.InRange(image.Pixels[at + c], (expected * 255) - 4, (expected * 255) + 4);
        }

        // La segunda importación de la misma carpeta no pisa la primera.
        CinemaDngResult again = await CinemaDngImport.RunAsync(manager, module, FfmpegExecutable.Locate(), sequence with { Frames = sequence.Frames.Take(2).ToList() },
            new CinemaDngOptions(Exposure: 1, HighQuality: false), outputFolder, "CinemaDNG de prueba");
        Assert.Equal(Path.Combine(outputFolder, "Toma 1 (2).mov"), again.Path);
    }

    /// <summary>
    /// El camino de la GPU (docs/hdr.md, fase D) con WARP: el módulo entrega el mosaico («mosaico») y <c>GpuRawDeveloper</c> lo
    /// revela; el ProRes tiene los mismos fotogramas y el mismo gris que por LibRaw, y el resultado dice que fue la GPU.
    /// </summary>
    [FfmpegLibraryFact]
    public async Task The_gpu_path_gives_the_same_prores_from_the_mosaic()
    {
        string? modules = ModuleCatalog.FindDevelopmentModules(AppContext.BaseDirectory);
        await using ModuleManager manager = new(new ModuleManagerOptions { Roots = modules is null ? [] : [new ModuleRoot(modules, ModuleOrigin.Development)], PingInterval = NoWatchdog });
        manager.Refresh();
        if (manager.FindByFunction(CinemaDngImport.MosaicFunction) is not { } module || !File.Exists(Path.Combine(Path.GetDirectoryName(module.EntryPath)!, "libraw.dll")))
        {
            output.WriteLine("Sin modulo-raw compilado con libraw.dll: se omite.");
            return;
        }

        const int Width = 640, Height = 360, Frames = 5;
        string dir = Path.Combine(_folder.Path, "Toma GPU");
        Directory.CreateDirectory(dir);
        byte[] dng = (SyntheticDng.Chart(Width, Height) with { FrameRate = (25, 1) }).Write();
        for (int i = 0; i < Frames; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, $"Toma_{i:00000}.dng"), dng);
        }

        var failures = new List<ModuleFailure>();
        manager.Failed += (_, f) => failures.Add(f);
        string outputFolder = Path.Combine(_folder.Path, "medios");
        CinemaDngResult gpu = await CinemaDngImport.RunAsync(manager, module, FfmpegExecutable.Locate(), CinemaDngImport.Scan(dir)!, new CinemaDngOptions(Exposure: 1),
            outputFolder, "CinemaDNG por GPU", createDevice: GpuDevice.CreateWarp);
        CinemaDngResult cpu = await CinemaDngImport.RunAsync(manager, module, FfmpegExecutable.Locate(), CinemaDngImport.Scan(dir)!, new CinemaDngOptions(Exposure: 1, HighQuality: false),
            outputFolder, "CinemaDNG por CPU", createDevice: () => null);
        output.WriteLine($"{gpu}\n{cpu}");
        Assert.Empty(failures);
        Assert.StartsWith("GPU (", gpu.Developer, StringComparison.Ordinal);
        Assert.Equal("CPU (LibRaw)", cpu.Developer);

        MediaInfo info = MediaProbe.Probe(gpu.Path);
        Assert.Equal("prores", info.Video!.Codec);
        Assert.Equal((Width, Height, Frames), (info.Video.Width, info.Video.Height, (int)info.Video.FrameCount));

        using ThumbnailDecoder fromGpu = ThumbnailDecoder.Open(gpu.Path);
        using ThumbnailDecoder fromCpu = ThumbnailDecoder.Open(cpu.Path);
        BgraImage a = fromGpu.Decode(Time.FromSeconds(0.1), Height)!;
        BgraImage b = fromCpu.Decode(Time.FromSeconds(0.1), Height)!;
        double expected = (1.099 * Math.Pow(0.36, 0.45)) - 0.099;
        for (int i = 0; i < SyntheticDng.ChartPatches.Count; i++)
        {
            (int x, int y) = SyntheticDng.PatchCenter(i, Width, Height);
            int at = ((y * a.Width) + x) * 4;
            for (int c = 0; c < 3; c++)
            {
                Assert.InRange(a.Pixels[at + c], b.Pixels[at + c] - 3, b.Pixels[at + c] + 3);
            }

            if (i == 0)
            {
                output.WriteLine($"Gris 18 % a +1 EV: GPU {a.Pixels[at]} {a.Pixels[at + 1]} {a.Pixels[at + 2]}, CPU {b.Pixels[at]} {b.Pixels[at + 1]} {b.Pixels[at + 2]}; esperado {expected * 255:0}");
                Assert.InRange(a.Pixels[at + 1], (expected * 255) - 4, (expected * 255) + 4);
            }
        }
    }

    [Fact]
    public void Mosaic_parameters_are_read_and_checked()
    {
        using JsonDocument good = JsonDocument.Parse("""{"cuadros":[{"patron":"RGGB","blanco":3839,"balance":[1.8,1,1.4],"matriz":[1,0,0,0,1,0,0,0,1]}],"ms":[3.5]}""");
        IReadOnlyList<RawDevelopParameters> frames = CinemaDngImport.ReadMosaicParameters(good.RootElement, 1, new CinemaDngOptions(Exposure: 0.5));
        Assert.Single(frames);
        Assert.Equal("RGGB", frames[0].Pattern.ToString());
        Assert.Equal(3839, frames[0].White);
        Assert.Equal(0.5, frames[0].Exposure);
        Assert.Equal(RawOutputCurve.Rec709, frames[0].Curve);
        Assert.Equal(1.4f, frames[0].Multipliers[2]);
        Assert.Throws<ModuleProtocolException>(() => CinemaDngImport.ReadMosaicParameters(good.RootElement, 2, new CinemaDngOptions()));
        foreach (string json in (string[])
        [
            """{"cuadros":[{"patron":"RGBG","blanco":3839,"balance":[1.8,1,1.4],"matriz":[1,0,0,0,1,0,0,0,1]}]}""",
            """{"cuadros":[{"patron":"RGGB","blanco":0,"balance":[1.8,1,1.4],"matriz":[1,0,0,0,1,0,0,0,1]}]}""",
            """{"cuadros":[{"patron":"RGGB","blanco":3839,"balance":[1.8,"1",1.4],"matriz":[1,0,0,0,1,0,0,0,1]}]}""",
            """{"cuadros":[{"patron":"RGGB","blanco":3839,"balance":[1.8,1,1.4],"matriz":[1,0,0]}]}""",
            """{"cuadros":{}}""",
            """[]""",
        ])
        {
            using JsonDocument bad = JsonDocument.Parse(json);
            Assert.Throws<ModuleProtocolException>(() => CinemaDngImport.ReadMosaicParameters(bad.RootElement, 1, new CinemaDngOptions()));
        }

        using JsonDocument info = JsonDocument.Parse("""{"ancho":640,"alto":360,"bayer":true}""");
        Assert.True(CinemaDngImport.IsBayer(info.RootElement));
        using JsonDocument old = JsonDocument.Parse("""{"ancho":640,"alto":360}""");
        Assert.False(CinemaDngImport.IsBayer(old.RootElement));
        Assert.False(CinemaDngImport.IsBayer(null));
    }

    /// <summary>
    /// Profundidad que queda tras la importación (docs/hdr.md, fase D): una rampa de los 3840 códigos de un CinemaDNG de
    /// 12 bits, un código cada 4 columnas, por el módulo y el intermedio ProRes; se cuentan los niveles de luma del .mov.
    /// </summary>
    [FfmpegLibraryFact]
    public async Task A_12_bit_ramp_reaches_the_prores_intermediate_with_its_levels_counted()
    {
        string? modules = ModuleCatalog.FindDevelopmentModules(AppContext.BaseDirectory);
        await using ModuleManager manager = new(new ModuleManagerOptions { Roots = modules is null ? [] : [new ModuleRoot(modules, ModuleOrigin.Development)], PingInterval = NoWatchdog });
        manager.Refresh();
        if (manager.FindByFunction(CinemaDngImport.Function) is not { } module || !File.Exists(Path.Combine(Path.GetDirectoryName(module.EntryPath)!, "libraw.dll")))
        {
            output.WriteLine("Sin modulo-raw compilado con libraw.dll: se omite.");
            return;
        }

        const int Levels = 3840, Block = 4, Width = Levels * Block, Height = 32;
        string dir = Path.Combine(_folder.Path, "Rampa");
        Directory.CreateDirectory(dir);
        byte[] dng = new SyntheticDng
        {
            Width = Width,
            Height = Height,
            Neutral = [1.0, 1.0, 1.0],
            FrameRate = (25, 1),
            Scene = (x, _) => { double v = x / Block / (double)(Levels - 1); return (v, v, v); },
        }.Write();
        for (int i = 0; i < 2; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, $"R_{i:000}.dng"), dng);
        }

        FfmpegExecutable ffmpeg = FfmpegExecutable.Locate();
        CinemaDngResult result = await CinemaDngImport.RunAsync(manager, module, ffmpeg, CinemaDngImport.Scan(dir)!,
            new CinemaDngOptions(HighQuality: false), Path.Combine(_folder.Path, "medios"), "Rampa");
        MediaInfo info = MediaProbe.Probe(result.Path);

        var start = new System.Diagnostics.ProcessStartInfo(ffmpeg.Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string a in (string[])["-hide_banner", "-loglevel", "error", "-i", result.Path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuv422p10le", "pipe:1"])
        {
            start.ArgumentList.Add(a);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        using var memory = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memory);
        await process.WaitForExitAsync();
        byte[] data = memory.ToArray();
        int row = Height / 2 * Width * 2;
        ushort[] luma = [.. Enumerable.Range(0, Levels).Select(i => BitConverter.ToUInt16(data, row + (((i * Block) + 2) * 2)))];
        int distinct = luma.Distinct().Count();
        output.WriteLine($"{info.Video!.PixelFormat} {info.Video.BitDepth} bits: {Levels} códigos RAW de 12 bits → {distinct} niveles de luma (Y {luma.Min()}..{luma.Max()}).");
        Assert.InRange(distinct, 600, 1024);
    }

    [FfmpegLibraryFact]
    public async Task Cancelling_leaves_no_file()
    {
        string? modules = ModuleCatalog.FindDevelopmentModules(AppContext.BaseDirectory);
        await using ModuleManager manager = new(new ModuleManagerOptions { Roots = modules is null ? [] : [new ModuleRoot(modules, ModuleOrigin.Development)], PingInterval = NoWatchdog });
        manager.Refresh();
        if (manager.FindByFunction(CinemaDngImport.Function) is not { } module || !File.Exists(Path.Combine(Path.GetDirectoryName(module.EntryPath)!, "libraw.dll")))
        {
            output.WriteLine("Sin modulo-raw compilado con libraw.dll: se omite.");
            return;
        }

        string dir = Path.Combine(_folder.Path, "Larga");
        Directory.CreateDirectory(dir);
        byte[] dng = SyntheticDng.Chart(1920, 1080).Write();
        for (int i = 0; i < 64; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, $"L_{i:000}.dng"), dng);
        }

        string outputFolder = Path.Combine(_folder.Path, "medios");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CinemaDngImport.RunAsync(manager, module, FfmpegExecutable.Locate(),
            CinemaDngImport.Scan(dir)!, new CinemaDngOptions(), outputFolder, "Cancelar", cancellationToken: cancel.Token));
        Assert.Empty(Directory.Exists(outputFolder) ? Directory.GetFiles(outputFolder) : []);
    }

    [Fact]
    public void Hdr_encoder_arguments_write_lossless_ffv1_in_pq_bt2020_from_planes()
    {
        IReadOnlyList<string> args = CinemaDngImport.EncoderArguments(4096, 2160, 24_000, 1_001, @"C:\toma.wav", @"C:\salida.mkv", CinemaDngIntermediate.HdrPq);
        Assert.Equal(
            "-hide_banner -nostdin -loglevel error -y -f rawvideo -pix_fmt gbrpf16le -s 4096x2160 -framerate 24000/1001 -i pipe:0 -i C:\\toma.wav "
            + "-map 0:v:0 -map 1:a:0? -c:a pcm_s24le -shortest "
            + "-vf zscale=tin=linear:pin=2020:min=gbr:rin=full:t=smpte2084:p=2020:m=2020_ncl:r=full:npl=203,format=yuv444p16le "
            + "-c:v ffv1 -level 3 -g 1 -slices 24 -slicecrc 1 -pix_fmt yuv444p16le "
            + "-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -color_range pc -f matroska C:\\salida.mkv",
            string.Join(' ', args));
        Assert.Equal(CinemaDngImport.EncoderArguments(64, 32, 25, 1, null, "x.mov"), CinemaDngImport.EncoderArguments(64, 32, 25, 1, null, "x.mov", CinemaDngIntermediate.Sdr709));
    }

    [Fact]
    public void Hdr_develops_linear_in_bt2020_on_both_paths()
    {
        var hdr = new CinemaDngOptions(Exposure: 2, Intermediate: CinemaDngIntermediate.HdrPq);
        using JsonDocument good = JsonDocument.Parse("""{"cuadros":[{"patron":"RGGB","blanco":3839,"balance":[1.8,1,1.4],"matriz":[1,0,0,0,1,0,0,0,1]}]}""");
        RawDevelopParameters frame = CinemaDngImport.ReadMosaicParameters(good.RootElement, 1, hdr)[0];
        Assert.Equal(RawOutputCurve.Linear, frame.Curve);
        Assert.Equal(RawDevelopParameters.Bt709ToBt2020, frame.Matrix);
        Assert.Equal(2, frame.Exposure);

        using JsonDocument libraw = CinemaDngImport.Parameters(hdr);
        Assert.Equal("rec2020", libraw.RootElement.GetProperty("espacio").GetString());
        Assert.Equal("lineal", libraw.RootElement.GetProperty("curva").GetString());
        using JsonDocument sdr = CinemaDngImport.Parameters(new CinemaDngOptions());
        Assert.Equal("""{"balance":"camara","exposicion":0,"espacio":"rec709","curva":"rec709","calidad":"alta"}""", sdr.RootElement.GetRawText());
    }

    /// <summary>
    /// Criterio de la fase 1 (docs/propuestas/hdr-12-bits.md): la rampa de 12 bits importada en HDR conserva más de 2000
    /// niveles de Y por la GPU (WARP) y por LibRaw, los dos caminos coinciden, y ffprobe ve PQ BT.2020 de rango completo.
    /// </summary>
    [FfmpegLibraryFact]
    public async Task A_12_bit_ramp_keeps_over_2000_levels_in_the_hdr_intermediate_on_both_paths()
    {
        if (await RawModuleAsync() is not { } raw)
        {
            return;
        }

        await using ModuleManager manager = raw.Manager;
        const int Levels = 3840, Block = 4, Width = Levels * Block, Height = 32;
        string dir = WriteFrames("Rampa HDR", new SyntheticDng
        {
            Width = Width,
            Height = Height,
            Neutral = [1.0, 1.0, 1.0],
            FrameRate = (25, 1),
            Scene = (x, _) => { double v = x / Block / (double)(Levels - 1); return (v, v, v); },
        }, 2);

        var hdr = new CinemaDngOptions(HighQuality: false, Intermediate: CinemaDngIntermediate.HdrPq);
        CinemaDngResult gpu = await CinemaDngImport.RunAsync(manager, raw.Module, FfmpegExecutable.Locate(), CinemaDngImport.Scan(dir)!, hdr,
            Path.Combine(_folder.Path, "medios"), "Rampa GPU", createDevice: GpuDevice.CreateWarp);
        CinemaDngResult cpu = await CinemaDngImport.RunAsync(manager, raw.Module, FfmpegExecutable.Locate(), CinemaDngImport.Scan(dir)!, hdr,
            Path.Combine(_folder.Path, "medios"), "Rampa CPU", createDevice: () => null);
        Assert.StartsWith("GPU (", gpu.Developer, StringComparison.Ordinal);
        Assert.Equal("CPU (LibRaw)", cpu.Developer);
        Assert.EndsWith(".mkv", gpu.Path, StringComparison.Ordinal);
        Assert.True(gpu.MegabytesPerFrame > 0);

        MediaInfo info = MediaProbe.Probe(gpu.Path);
        output.WriteLine($"{info.Video!.Codec} {info.Video.PixelFormat} {info.Video.Color}");
        Assert.Equal(("ffv1", "yuv444p16le", 16), (info.Video.Codec, info.Video.PixelFormat, info.Video.BitDepth));
        Assert.Equal(new VideoColor(YuvMatrix.Bt2020, true) { Transfer = HdrTransfer.Pq, Bt2020Primaries = true }, info.Video.Color);

        ushort[] a = await LumaRowAsync(gpu.Path, Width, Height);
        ushort[] b = await LumaRowAsync(cpu.Path, Width, Height);
        ushort[] fromGpu = [.. Enumerable.Range(0, Levels).Select(i => a[(i * Block) + 2])];
        ushort[] fromCpu = [.. Enumerable.Range(0, Levels).Select(i => b[(i * Block) + 2])];
        int worst = Enumerable.Range(0, Levels).Max(i => Math.Abs(fromGpu[i] - fromCpu[i]));
        output.WriteLine($"GPU: {fromGpu.Distinct().Count()} niveles (Y {fromGpu.Min()}..{fromGpu.Max()}); LibRaw: {fromCpu.Distinct().Count()} niveles "
            + $"(Y {fromCpu.Min()}..{fromCpu.Max()}); mayor diferencia {worst} códigos de 16 bits. {gpu.MegabytesPerFrame:0.00} MB por cuadro.");
        Assert.True(fromGpu.Distinct().Count() >= 2000);
        Assert.True(fromCpu.Distinct().Count() >= 2000);
        Assert.InRange(worst, 0, ParityCodes);
    }

    /// <summary>
    /// Lo que guarda el intermedio HDR por encima del blanco: con 0 EV el blanco del sensor da PQ(203 nits); con +2 EV el
    /// mismo blanco da PQ(812 nits), no PQ(203), y un cuarto del blanco sube a PQ(203); igual por la GPU y por LibRaw.
    /// </summary>
    [FfmpegLibraryFact]
    public async Task Highlights_above_white_survive_in_the_hdr_intermediate_on_both_paths()
    {
        if (await RawModuleAsync() is not { } raw)
        {
            return;
        }

        await using ModuleManager manager = raw.Manager;
        const int Width = 128, Height = 64;
        string dir = WriteFrames("Luces", new SyntheticDng
        {
            Width = Width,
            Height = Height,
            Neutral = [1.0, 1.0, 1.0],
            FrameRate = (25, 1),
            Scene = (x, _) => x < Width / 2 ? (1.0, 1.0, 1.0) : (0.25, 0.25, 0.25),
        }, 1);

        int white203 = PqCode(203), white812 = PqCode(812), quarter = PqCode(203 * 0.25);
        foreach ((string name, Func<GpuDevice?> device) in ((string, Func<GpuDevice?>)[])[("GPU", GpuDevice.CreateWarp), ("LibRaw", () => null)])
        {
            foreach ((double ev, int expectedWhite, int expectedQuarter) in ((double, int, int)[])[(0, white203, quarter), (2, white812, white203)])
            {
                CinemaDngResult result = await CinemaDngImport.RunAsync(manager, raw.Module, FfmpegExecutable.Locate(), CinemaDngImport.Scan(dir)!,
                    new CinemaDngOptions(Exposure: ev, Intermediate: CinemaDngIntermediate.HdrPq), Path.Combine(_folder.Path, "medios"), $"Luces {name}",
                    createDevice: device);
                ushort[] row = await LumaRowAsync(result.Path, Width, Height);
                output.WriteLine($"{name} {ev:+0;-0;0} EV: blanco Y {row[Width / 4]} (esperado {expectedWhite}), cuarto Y {row[3 * Width / 4]} (esperado {expectedQuarter}).");
                Assert.InRange(row[Width / 4], expectedWhite - HalfStep(expectedWhite == white203 ? 203 : 812), expectedWhite + 2);
                Assert.InRange(row[3 * Width / 4], expectedQuarter - ParityCodes, expectedQuarter + ParityCodes);
            }
        }
    }

    /// <summary>
    /// Los grises no distinguen primarios ni el orden de los planos: los parches de color de la carta llegan al intermedio
    /// HDR en BT.2020 (la escena sRGB por <see cref="RawDevelopParameters.Bt709ToBt2020"/>, en PQ con 1,0 = 203 nits) y
    /// cada canal en su sitio, por la GPU y por LibRaw (medido: 9 códigos de 16 bits como mucho).
    /// </summary>
    [FfmpegLibraryFact]
    public async Task Chart_colors_reach_the_hdr_intermediate_in_bt2020_on_both_paths()
    {
        if (await RawModuleAsync() is not { } raw)
        {
            return;
        }

        await using ModuleManager manager = raw.Manager;
        const int Width = 384, Height = 64;
        string dir = WriteFrames("Carta HDR", SyntheticDng.Chart(Width, Height) with { FrameRate = (25, 1) }, 1);
        IReadOnlyList<float> m = RawDevelopParameters.Bt709ToBt2020;
        foreach ((string name, Func<GpuDevice?> device) in ((string, Func<GpuDevice?>)[])[("GPU", GpuDevice.CreateWarp), ("LibRaw", () => null)])
        {
            CinemaDngResult result = await CinemaDngImport.RunAsync(manager, raw.Module, FfmpegExecutable.Locate(), CinemaDngImport.Scan(dir)!,
                new CinemaDngOptions(Intermediate: CinemaDngIntermediate.HdrPq), Path.Combine(_folder.Path, "medios"), $"Carta {name}", createDevice: device);
            float[] rgb = await RgbFrameAsync(result.Path, Width, Height);
            for (int patch = 0; patch < SyntheticDng.ChartPatches.Count; patch++)
            {
                (double r, double g, double b) = SyntheticDng.ChartPatches[patch];
                (int x, int y) = SyntheticDng.PatchCenter(patch, Width, Height);
                for (int c = 0; c < 3; c++)
                {
                    int expected = PqCode(203 * ((m[c * 3] * r) + (m[(c * 3) + 1] * g) + (m[(c * 3) + 2] * b)));
                    int actual = (int)Math.Round(rgb[(((c + 2) % 3 * Height * Width) + (y * Width) + x)] * 65535);
                    output.WriteLine($"{name} parche {patch} canal {"RGB"[c]}: {actual} (esperado {expected}).");
                    Assert.InRange(actual, expected - 32, expected + 32);
                }
            }
        }
    }

    /// <summary>El primer cuadro en planos G, B, R de 32 bits sin convertir la curva (códigos PQ de 0 a 1).</summary>
    private static async Task<float[]> RgbFrameAsync(string path, int width, int height)
    {
        var start = new System.Diagnostics.ProcessStartInfo(FfmpegExecutable.Locate().Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string a in (string[])["-hide_banner", "-loglevel", "error", "-i", path, "-frames:v", "1",
            "-vf", "zscale=min=2020_ncl:rin=full:m=gbr:r=full,format=gbrpf32le", "-f", "rawvideo", "pipe:1"])
        {
            start.ArgumentList.Add(a);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        using var memory = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memory);
        await process.WaitForExitAsync();
        float[] data = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(memory.ToArray()).ToArray();
        Assert.Equal(width * height * 3, data.Length);
        return data;
    }

    /// <summary>
    /// Lo que baja el código PQ un escalón de media precisión por debajo de <paramref name="nits"/> (2^-11 relativo, unos 3
    /// códigos): la GPU entrega rgba16f y su blanco puede quedar en 0,99951 en lugar de 1,0; LibRaw da el código exacto.
    /// </summary>
    private static int HalfStep(double nits) => PqCode(nits) - PqCode(nits * (1 - Math.Pow(2, -11))) + 1;

    /// <summary>La tolerancia de D-059 entre la GPU y LibRaw (±3 de 255) llevada a códigos de 16 bits.</summary>
    private const int ParityCodes = 3 * 257;

    /// <summary>El código de 16 bits de rango completo de <paramref name="nits"/> en PQ (SMPTE ST 2084).</summary>
    private static int PqCode(double nits)
    {
        const double M1 = 2610.0 / 16384, M2 = 2523.0 / 4096 * 128, C1 = 3424.0 / 4096, C2 = 2413.0 / 4096 * 32, C3 = 2392.0 / 4096 * 32;
        double y = Math.Pow(nits / 10_000, M1);
        return (int)Math.Round(Math.Pow((C1 + (C2 * y)) / (1 + (C3 * y)), M2) * 65535);
    }

    private async Task<(ModuleManager Manager, ModuleDescriptor Module)?> RawModuleAsync()
    {
        string? modules = ModuleCatalog.FindDevelopmentModules(AppContext.BaseDirectory);
        ModuleManager manager = new(new ModuleManagerOptions { Roots = modules is null ? [] : [new ModuleRoot(modules, ModuleOrigin.Development)], PingInterval = NoWatchdog });
        manager.Refresh();
        if (manager.FindByFunction(CinemaDngImport.MosaicFunction) is { } module && File.Exists(Path.Combine(Path.GetDirectoryName(module.EntryPath)!, "libraw.dll")))
        {
            return (manager, module);
        }

        output.WriteLine("Sin modulo-raw compilado con libraw.dll: se omite.");
        await manager.DisposeAsync();
        return null;
    }

    private string WriteFrames(string name, SyntheticDng dng, int frames)
    {
        string dir = Path.Combine(_folder.Path, name);
        Directory.CreateDirectory(dir);
        byte[] bytes = dng.Write();
        for (int i = 0; i < frames; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, $"F_{i:000}.dng"), bytes);
        }

        return dir;
    }

    /// <summary>La fila del centro del plano Y del primer cuadro, leída a yuv444p16le.</summary>
    private static async Task<ushort[]> LumaRowAsync(string path, int width, int height)
    {
        var start = new System.Diagnostics.ProcessStartInfo(FfmpegExecutable.Locate().Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string a in (string[])["-hide_banner", "-loglevel", "error", "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuv444p16le", "pipe:1"])
        {
            start.ArgumentList.Add(a);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        using var memory = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memory);
        await process.WaitForExitAsync();
        byte[] data = memory.ToArray();
        Assert.True(data.Length >= width * height * 6, $"ffmpeg dio {data.Length} bytes");
        return [.. Enumerable.Range(0, width).Select(x => BitConverter.ToUInt16(data, ((height / 2 * width) + x) * 2))];
    }
}
