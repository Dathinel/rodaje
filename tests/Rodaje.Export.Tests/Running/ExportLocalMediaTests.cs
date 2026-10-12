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
/// Exporta a MP4 los videos propios de una carpeta (<c>RODAJE_MEDIOS_LOCALES</c>), uno tras otro en una secuencia
/// 9:16 con su audio, tal como quedan al arrastrarlos a la línea de tiempo. Sin la variable se omite: los medios no
/// se versionan. La salida queda en <c>%TEMP%\rodaje-pruebas-locales</c> para mirarla.
/// </summary>
[Collection(ExportGpuGroup.Name)]
public sealed class ExportLocalMediaTests(ITestOutputHelper output)
{
    private const string Variable = "RODAJE_MEDIOS_LOCALES";

    [LocalMediaFact]
    [Trait("Categoria", "Local")]
    public Task All_local_videos_export_to_mp4() => ExportAsync(int.MaxValue);

    [LocalMediaFact]
    [Trait("Categoria", "Local")]
    public Task All_local_videos_but_the_last_export_to_mp4() => ExportAsync(-1);

    private async Task ExportAsync(int take)
    {
        string[] files = [.. Directory.GetFiles(Environment.GetEnvironmentVariable(Variable)!, "*.mp4").Order(StringComparer.OrdinalIgnoreCase)];
        files = take < 0 ? files[..^1] : files;
        var sequence = new Sequence { Id = "sec", Name = "Local", FrameRate = Rational.Fps30, Width = 1080, Height = 1920 };
        var project = new Project { Id = "local", Name = "Local", Sequences = [sequence] };
        var video = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        var audio = new Track { Id = "a0", Kind = TrackKind.Audio, Name = "Audio 1" };
        sequence.VideoTracks.Add(video);
        sequence.AudioTracks.Add(audio);
        Time at = Time.Zero;
        for (int i = 0; i < files.Length; i++)
        {
            var media = new MediaRef { Id = $"m{i}", Path = files[i], Name = Path.GetFileName(files[i]) };
            MediaProbe.Probe(media.Path).ApplyTo(media);
            project.Media.Add(media);
            video.Clips.Add(new Clip { Id = $"video{i}", MediaId = media.Id, Start = at, SourceIn = Time.Zero, SourceOut = media.Duration, LinkId = $"l{i}" });
            audio.Clips.Add(new Clip { Id = $"audio{i}", MediaId = media.Id, Start = at, SourceIn = Time.Zero, SourceOut = media.Duration, LinkId = $"l{i}" });
            at += media.Duration;
        }

        Assert.Empty(ModelValidator.Validate(project));
        IReadOnlyList<EncoderSpec> encoders = await new CatalogEncoderSource(Skip.ProxyExecutable!).GetAsync();
        string folder = Path.Combine(Path.GetTempPath(), "rodaje-pruebas-locales");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{files.Length}-clips.mp4");
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264 },
            Audio = new AudioExportSettings { Codec = ExportCodec.Aac },
        };
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable! });
        using ExportJob job = runner.Start(new ExportRequest(project, "sec", path, settings), encoders);
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(15));

        long frames = sequence.Duration.ToFrames(sequence.FrameRate);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{files.Length} clips, {sequence.Duration.ToSeconds():0.000} s: {job.Progress.State} en {job.Progress.Elapsed.TotalSeconds:0.0} s ({job.Plan?.VideoEncoder?.Id}); {path}"));
        Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
        output.WriteLine($"{job.Report!.Video} / {job.Report.Audio}");
        Assert.Equal((1080, 1920), (job.Report.Video!.Width, job.Report.Video.Height));
        Assert.InRange(job.Report.Video.FrameCount, frames, frames + 1);
        Assert.InRange(job.Report.Audio!.DurationSeconds, sequence.Duration.ToSeconds() - 0.05, sequence.Duration.ToSeconds() + 0.05);
    }
}

/// <summary>Necesita <c>RODAJE_MEDIOS_LOCALES</c> (carpeta con los .mp4) además de lo de <see cref="ExportFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LocalMediaFactAttribute : FactAttribute
{
    public LocalMediaFactAttribute() => Skip = Environment.GetEnvironmentVariable("RODAJE_MEDIOS_LOCALES") is { Length: > 0 } folder && Directory.Exists(folder)
        ? new ExportFactAttribute().Skip
        : "Sin RODAJE_MEDIOS_LOCALES: no hay medios propios que exportar.";
}
