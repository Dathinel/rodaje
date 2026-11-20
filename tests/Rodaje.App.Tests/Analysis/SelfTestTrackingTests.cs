using Rodaje.App.Services.Tracking;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Analysis;

[Collection(StabilizeDecodeTests.Name)]
public sealed class SelfTestTrackingTests(ITestOutputHelper output)
{
    [FfmpegLibraryFact(Encoder = "libopenh264")]
    public async Task Clean_machine_pattern_loses_the_default_corners_inside_the_clip()
    {
        List<PointTrackFrame> frames = await TrackAsync(textured: false);
        Assert.Single(frames, f => f.Samples.All(s => !s.IsLost));
        Assert.All(frames.Skip(1), f =>
        {
            Assert.True(f.Samples[0].IsLost);
            Assert.True(f.Samples[3].IsLost);
        });
        (Project project, Clip clip) = ProjectWithTrack(frames);
        Assert.Null(TrackApply.Prepare(project, project.Sequences[0], clip, "t", TrackUse.PixelateZone, Time.Zero, null, false, out string problem));
        Assert.Equal("No hay fotogramas seguidos dentro del clip.", problem);
    }

    [FfmpegLibraryFact(Encoder = "libopenh264")]
    public async Task Textured_clean_machine_pattern_applies_a_zone_and_undoes_it()
    {
        List<PointTrackFrame> frames = await TrackAsync(textured: true);
        // Las figuras de testsrc2 pueden tapar algún punto más adelante; hacen falta cuadros realmente seguidos,
        // además del inicial (cuya confianza 1 no procede del análisis).
        Assert.True(frames.Skip(1).Count(f => f.Samples.All(s => s.Confidence >= 0.7f)) >= 2);
        (Project project, Clip clip) = ProjectWithTrack(frames);
        string before = Rodaje.Core.Serialization.ProjectSerializer.Serialize(project);
        TrackApplication? zone = TrackApply.Prepare(project, project.Sequences[0], clip, "t", TrackUse.PixelateZone,
            frames[frames.Count / 2].Time, null, false, out string problem);
        Assert.True(zone is not null, problem);
        Assert.Equal(frames.Count(f => f.Samples.All(s => !s.IsLost)), zone.Keyframes);
        var undo = new UndoStack(project);
        undo.Execute(zone.Command);
        Assert.Contains(clip.Effects, e => e.Type == BuiltInEffects.Pixelate);
        Assert.Contains(clip.Effects, e => e.Type == BuiltInEffects.Mask);
        undo.Undo();
        Assert.Equal(before, Rodaje.Core.Serialization.ProjectSerializer.Serialize(project));
    }

    private async Task<List<PointTrackFrame>> TrackAsync(bool textured)
    {
        using var folder = new TempDirectory();
        string path = folder.File("h264-1080p-23976-aac-ci.mp4");
        // El primer segundo del material de probar-maquina-limpia.ps1, con el mismo codificador y ajustes.
        string source = "testsrc2=size=1920x1080:rate=24000/1001" + (textured ? ",noise=alls=40:allf=u:all_seed=42,gblur=sigma=1" : "");
        FfmpegTool.Default!.Run(["-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", source,
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "1", "-c:v", "libopenh264", "-b:v", "8M", "-g", "48", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-ac", "2", "-b:a", "192k", "-movflags", "+faststart", path]);
        var frames = new List<PointTrackFrame>();
        var request = new PointTrackRequest(path, Time.Zero, Time.FromFrames(20, Rational.Ntsc24), true, PointTrackKind.Corners,
            TrackEdits.DefaultPoints(PointTrackKind.Corners, 1920.0 / 1080), 0.7f, _ => CropRect.None);
        await PointTracking.RunAsync(request, frames.Add, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(21, frames.Count);
        Assert.All(frames, f => Assert.InRange(f.Time.Ticks, 0, Time.FromFrames(20, Rational.Ntsc24).Ticks));
        foreach (PointTrackFrame frame in frames)
        {
            output.WriteLine($"{frame.Time.Ticks}: {string.Join(", ", frame.Samples.Select(s => s.Confidence.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)))}");
        }

        return frames;
    }

    private static (Project Project, Clip Clip) ProjectWithTrack(List<PointTrackFrame> frames)
    {
        var samples = new PointTrack([.. frames.Select(f => f.Time)], [.. frames.Select(f => f.Samples)]);
        var track = new EffectInstance { Id = "t", Type = BuiltInEffects.Track };
        track.Options[BuiltInEffects.TrackOptions.Kind] = PointTrack.KindName(PointTrackKind.Corners);
        track.Options[BuiltInEffects.TrackOptions.Samples] = samples.Format();
        var clip = new Clip { Id = "c", MediaId = "m", SourceOut = Time.FromSeconds(1), Effects = [track] };
        var project = new Project
        {
            Id = "p",
            Media = [new MediaRef { Id = "m", Path = "ci.mp4", HasVideo = true, Width = 1920, Height = 1080, FrameRate = Rational.Ntsc24 }],
            Sequences = [new Sequence { Id = "s", VideoTracks = [new Track { Id = "v", Kind = TrackKind.Video, Clips = [clip] }] }],
        };
        return (project, clip);
    }
}
