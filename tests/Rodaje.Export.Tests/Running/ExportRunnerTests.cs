using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using static Rodaje.Export.Tests.Running.ExportProjects;

namespace Rodaje.Export.Tests.Running;

/// <summary>El ciclo de vida de un trabajo: sonda, fallos antes de empezar, solo audio, conversión de tasa, dos pasadas y «exportar sin él».</summary>
[Collection(ExportGpuGroup.Name)]
public sealed class ExportRunnerTests(ITestOutputHelper output) : IDisposable
{
    public void Dispose() => ExportEndToEndTests.DeleteFolders();

    private static readonly Lazy<CatalogEncoderSource> Source = new(() => new CatalogEncoderSource(Skip.ProxyExecutable!));

    [ProxyFact]
    public async Task The_probe_keeps_what_encodes_here_and_says_why_the_rest_does_not()
    {
        EncoderSpec pcm = ExportEncoderCatalog.Find("pcm_s24le")!;
        var source = new CatalogEncoderSource(new ExportEncoderAvailability(new FfmpegExportEncoderProbe(Skip.ProxyExecutable!),
            catalog: [ExportEncoderCatalog.Find("libopus")!, pcm, pcm with { Id = "no_existe" }]));

        IReadOnlyList<EncoderSpec> works = await source.GetAsync();

        Assert.Equal(["libopus", "pcm_s24le"], works.Select(e => e.Id));
        Assert.Same(works, await source.GetAsync());
        Assert.Contains("no_existe", (await source.Availability.GetAsync()).Find("no_existe")!.Reason, StringComparison.Ordinal);
    }

    [ProxyFact]
    public async Task A_request_that_cannot_be_planned_fails_without_touching_anything()
    {
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable!, Encoders = Source.Value });
        string folder = ExportEndToEndTests.Folder();
        var changes = 0;

        using ExportJob job = runner.Start(new ExportRequest(ExportPlanTests.Project(Time.FromSeconds(1)), "sec", Path.Combine(folder, "x.mp4"), new ExportSettings()));
        job.Changed += (_, _) => Interlocked.Increment(ref changes);
        await job.Completion;

        Assert.Equal(ExportJobState.Failed, job.Progress.State);
        Assert.Equal(ExportErrorCode.InvalidSettings, job.Failure!.Code);
        Assert.Null(job.Plan);
        Assert.Empty(Directory.GetFileSystemEntries(folder));
        Assert.Throws<InvalidOperationException>(() => new ExportRunner(runner.Options with { Encoders = null }).Start(job.Request));
        job.Cancel();
    }

    [ProxyFact]
    public async Task Audio_only_mixes_measures_and_encodes_without_the_gpu()
    {
        Project project = TwoTracks();
        string path = Path.Combine(ExportEndToEndTests.Folder(), "solo audio.m4a");
        string temporary = ExportEndToEndTests.Folder();
        var journal = new ExportJournal(ExportEndToEndTests.Folder());
        var states = new List<ExportJobState>();
        var runner = new ExportRunner(new ExportRunnerOptions
        {
            Ffmpeg = Skip.ProxyExecutable!, Encoders = Source.Value, TemporaryDirectory = temporary, Journal = journal,
            CreateDevice = () => throw new InvalidOperationException("Sin video no hace falta la GPU."),
        });

        using ExportJob job = runner.Start(new ExportRequest(project, "sec", path, ExportPresets.AudioOnly.Settings, new ExportRange(S(1), S(4))));
        job.Changed += (_, _) =>
        {
            lock (states)
            {
                if (states.Count == 0 || states[^1] != job.Progress.State)
                {
                    states.Add(job.Progress.State);
                }
            }
        };
        await job.Completion;

        Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
        Assert.Equal(1, job.Progress.Fraction);
        output.WriteLine($"{job.Report!.Audio}; estados {string.Join(" → ", states)}; {job.Loudness}");
        Assert.Contains(ExportJobState.MeasuringLoudness, states);
        Assert.Equal(3.0, job.Report.Audio!.DurationSeconds, 1);
        Assert.Null(job.Report.Video);
        Assert.Empty(Directory.GetFileSystemEntries(temporary));
        Assert.Empty(journal.Entries());
    }

    [ExportFact]
    public async Task Rate_conversion_by_nearest_and_by_blend_gives_whole_frames_of_the_output_rate()
    {
        Project project = TwoTracks(1280, 720);
        foreach ((Rational rate, FrameRateConversion conversion, long frames) in new[]
        {
            (Rational.Fps25, FrameRateConversion.Blend, 50L),
            (Rational.Ntsc24, FrameRateConversion.Nearest, 48L),
        })
        {
            var settings = new ExportSettings
            {
                Container = ExportContainer.Mkv,
                Video = new VideoExportSettings { Codec = ExportCodec.H264, FrameRate = rate, FrameRateConversion = conversion },
            };
            ExportJob job = await RunAsync(project, settings, $"tasa-{conversion}.mkv", new ExportRange(S(1), S(3)));

            Assert.Equal(frames, job.Report!.Video!.FrameCount);
            Assert.Equal(rate, job.Report.Video.FrameRate);
        }
    }

    [ExportFact]
    public async Task Two_passes_compose_the_video_twice_and_report_both()
    {
        var passes = new HashSet<int>();
        var settings = new ExportSettings
        {
            Container = ExportContainer.WebM,
            Video = new VideoExportSettings { Codec = ExportCodec.Vp9, Width = 640, RateControl = RateControl.TargetSize(400_000) },
        };

        ExportJob job = await RunAsync(TwoTracks(), settings, "dos pasadas.webm", new ExportRange(S(0), S(2)), j => passes.Add(j.Progress.Pass));

        Assert.Equal([1, 2], passes.Order());
        Assert.Equal(2, job.Progress.PassCount);
        Assert.Equal(60, job.Report!.Video!.FrameCount);
    }

    [ExportFact]
    public async Task A_missing_lut_stops_the_export_unless_the_person_continues_without_it()
    {
        Project project = TwoTracks(1280, 720);
        Clip clip = project.Sequences[0].VideoTracks[0].Clips[0];
        clip.Effects[0].Options[BuiltInEffects.ColorOptions.Lut] = "rodaje.filtros@1.0.0/no-esta";
        var settings = new ExportSettings { Video = new VideoExportSettings { Codec = ExportCodec.H264 } };
        var range = new ExportRange(S(0), S(1));

        ExportJob failed = await RunAsync(project, settings, "lut.mp4", range, expectSuccess: false);
        Assert.Equal(ExportErrorCode.ContentMissing, failed.Failure!.Code);
        Assert.True(failed.Failure.CanContinueWithout);

        ExportJob continued = await RunAsync(project, settings, "lut.mp4", range, request: r => r with { ContinueWithoutMissing = true });
        Assert.Equal(30, continued.Report!.Video!.FrameCount);
        Assert.NotNull(continued.Thumbnail);
    }

    [ExportFact]
    public void Stale_temporaries_of_dead_processes_are_cleaned_at_start()
    {
        var journal = new ExportJournal(ExportEndToEndTests.Folder());
        Assert.Empty(ExportRunner.CleanupStale(journal));
        string file = Path.Combine(ExportEndToEndTests.Folder(), "es un archivo");
        File.WriteAllText(file, "");
        Assert.Empty(ExportRunner.CleanupStale(new ExportJournal(file)));
    }

    private async Task<ExportJob> RunAsync(Project project, ExportSettings settings, string name, ExportRange range, Action<ExportJob>? changed = null,
        bool expectSuccess = true, Func<ExportRequest, ExportRequest>? request = null)
    {
        string path = Path.Combine(ExportEndToEndTests.Folder(), name);
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable!, Encoders = Source.Value });
        var exportRequest = new ExportRequest(project, "sec", path, settings, range);
        ExportJob job = runner.Start(request?.Invoke(exportRequest) ?? exportRequest);
        if (changed is not null)
        {
            job.Changed += (_, _) => changed(job);
        }

        await job.Completion.WaitAsync(TimeSpan.FromMinutes(3));
        output.WriteLine($"{name}: {job.Progress.State} en {job.Progress.Elapsed.TotalSeconds:0.00} s {job.Failure?.Message}");
        if (expectSuccess)
        {
            Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
        }

        return job;
    }
}
