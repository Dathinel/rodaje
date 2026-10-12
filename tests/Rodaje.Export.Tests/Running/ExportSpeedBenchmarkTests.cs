using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Probe;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.Running;

/// <summary>
/// Mide la exportación de cada .mp4 de <c>RODAJE_MEDIR_EXPORTAR</c> como un clip suelto a su tamaño (H.264 automático,
/// alto y bajo rendimiento) y de un corte simple (del segundo keyframe al final). Sin la variable no mide: los
/// archivos los genera quien mide.
/// </summary>
[Collection(ExportGpuGroup.Name)]
[Trait("Categoria", "Rendimiento")]
public sealed class ExportSpeedBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Measures_each_file()
    {
        if (Environment.GetEnvironmentVariable("RODAJE_MEDIR_EXPORTAR") is not { Length: > 0 } folder || !Directory.Exists(folder)
            || Skip.ProxyExecutable is null)
        {
            output.WriteLine("Sin RODAJE_MEDIR_EXPORTAR o sin ffmpeg.exe: no se mide.");
            return;
        }

        IReadOnlyList<EncoderSpec> encoders = await new CatalogEncoderSource(Skip.ProxyExecutable).GetAsync();
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable });
        string target = Path.Combine(Path.GetTempPath(), "rodaje-medir-exportar");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(folder, "*.mp4").Order(StringComparer.Ordinal))
        {
            MediaInfo info = MediaProbe.Probe(file);
            Time duration = info.Video!.Duration;
            foreach (ExportPerformance mode in new[] { ExportPerformance.Fastest, ExportPerformance.Background })
            {
                await ExportAsync(runner, encoders, file, info, Time.Zero, duration, mode, Path.Combine(target, $"{Path.GetFileNameWithoutExtension(file)}-{mode}.mp4"),
                    reencode: true);
            }

            // Corte simple: del keyframe de 1 s (GOP de 30) al final, sin cambios.
            await ExportAsync(runner, encoders, file, info, Time.FromSeconds(1), duration, ExportPerformance.Fastest,
                Path.Combine(target, $"{Path.GetFileNameWithoutExtension(file)}-corte.mp4"), reencode: false);
        }
    }

    private async Task ExportAsync(ExportRunner runner, IReadOnlyList<EncoderSpec> encoders, string file, MediaInfo info, Time from, Time to,
        ExportPerformance mode, string path, bool reencode)
    {
        var sequence = new Sequence
        {
            Id = "sec", Name = "Medir", FrameRate = info.Video!.FrameRate, Width = info.Video.DisplayWidth, Height = info.Video.DisplayHeight,
        };
        var project = new Project { Id = "medir", Name = "Medir", Sequences = [sequence] };
        var media = new MediaRef { Id = "m", Path = file, Name = Path.GetFileName(file) };
        info.ApplyTo(media);
        project.Media.Add(media);
        var video = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        video.Clips.Add(new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = from, SourceOut = to, LinkId = "l" });
        sequence.VideoTracks.Add(video);
        if (info.Audio is not null)
        {
            var audio = new Track { Id = "a0", Kind = TrackKind.Audio, Name = "Audio 1" };
            audio.Clips.Add(new Clip { Id = "ca", MediaId = "m", Start = Time.Zero, SourceIn = from, SourceOut = to, LinkId = "l" });
            sequence.AudioTracks.Add(audio);
        }

        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264 },
            Audio = info.Audio is null ? null : new AudioExportSettings { Codec = ExportCodec.Aac },
            Performance = mode,
            SmartRender = !reencode,
        };
        // La mejor de tres: la máquina de medida comparte CPU y GPU con otros procesos.
        double best = double.MaxValue;
        string what = "";
        for (int round = 0; round < 3; round++)
        {
            var watch = Stopwatch.StartNew();
            using ExportJob job = runner.Start(new ExportRequest(project, "sec", path, settings), encoders);
            await job.Completion.WaitAsync(TimeSpan.FromMinutes(15));
            watch.Stop();
            Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
            best = Math.Min(best, watch.Elapsed.TotalSeconds);
            what = $"{job.Plan?.VideoEncoder?.Id}{Describe(job)}";
        }

        long frames = (to - from).ToFrames(sequence.FrameRate);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Path.GetFileName(path)}: {best:0.00} s, {frames / best:0} fps ({what}, {new FileInfo(path).Length / 1e6:0.0} MB)"));
    }

    private static string Describe(ExportJob job) => job.StreamCopy is { Copies: true } ? ", copia directa" : "";
}
