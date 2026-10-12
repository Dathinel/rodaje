using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Timing;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Xunit.Abstractions;
using static Rodaje.Export.Tests.Acceptance.AcceptanceSupport;

namespace Rodaje.Export.Tests.Acceptance;

/// <summary>
/// Cancelación: 20 al azar (en la sonda de sonoridad, la mezcla o codificando), cada una ≤1 s, sin archivo, temporal ni
/// <c>ffmpeg.exe</c>, y la VRAM del proceso ±50 MB. Tras matar el proceso que exporta, el arranque siguiente limpia.
/// </summary>
[Collection(ExportAcceptanceGroup.Name)]
public sealed class ExportCancelAcceptanceTests(ITestOutputHelper output)
{
    private const string ChildVariable = "RODAJE_EXPORTAR_HIJO";
    private const long Mb = 1024 * 1024;

    private static readonly ExportSettings Settings = new()
    {
        Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_nvenc", RateControl = RateControl.Vbr(12_000, 18_000) },
        Audio = new AudioExportSettings { Codec = ExportCodec.Aac, Loudness = LoudnessTarget.Platforms },
    };

    [ExportAcceptanceFact]
    public async Task Twenty_random_cancellations_leave_nothing_behind()
    {
        AcceptanceScene scene = Scene(1);
        IReadOnlyList<EncoderSpec> encoders = await ProbedEncoders;
        string folder = Folder("cancelar");
        using GpuDevice probe = GpuDevice.Create();
        await ExportAsync(output, scene.Project, Path.Combine(folder, "calentar.mp4"), Settings, new ExportRange(Time.Zero, Time.FromSeconds(3)));
        File.Delete(Path.Combine(folder, "calentar.mp4"));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long baseline = probe.QueryLocalVideoMemoryUsage() ?? 0;

        var random = new Random(20);
        var stopTimes = new List<double>();
        var states = new List<string>();
        long worstVram = 0;
        long busiest = 0;
        var problems = new List<string>();
        int pid = Environment.ProcessId;
        for (int n = 0; n < 20; n++)
        {
            string path = Path.Combine(folder, $"cancelada {n}.mp4");
            var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Ffmpeg, TemporaryDirectory = folder });
            using ExportJob job = runner.Start(new ExportRequest(scene.Project.Clone(), AcceptanceScene.SequenceId, path, Settings), encoders);
            // La mitad en la preparación o el audio; la otra mitad ya codificando.
            if (n % 2 == 1)
            {
                while (job.Progress.State != ExportJobState.Encoding && !job.Completion.IsCompleted)
                {
                    await Task.Delay(5);
                }
            }

            await Task.Delay(random.Next(0, n % 2 == 1 ? 1500 : 4000));
            var seen = FfmpegChildren(pid);
            ExportJobState state = job.Progress.State;
            busiest = Math.Max(busiest, (probe.QueryLocalVideoMemoryUsage() ?? 0) - baseline);
            var watch = Stopwatch.StartNew();
            job.Cancel();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            watch.Stop();
            stopTimes.Add(watch.Elapsed.TotalMilliseconds);
            states.Add(string.Create(CultureInfo.InvariantCulture, $"{state}@{job.Progress.FramesDone}:{watch.Elapsed.TotalMilliseconds:0}ms"));

            if (job.Progress.State != ExportJobState.Canceled)
            {
                problems.Add($"{n}: terminó en {job.Progress.State}");
            }

            string[] left = Directory.GetFileSystemEntries(folder);
            if (left.Length > 0)
            {
                problems.Add($"{n}: quedó {string.Join(", ", left.Select(Path.GetFileName))}");
            }

            List<int> alive = [.. seen.Where(IsAlive)];
            if (alive.Count > 0)
            {
                problems.Add($"{n}: ffmpeg vivo {string.Join(", ", alive)}");
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            long vram = (probe.QueryLocalVideoMemoryUsage() ?? 0) - baseline;
            worstVram = Math.Max(worstVram, Math.Abs(vram));
            if (Math.Abs(vram) > 50 * Mb)
            {
                problems.Add($"{n}: VRAM {vram / Mb} MB sobre la base");
            }
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"20 cancelaciones: mediana {Percentile(stopTimes, 0.5):0} ms, máximo {stopTimes.Max():0} ms; VRAM del proceso {baseline / Mb} MB de base, hasta +{busiest / Mb} MB exportando, peor tras cancelar ±{worstVram / (double)Mb:0.0} MB. {string.Join(" ", states)}"));
        Delete(folder);
        Assert.True(stopTimes.Max() <= 1000, $"{stopTimes.Max():0} ms");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [ExportAcceptanceFact]
    public async Task After_killing_the_exporting_process_the_next_start_cleans_up()
    {
        string folder = Folder("matar");
        string dotnet = Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.GetDirectoryName(Environment.ProcessPath)!, "dotnet.exe");
        var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in (string[])["test", typeof(ExportCancelAcceptanceTests).Assembly.Location, "--filter", "FullyQualifiedName~Child_exports_until_killed"])
        {
            info.ArgumentList.Add(a);
        }

        info.Environment[ChildVariable] = folder;
        using Process host = Process.Start(info)!;
        _ = host.StandardOutput.ReadToEndAsync();
        _ = host.StandardError.ReadToEndAsync();
        var journal = new ExportJournal(folder);
        var watch = Stopwatch.StartNew();
        int child = -1;
        while (watch.Elapsed < TimeSpan.FromMinutes(3))
        {
            var entries = File.Exists(journal.Path) ? journal.Entries() : Array.Empty<(string, int, string, IReadOnlyList<string>)>();
            if (entries.Count > 0 && Directory.GetFiles(folder, "*" + ExportOutputGuard.TemporaryMarker + "*").Any(f => new FileInfo(f).Length > 1_000_000))
            {
                child = entries[0].Item2;
                break;
            }

            await Task.Delay(100);
        }

        Assert.True(child > 0, "El proceso hijo no llegó a escribir video.");
        List<int> ffmpeg = FfmpegChildren(child);
        string[] before = Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories);
        using (var victim = Process.GetProcessById(child))
        {
            victim.Kill();
            victim.WaitForExit();
        }

        var dying = Stopwatch.StartNew();
        while (ffmpeg.Any(IsAlive) && dying.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(50);
        }

        bool ffmpegGone = !ffmpeg.Any(IsAlive);

        // El arranque siguiente: si algo seguía abierto, la entrada se queda y el otro arranque lo borra.
        var cleaned = new List<string>();
        int starts = 0;
        do
        {
            cleaned.AddRange(ExportRunner.CleanupStale(journal));
            starts++;
        }
        while (journal.Entries().Count > 0 && starts < 5 && await Delay());

        string[] after = [.. Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories).Where(p => !Path.GetFileName(p).StartsWith(ExportJournal.FileName, StringComparison.Ordinal))];
        output.WriteLine($"Hijo {child} con ffmpeg {string.Join(",", ffmpeg)} muerto; ffmpeg terminó solo: {ffmpegGone} en {dying.ElapsedMilliseconds} ms; limpio en {starts} arranque(s). " +
            $"Antes: {string.Join(", ", before.Select(Path.GetFileName))}. Limpiados: {string.Join(", ", cleaned.Select(Path.GetFileName))}. Quedan: {string.Join(", ", after.Select(Path.GetFileName))}.");
        try
        {
            host.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        Delete(folder);
        Assert.NotEmpty(ffmpeg);
        Assert.True(ffmpegGone);
        Assert.Empty(after);
        Assert.Empty(journal.Entries());
    }

    private static async Task<bool> Delay()
    {
        await Task.Delay(1000);
        return true;
    }

    /// <summary>Solo como proceso hijo de la prueba anterior: exporta hasta que lo maten.</summary>
    [ChildFact]
    public async Task Child_exports_until_killed()
    {
        string folder = Environment.GetEnvironmentVariable(ChildVariable)!;
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Ffmpeg, Journal = new ExportJournal(folder), TemporaryDirectory = folder });
        using ExportJob job = runner.Start(new ExportRequest(Scene(1).Project.Clone(), AcceptanceScene.SequenceId, Path.Combine(folder, "salida.mp4"),
            Settings with { Audio = null }), await ProbedEncoders);
        await job.Completion;
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    private sealed class ChildFactAttribute : FactAttribute
    {
        public ChildFactAttribute() => Skip = Environment.GetEnvironmentVariable(ChildVariable) is null ? "Solo como proceso hijo de la prueba de matar la exportación." : null;
    }
}
