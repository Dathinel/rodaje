using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Placement.PlacementBuilder;

namespace Rodaje.Core.Tests.Editing.Placement;

/// <summary>Reemplazar el medio de un clip conservando duración, efectos y keyframes (función 8 de docs/pendientes.md).</summary>
public class ReplaceMediaTests
{
    private static PlacementBuilder WithMedia(params MediaRef[] extra)
    {
        var b = new PlacementBuilder();
        b.Project.Media.AddRange(extra);
        return b;
    }

    private static MediaRef Video(string id, long frames, bool audio = true) =>
        new() { Id = id, Name = id, Duration = F(frames), HasVideo = true, HasAudio = audio };

    private static EffectInstance AnimatedOpacity(params long[] frames) => new()
    {
        Id = "fx",
        Type = BuiltInEffects.Transform,
        Parameters =
        {
            [BuiltInEffects.TransformParameters.Opacity] = new Parameter
            {
                Value = 1,
                Keyframes = [.. frames.Select((f, i) => new Keyframe(F(f), i % 2 == 0 ? 0 : 1))],
            },
        },
    };

    private static void Fails(PlacementBuilder b, Action action, string expected)
    {
        string before = b.Snapshot() + string.Join(",", b.Sequence.AllTracks.SelectMany(t => t.Clips).Select(c => c.MediaId));
        EditException ex = Assert.Throws<EditException>(action);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, b.Snapshot() + string.Join(",", b.Sequence.AllTracks.SelectMany(t => t.Clips).Select(c => c.MediaId)));
    }

    [Fact]
    public void Keeps_the_same_source_range_when_it_fits()
    {
        PlacementBuilder b = WithMedia(Video("nuevo", 200));
        Clip clip = b.Add("v1", "c", 10, 20, 60);
        clip.Effects.Add(AnimatedOpacity(25, 40));

        ReplaceMediaPreview result = TimelineEdits.ReplaceMedia(b.Context(), "c", "nuevo");

        Assert.False(result.Shortened);
        Assert.Equal(F(40), result.NewDuration);
        Assert.Equal("nuevo", clip.MediaId);
        Assert.Equal("c@10[20,60)", b.Layout("v1"));
        Assert.Equal([F(25), F(40)], clip.Effects[0].Parameters[BuiltInEffects.TransformParameters.Opacity].Keyframes.Select(k => k.Time));
        b.AssertValid();
    }

    [Fact]
    public void Starts_at_the_beginning_and_moves_keyframes_when_the_range_does_not_fit()
    {
        PlacementBuilder b = WithMedia(Video("nuevo", 45));
        Clip clip = b.Add("v1", "c", 10, 50, 90);
        clip.Effects.Add(AnimatedOpacity(50, 70, 89));

        ReplaceMediaPreview result = TimelineEdits.ReplaceMedia(b.Context(), "c", "nuevo");

        Assert.False(result.Shortened);
        Assert.Equal("c@10[0,40)", b.Layout("v1"));
        Assert.Equal([F(0), F(20), F(39)], clip.Effects[0].Parameters[BuiltInEffects.TransformParameters.Opacity].Keyframes.Select(k => k.Time));
        Assert.Equal(F(20), clip.ToEffectTime(F(30)));
        b.AssertValid();
    }

    [Fact]
    public void Shorter_media_shortens_the_clip_and_the_preview_warns()
    {
        PlacementBuilder b = WithMedia(Video("corto", 30));
        b.Add("v1", "c", 0, 10, 70);
        b.Add("v1", "d", 60, 0, 10);

        ReplaceMediaPreview preview = TimelineEdits.PreviewReplaceMedia(b.Project, b.Sequence, "c", "corto");
        Assert.True(preview.Shortened);
        Assert.Equal(F(60), preview.OldDuration);
        Assert.Equal(F(30), preview.NewDuration);
        Assert.Equal("c@0[10,70) d@60[0,10)", b.Layout("v1"));

        ReplaceMediaPreview done = TimelineEdits.ReplaceMedia(b.Context(), "c", "corto");
        Assert.Equal(preview, done);
        Assert.Equal("c@0[0,30) d@60[0,10)", b.Layout("v1"));
        b.AssertValid();
    }

    [Fact]
    public void Replaces_linked_clips_that_used_the_same_media()
    {
        PlacementBuilder b = WithMedia(Video("nuevo", 100));
        b.Add("v1", "v", 0, 10, 40, link: "l");
        b.Add("a1", "a", 0, 10, 40, link: "l");
        b.Add("a2", "m", 0, 0, 30, media: "aud", link: "l");

        ReplaceMediaPreview result = TimelineEdits.ReplaceMedia(b.Context(), "v", "nuevo");

        Assert.Equal(2, result.ClipCount);
        Assert.Equal("nuevo", b.Clip("v").MediaId);
        Assert.Equal("nuevo", b.Clip("a").MediaId);
        Assert.Equal("aud", b.Clip("m").MediaId);
        b.AssertValid();
    }

    [Fact]
    public void Keeps_speed_when_the_range_fits()
    {
        PlacementBuilder b = WithMedia(Video("nuevo", 200));
        Clip clip = b.Add("v1", "c", 0, 10, 90);
        clip.Speed = new ClipSpeed { Rate = new Rational(2, 1) };
        clip.Duration = F(40);
        b.AssertValid();

        TimelineEdits.ReplaceMedia(b.Context(), "c", "nuevo");

        Assert.Equal(F(40), clip.Duration);
        Assert.Equal(new Rational(2, 1), clip.Speed!.Rate);
        b.AssertValid();
    }

    [Fact]
    public void Errors_leave_everything_as_it_was()
    {
        PlacementBuilder b = WithMedia(Video("corto", 20));
        b.Add("v1", "c", 0, 0, 40, link: "l");
        b.Add("a1", "a", 0, 0, 40, link: "l");
        Clip fast = b.Add("v2", "r", 0, 0, 80);
        fast.Speed = new ClipSpeed { Rate = new Rational(2, 1) };
        fast.Duration = F(40);
        b.AssertValid();

        Fails(b, () => TimelineEdits.ReplaceMedia(b.Context(), "nada", "corto"), "No existe el clip «nada»");
        Fails(b, () => TimelineEdits.ReplaceMedia(b.Context(), "c", "nada"), "No existe el medio «nada»");
        Fails(b, () => TimelineEdits.ReplaceMedia(b.Context(), "c", "av"), "ya usa");
        Fails(b, () => TimelineEdits.ReplaceMedia(b.Context(), "c", "aud"), "no tiene imagen");
        Fails(b, () => TimelineEdits.ReplaceMedia(b.Context(), "c", "vid"), "no tiene sonido");
        Fails(b, () => TimelineEdits.ReplaceMedia(b.Context(), "r", "corto"), "tiene velocidad");
        b.Track("v1").Locked = true;
        Fails(b, () => TimelineEdits.ReplaceMedia(b.Context(), "c", "corto"), "bloqueada");
    }

    [Fact]
    public void Title_clips_have_no_media_to_replace()
    {
        PlacementBuilder b = WithMedia();
        var title = new Clip { Id = "t", Start = F(0), SourceIn = F(0), SourceOut = F(10), Generator = new ClipGenerator() };
        TrackOps.AddClipSorted(b.Track("v2"), title);

        Fails(b, () => TimelineEdits.ReplaceMedia(b.Context(), "t", "av"), "es un título");
    }

    [Fact]
    public void Command_undoes_in_one_step()
    {
        PlacementBuilder b = WithMedia(Video("corto", 30));
        Clip clip = b.Add("v1", "c", 0, 50, 90);
        clip.Effects.Add(AnimatedOpacity(60));
        var stack = new UndoStack(b.Project);

        stack.Execute(Edits.ReplaceMedia(b.Sequence.Id, "c", "corto"));
        Assert.Equal("c@0[0,30)", b.Layout("v1"));
        Assert.Equal(F(10), b.Clip("c").Effects[0].Parameters[BuiltInEffects.TransformParameters.Opacity].Keyframes[0].Time);

        Assert.True(stack.Undo());
        Assert.Equal("c@0[50,90)", b.Layout("v1"));
        Assert.Equal("av", b.Clip("c").MediaId);
        Assert.Equal(F(60), b.Clip("c").Effects[0].Parameters[BuiltInEffects.TransformParameters.Opacity].Keyframes[0].Time);
    }
}
