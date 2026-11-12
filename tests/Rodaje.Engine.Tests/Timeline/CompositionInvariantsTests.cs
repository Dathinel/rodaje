using System.Runtime.CompilerServices;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class CompositionInvariantsTests : IDisposable
{
    private readonly string _folder = Scratch();

    public CompositionInvariantsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(59)]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(89)]
    [InlineData(90)]
    [InlineData(91)]
    [InlineData(119)]
    [InlineData(120)]
    [InlineData(121)]
    public void Composition_at_a_frame_is_identical_after_edits_history_and_disk_load(int frame)
    {
        TimelineBuilder builder = Scene();
        Project project = builder.Project;
        var history = new UndoStack(project);
        var ids = new SequentialIdGenerator();
        Time instant = F(frame);
        TimelinePlan initial = Plan(project);
        VideoState[] before = Video(initial, instant);
        history.Execute(Edits.Move("sec", "upper", F(45), ids: ids));
        history.Execute(Edits.SetSpeed("sec", "lower", new Rational(2, 1), ids: ids));
        history.Execute(Edits.SetKeyframe("sec", "upper", "transform", "opacity", F(315), 0.375, ids: ids));
        TimelinePlan edited = Plan(project);
        VideoState[] final = Video(edited, instant);
        Assert.NotEqual(ProjectSerializer.Serialize(builder.Project), ProjectSerializer.Serialize(InitialProject()));
        while (history.Undo())
        {
        }

        Assert.Equal(before, Video(Plan(project), instant));
        Assert.True(initial.Audio.SoundsLike(Plan(project).Audio));
        while (history.Redo())
        {
        }

        Assert.Equal(final, Video(Plan(project), instant));
        Assert.True(edited.Audio.SoundsLike(Plan(project).Audio));
        string path = Path.Combine(_folder, "Composición ñ.rodaje");
        ProjectSerializer.Save(project, path);
        Project loaded = ProjectSerializer.Load(path);
        TimelinePlan disk = Plan(loaded);
        Assert.Equal(final, Video(disk, instant));
        Assert.True(edited.Audio.SoundsLike(disk.Audio));
        Assert.Equal(edited.Duration, disk.Duration);
        // Visitar otros instantes en distinto orden no cambia la evaluación del mismo plan.
        foreach (int other in new[] { 121, 0, 59, 90, 30, 60 })
        {
            _ = Video(disk, F(other));
            Assert.Equal(final, Video(disk, instant));
        }
    }

    [Theory]
    [InlineData(29, null, -1)]
    [InlineData(30, "lower", 300)]
    [InlineData(31, "lower", 301)]
    [InlineData(89, "lower", 359)]
    [InlineData(90, null, -1)]
    [InlineData(91, null, -1)]
    public void Exact_clip_boundaries_include_only_the_first_through_last_frame(int frame, string? id, long sourceFrame)
    {
        VideoLayerPlan layer = Plan(Scene().Project).Layers[0];
        VideoClipPlan? clip = layer.ClipAt(F(frame));
        Assert.Equal(id, clip?.ClipId);
        Assert.Equal(sourceFrame, clip?.FrameAt(F(frame)) ?? -1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Empty_hidden_and_muted_tracks_have_independent_composition_rules(bool hidden, bool muted)
    {
        TimelineBuilder builder = Scene();
        builder.Sequence.VideoTracks[1].Hidden = hidden;
        builder.Sequence.AudioTracks[0].Muted = muted;
        TimelinePlan plan = Plan(builder.Project);
        Assert.Equal(hidden ? new[] { "v0", "v2" } : new[] { "v0", "v1", "v2" }, plan.Layers.Select(l => l.TrackId));
        Assert.Equal(hidden ? new[] { "v1" } : [], plan.HiddenLayers.Select(l => l.TrackId));
        Assert.Empty(plan.Layers.Single(l => l.TrackId == "v2").Clips);
        Assert.Equal(muted ? new[] { "a1" } : new[] { "a0", "a1" }, plan.Audio.Tracks.Select(t => t.TrackId));
        Assert.Empty(plan.Audio.Tracks.Single(t => t.TrackId == "a1").Clips);
        Assert.Equal(F(120), plan.Duration);
        Assert.Equal("lower", plan.Layers[0].ClipAt(F(30))!.ClipId);
    }

    private static TimelineBuilder Scene()
    {
        var builder = new TimelineBuilder(3, 2);
        builder.AddMedia("media", Rational.Fps30, 3000);
        builder.AddVideo(0, "media", F(30), F(300), F(60), "lower");
        Clip upper = builder.AddVideo(1, "media", F(60), F(300), F(60), "upper");
        upper.Effects.Add(new EffectInstance { Id = "transform", Type = "rodaje.transform", Parameters = new()
            { ["opacity"] = new Parameter { Keyframes = [new Keyframe(F(300), 0.25), new Keyframe(F(360), 0.75)] } } });
        builder.AddAudio(0, "media", F(30), F(300), F(90), "sound");
        return builder;
    }

    private static Project InitialProject() => Scene().Project;

    private TimelinePlan Plan(Project project) => TimelinePlan.Build(project, "sec", _folder);

    private static VideoState[] Video(TimelinePlan plan, Time instant) =>
        [.. plan.Lanes.Select(layer =>
        {
            VideoClipPlan? clip = layer.ClipAt(instant);
            return new VideoState(layer.TrackId, clip?.ClipId, clip?.Source.Path, clip?.Start.Ticks, clip?.End.Ticks,
                clip?.FrameAt(instant), clip is null ? null : EffectPlan.Transform(clip.Effects, clip.EffectTimeAt(instant)));
        })];

    private sealed record VideoState(string Track, string? Clip, string? Path, long? Start, long? End, long? Frame, LayerTransform? Transform);

    private static Time F(long frame) => Time.FromFrames(frame, Rational.Fps30);

    private static string Scratch([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, ".composition-tests-" + Guid.NewGuid().ToString("N"));
}
