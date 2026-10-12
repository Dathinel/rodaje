using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing;

public sealed class AnalysisEditsTests
{
    private static TrimFixture Talk() => new TrimFixture()
        .Add("v1", "v", "m2", 0, 200, 0, "L")
        .Add("a1", "a", "m2", 0, 200, 0, "L")
        .Add("v1", "after", "m1", 0, 25, 200);

    [Fact]
    public void Removing_silences_with_ripple_cuts_video_and_audio_and_closes_gaps_in_one_undo_step()
    {
        TrimFixture f = Talk();
        var undo = new UndoStack(f.Project);
        SequenceSnapshot before = SequenceSnapshot.Capture(f.Sequence);
        undo.Execute(Edits.RemoveSilences("seq", "v", [(F(20), F(50)), (F(100), F(110)), (F(190), F(200))], ripple: true));

        Assert.Equal([F(0), F(20), F(70)], f.Track("v1").Clips.Take(3).Select(c => c.Start));
        Assert.Equal([F(20), F(70), F(150)], f.Track("v1").Clips.Take(3).Select(c => c.End));
        Assert.Equal([F(0), F(50), F(110)], f.Track("v1").Clips.Take(3).Select(c => c.SourceIn));
        Assert.Equal(f.Track("v1").Clips.Take(3).Select(c => (c.Start, c.End)), f.Track("a1").Clips.Select(c => (c.Start, c.End)));
        Assert.Equal(F(150), f.Clip("after").Start);

        Assert.True(undo.Undo());
        Assert.True(before.Matches(f.Sequence));
        Assert.Equal(0, undo.Count);
    }

    [Fact]
    public void Without_ripple_the_gaps_stay()
    {
        TrimFixture f = Talk();
        AnalysisEdits.RemoveSpans(f.Ctx, "v", [(F(20), F(50))], ripple: false, RippleScope.Track);
        Assert.Equal([(F(0), F(20)), (F(50), F(200)), (F(200), F(225))], f.Track("v1").Clips.Select(c => (c.Start, c.End)));
        Assert.Equal(2, f.Track("a1").Clips.Count);
    }

    [Fact]
    public void Spans_snap_inwards_to_frames_and_are_clipped_to_the_clip()
    {
        TrimFixture f = Talk();
        IReadOnlyList<(Time Start, Time End)> removed = AnalysisEdits.RemoveSpans(
            f.Ctx, "v", [(F(10) + Time.FromTicks(5), F(20) - Time.FromTicks(5)), (F(190), F(400))], ripple: true, RippleScope.Track);
        Assert.Equal([(F(11), F(19)), (F(190), F(200))], removed);
        Assert.Equal(F(182) - F(0), f.Track("a1").Clips[^1].End);
    }

    [Fact]
    public void A_clip_that_is_all_silence_is_rejected()
    {
        TrimFixture f = Talk();
        Assert.Throws<EditException>(() => AnalysisEdits.RemoveSpans(f.Ctx, "v", [(F(0), F(200))], true, RippleScope.Track));
    }

    [Fact]
    public void Beat_markers_snap_to_frames_and_skip_existing_ones()
    {
        TrimFixture f = Talk();
        TimelineEdits.AddMarker(f.Ctx, F(25), "Mío");
        IReadOnlyList<string> ids = AnalysisEdits.AddMarkers(f.Ctx, [F(25) + Time.FromTicks(100), F(50), F(50), F(75) - Time.FromTicks(3)], "Pulso");
        Assert.Equal(2, ids.Count);
        Assert.Equal([F(25), F(50), F(75)], f.Sequence.Markers.Select(m => m.Time));
    }

    [Fact]
    public void Cutting_to_the_beat_splits_inside_the_clip_only()
    {
        TrimFixture f = Talk();
        IReadOnlyList<string> created = AnalysisEdits.SplitAt(f.Ctx, "v", [F(0), F(50), F(100), F(300)]);
        Assert.Equal(4, created.Count); // video y audio en cada corte
        Assert.Equal([F(0), F(50), F(100), F(200)], f.Track("v1").Clips.Select(c => c.Start));
    }

    [Fact]
    public void Ducking_adds_gain_keyframes_on_top_of_the_existing_volume_and_undoes_in_one_step()
    {
        TrimFixture f = new TrimFixture().Add("a2", "music", "m2", 0, 200, 0);
        f.Clip("music").Effects.Add(new EffectInstance
        {
            Id = "fx",
            Type = BuiltInEffects.Audio,
            Parameters = new() { [BuiltInEffects.AudioParameters.GainDb] = new Parameter { Value = -3 } },
        });
        var undo = new UndoStack(f.Project);
        SequenceSnapshot before = SequenceSnapshot.Capture(f.Sequence);
        (Time, double)[] envelope = [(F(20), 0), (F(25), -12), (F(50), -12), (F(60), 0)];
        undo.Execute(Edits.Duck("seq", ["music"], envelope));

        Parameter gain = f.Clip("music").Effects[0].Parameters[BuiltInEffects.AudioParameters.GainDb];
        Assert.Equal([F(20), F(25), F(50), F(60)], gain.Keyframes.Select(k => k.Time));
        Assert.Equal([-3, -15, -15, -3], gain.Keyframes.Select(k => k.Value));
        Assert.Equal(-9, gain.Evaluate(F(55)), 6);

        Assert.True(undo.Undo());
        Assert.True(before.Matches(f.Sequence));
    }

    [Fact]
    public void Ducking_creates_the_audio_effect_and_keeps_a_clip_edge_that_starts_ducked()
    {
        TrimFixture f = new TrimFixture().Add("a2", "music", "m2", 50, 150, 10);
        AnalysisEdits.ApplyGainOffsets(f.Ctx, "music", [(F(0), 0), (F(5), -10), (F(30), -10), (F(40), 0)]);
        EffectInstance fx = Assert.Single(f.Clip("music").Effects);
        Parameter gain = fx.Parameters[BuiltInEffects.AudioParameters.GainDb];
        // Keyframes en tiempo del origen: el clip empieza en 10 con el origen en 50.
        Assert.Equal(F(50), gain.Keyframes[0].Time);
        Assert.Equal(-10, gain.Keyframes[0].Value);
        Assert.Equal(0, gain.Evaluate(F(50 + 30)));
    }

    [Fact]
    public void Envelope_offset_is_linear_between_points_and_zero_outside()
    {
        (Time, double)[] points = [(F(10), 0), (F(20), -10)];
        Assert.Equal(0, AnalysisEdits.OffsetAt(points, F(5)));
        Assert.Equal(-5, AnalysisEdits.OffsetAt(points, F(15)), 9);
        Assert.Equal(-10, AnalysisEdits.OffsetAt(points, F(20)));
        Assert.Equal(0, AnalysisEdits.OffsetAt(points, F(21)));
    }
}
