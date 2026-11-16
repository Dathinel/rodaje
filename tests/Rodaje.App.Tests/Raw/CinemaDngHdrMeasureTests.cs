using System.Diagnostics;
using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Raw;
using Rodaje.Core.Model;
using Rodaje.Core.Raw;
using Rodaje.Media.Gpu;
using Rodaje.Media.Proxies;
using Rodaje.Modules.Protocol;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Raw;

public sealed class CinemaDngHdrMeasureTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(SequenceColorSpace.Sdr709, false)]
    [InlineData(SequenceColorSpace.HdrPq, true)]
    [InlineData(SequenceColorSpace.HdrHlg, true)]
    public void The_dialog_proposes_the_intermediate_of_the_active_sequence(SequenceColorSpace space, bool hdr) =>
        Assert.Equal(hdr ? CinemaDngIntermediate.HdrPq : CinemaDngIntermediate.Sdr709, CinemaDngImport.DefaultIntermediate(space));

    /// <summary>
    /// Las cifras de la fase 4 (docs/propuestas/hdr-12-bits.md): 24 cuadros 4K DCI sintéticos importados en SDR y en HDR por
    /// la GPU del equipo (si hay) y por LibRaw; el HDR tarda como mucho 1,25 veces el SDR y los planos cuestan menos de 40 ms.
    /// </summary>
    [MeasureFact]
    public async Task Hdr_import_costs_at_most_a_quarter_more_than_sdr()
    {
        string? modules = ModuleCatalog.FindDevelopmentModules(AppContext.BaseDirectory);
        await using ModuleManager manager = new(new ModuleManagerOptions
        {
            Roots = modules is null ? [] : [new ModuleRoot(modules, ModuleOrigin.Development)],
            PingInterval = Timeout.InfiniteTimeSpan,
        });
        manager.Refresh();
        if (manager.FindByFunction(CinemaDngImport.MosaicFunction) is not { } module || !File.Exists(Path.Combine(Path.GetDirectoryName(module.EntryPath)!, "libraw.dll")))
        {
            output.WriteLine("Sin modulo-raw compilado con libraw.dll: se omite.");
            return;
        }

        const int Width = 4096, Height = 2160, Frames = 24;
        byte[] rgba = new byte[Width * Height * 8], planes = new byte[Width * Height * 6];
        GbrPlanes.FromRgbaHalf(rgba, planes);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
        {
            GbrPlanes.FromRgbaHalf(rgba, planes);
        }

        double planesMs = clock.Elapsed.TotalMilliseconds / 5;
        output.WriteLine($"GbrPlanes 4K: {planesMs:0.0} ms por cuadro.");

        string dir = Path.Combine(_folder.Path, "Toma 4K");
        Directory.CreateDirectory(dir);
        byte[] dng = (SyntheticDng.Chart(Width, Height) with { FrameRate = (24, 1) }).Write();
        for (int i = 0; i < Frames; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, $"T_{i:000}.dng"), dng);
        }

        bool hardware = CinemaDngImport.HardwareDevice() is { } probe && Dispose(probe);
        var paths = new List<(string Name, Func<GpuDevice?> Device)> { ("LibRaw", () => null) };
        if (hardware)
        {
            paths.Insert(0, ("GPU", CinemaDngImport.HardwareDevice));
        }

        foreach ((string name, Func<GpuDevice?> device) in paths)
        {
            // Una vuelta corta antes de medir: el primer cuadro carga LibRaw, el shader y ffmpeg.
            CinemaDngFolder all = CinemaDngImport.Scan(dir)!;
            File.Delete((await CinemaDngImport.RunAsync(manager, module, FfmpegExecutable.Locate(), all with { Frames = all.Frames.Take(2).ToList() },
                new CinemaDngOptions(HighQuality: false), Path.Combine(_folder.Path, "medios"), $"{name} en frío", device)).Path);
            var seconds = new Dictionary<CinemaDngIntermediate, double>();
            foreach (CinemaDngIntermediate intermediate in (CinemaDngIntermediate[])[CinemaDngIntermediate.Sdr709, CinemaDngIntermediate.HdrPq])
            {
                clock.Restart();
                CinemaDngResult result = await CinemaDngImport.RunAsync(manager, module, FfmpegExecutable.Locate(), CinemaDngImport.Scan(dir)!,
                    new CinemaDngOptions(HighQuality: false, Intermediate: intermediate), Path.Combine(_folder.Path, "medios"), $"{name} {intermediate}", device);
                seconds[intermediate] = clock.Elapsed.TotalSeconds;
                output.WriteLine($"{name} {intermediate} ({result.Developer}): {seconds[intermediate] * 1000 / Frames:0} ms por cuadro en total, "
                    + $"{result.MillisecondsPerFrame:0} ms de revelado, {result.MegabytesPerFrame:0.0} MB por cuadro.");
                File.Delete(result.Path);
            }

            output.WriteLine($"{name}: HDR / SDR = {seconds[CinemaDngIntermediate.HdrPq] / seconds[CinemaDngIntermediate.Sdr709]:0.00}.");
            Assert.True(seconds[CinemaDngIntermediate.HdrPq] <= 1.25 * seconds[CinemaDngIntermediate.Sdr709], $"{name}: el HDR tarda más de 1,25 veces el SDR.");
        }

        Assert.True(planesMs < 40, $"GbrPlanes tarda {planesMs:0.0} ms por cuadro 4K.");

        static bool Dispose(GpuDevice gpu)
        {
            gpu.Dispose();
            return true;
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class MeasureFactAttribute : FactAttribute
{
    public MeasureFactAttribute() => Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.LongRun, Rodaje.TestMedia.Skip.FfmpegLibraries);
}
