using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Raw;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
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

    /// <summary>La carpeta entera por el módulo de verdad y el ffmpeg.exe LGPL: el intermedio tiene los cuadros, la cadencia y el gris del 18 %.</summary>
    [FfmpegLibraryFact]
    public async Task A_cinemadng_folder_becomes_a_prores_file_with_the_right_frames_and_gray()
    {
        string? modules = ModuleCatalog.FindDevelopmentModules(AppContext.BaseDirectory);
        await using ModuleManager manager = new(new ModuleManagerOptions { Roots = modules is null ? [] : [new ModuleRoot(modules, ModuleOrigin.Development)] });
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

    [FfmpegLibraryFact]
    public async Task Cancelling_leaves_no_file()
    {
        string? modules = ModuleCatalog.FindDevelopmentModules(AppContext.BaseDirectory);
        await using ModuleManager manager = new(new ModuleManagerOptions { Roots = modules is null ? [] : [new ModuleRoot(modules, ModuleOrigin.Development)] });
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
            CinemaDngImport.Scan(dir)!, new CinemaDngOptions(), outputFolder, "Cancelar", cancel.Token));
        Assert.Empty(Directory.Exists(outputFolder) ? Directory.GetFiles(outputFolder) : []);
    }
}
