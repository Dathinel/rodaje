using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Commands;

/// <summary>Alineación, curva del avance, duración de varias a la vez y transiciones de audio (formato 12).</summary>
public class TransitionTimingTests
{
    /// <summary>«clip-1» [0, 5), «clip-5» [5, 7) y «clip-6» [7, 8) seguidos en Video 1.</summary>
    private static (Project Project, UndoStack Stack) NewStack()
    {
        Project project = Create();
        Track v1 = Seq(project).VideoTracks[0];
        v1.Clips.Insert(1, NewClip("clip-5", 5, 2));
        v1.Clips.Insert(2, NewClip("clip-6", 7, 1));
        return (project, new UndoStack(project));
    }

    private static Track V1(Project project) => Seq(project).VideoTracks[0];

    private static string Dump(Project project) => ProjectSerializer.Serialize(project);

    private static void RoundTrip(UndoStack stack, ICommand command)
    {
        string before = Dump(stack.Project);
        stack.Execute(command);
        string after = Dump(stack.Project);
        Assert.NotEqual(before, after);
        Assert.Empty(ModelValidator.Validate(stack.Project));
        Assert.True(stack.Undo());
        Assert.Equal(before, Dump(stack.Project));
        Assert.True(stack.Redo());
        Assert.Equal(after, Dump(stack.Project));
    }

    private static string Add(UndoStack stack, string from, double seconds, string type = BuiltInTransitions.Crossfade)
    {
        SequenceEditCommand add = Edits.AddTransition(SequenceId, from, type, S(seconds));
        stack.Execute(add);
        return Assert.Single(add.CreatedIds);
    }

    [Theory]
    [InlineData(TransitionAlignment.Center, 5L, 6L)]
    [InlineData(TransitionAlignment.Before, 11L, 0L)]
    [InlineData(TransitionAlignment.After, 0L, 11L)]
    public void Alignment_splits_the_duration_around_the_cut(TransitionAlignment alignment, long before, long after)
    {
        var transition = new TransitionInstance { Duration = Time.FromTicks(11), Alignment = alignment };

        Assert.Equal((before, after), (transition.Before.Ticks, transition.After.Ticks));
    }

    [Fact]
    public void Changing_the_alignment_is_one_undoable_step_and_shortens_only_if_it_does_not_fit()
    {
        (Project project, UndoStack stack) = NewStack();
        string first = Add(stack, "clip-1", 2);
        Add(stack, "clip-5", 1);

        RoundTrip(stack, Edits.SetTransitionAlignment(SequenceId, first, TransitionAlignment.Before));
        Assert.Equal((S(2), S(2), Time.Zero), (V1(project).Transitions[0].Duration, V1(project).Transitions[0].Before, V1(project).Transitions[0].After));

        // Al inicio de «clip-5» (2 s) queda lo que no usa la siguiente, centrada de 1 s: 1,5 s.
        RoundTrip(stack, Edits.SetTransitionAlignment(SequenceId, first, TransitionAlignment.After));
        Assert.Equal(S(1.5), V1(project).Transitions[0].Duration);
        Assert.Equal(S(1.5), TransitionOps.MaxDuration(V1(project), V1(project).Transitions[0]));
        Assert.Equal(S(5), TransitionOps.MaxDuration(V1(project), V1(project).Transitions[0], TransitionAlignment.Before));
    }

    [Fact]
    public void Two_transitions_that_overlap_inside_a_clip_are_rejected()
    {
        (Project project, _) = NewStack();
        V1(project).Transitions.Add(new TransitionInstance
        {
            Id = "t1", Type = BuiltInTransitions.Crossfade, From = "clip-1", To = "clip-5", Duration = S(1.5), Alignment = TransitionAlignment.After,
        });
        V1(project).Transitions.Add(new TransitionInstance
        {
            Id = "t2", Type = BuiltInTransitions.Wipe, From = "clip-5", To = "clip-6", Duration = S(1), Alignment = TransitionAlignment.Before,
        });

        Assert.Contains(ModelValidator.Validate(project), e => e.Contains("se pisa", StringComparison.Ordinal));
        V1(project).Transitions[1].Duration = S(0.5);
        Assert.Empty(ModelValidator.Validate(project));
    }

    [Fact]
    public void Trimming_a_clip_shortens_aligned_transitions_so_they_still_fit()
    {
        (Project project, _) = NewStack();
        V1(project).Transitions.Add(new TransitionInstance
        {
            Id = "t1", Type = BuiltInTransitions.Crossfade, From = "clip-1", To = "clip-5", Duration = S(1.5), Alignment = TransitionAlignment.After,
        });
        V1(project).Transitions.Add(new TransitionInstance
        {
            Id = "t2", Type = BuiltInTransitions.Wipe, From = "clip-5", To = "clip-6", Duration = S(0.5), Alignment = TransitionAlignment.Before,
        });

        // «clip-5» pasa a medir 1 s: la primera le guarda a la segunda lo que usa (hasta la mitad del clip) y las dos caben.
        Clip clip5 = V1(project).Clips[1];
        clip5.SourceOut = clip5.SourceIn + S(1);
        V1(project).Clips[2].Start = S(6);
        TransitionOps.Reconcile(Seq(project));

        Assert.Equal([("t1", S(0.5)), ("t2", S(0.5))], V1(project).Transitions.Select(t => (t.Id, t.Duration)));
        Assert.Empty(ModelValidator.Validate(project));
    }

    [Fact]
    public void The_curve_is_stored_only_when_it_is_not_linear_and_the_bezier_points_are_clamped()
    {
        (Project project, UndoStack stack) = NewStack();
        string id = Add(stack, "clip-1", 1);

        RoundTrip(stack, Edits.SetTransitionCurve(SequenceId, id, Interpolation.Bezier, new BezierEase(1.5, 4, -1, 0.5)));
        TransitionInstance transition = V1(project).Transitions[0];
        Assert.Equal(Interpolation.Bezier, transition.Curve);
        Assert.Equal(new BezierEase(1, BezierEase.MaxY, 0, 0.5), transition.Ease);
        string json = Dump(project);
        Assert.Contains("\"curve\": \"bezier\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ease\"", json, StringComparison.Ordinal);

        RoundTrip(stack, Edits.SetTransitionCurve(SequenceId, id, Interpolation.Linear));
        Assert.Null(V1(project).Transitions[0].Curve);
        Assert.Null(V1(project).Transitions[0].Ease);
        Assert.DoesNotContain("\"curve\"", Dump(project), StringComparison.Ordinal);
    }

    [Fact]
    public void Consecutive_curve_changes_merge_into_one_step()
    {
        (_, UndoStack stack) = NewStack();
        string id = Add(stack, "clip-1", 1);
        string before = Dump(stack.Project);

        stack.Execute(Edits.SetTransitionCurve(SequenceId, id, Interpolation.Bezier, new BezierEase(0.2, 0, 0.8, 1)));
        stack.Execute(Edits.SetTransitionCurve(SequenceId, id, Interpolation.Bezier, new BezierEase(0.3, 0, 0.7, 1)));
        stack.Execute(Edits.SetTransitionCurve(SequenceId, id, Interpolation.Bezier, new BezierEase(0.4, 0, 0.6, 1)));

        Assert.True(stack.Undo());
        Assert.Equal(before, Dump(stack.Project));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Interpolation.Linear)]
    [InlineData(Interpolation.EaseIn)]
    [InlineData(Interpolation.EaseOut)]
    [InlineData(Interpolation.EaseInOut)]
    [InlineData(Interpolation.Bezier)]
    [InlineData(Interpolation.Bounce)]
    [InlineData(Interpolation.Elastic)]
    [InlineData(Interpolation.Hold)]
    public void Every_curve_is_exact_at_the_ends_and_stays_between_zero_and_one(Interpolation? curve)
    {
        var overshoot = new BezierEase(0.3, -1.5, 0.7, 2.5);
        Assert.Equal(0, TransitionInstance.EaseProgress(curve, overshoot, 0));
        Assert.Equal(0, TransitionInstance.EaseProgress(curve, overshoot, -0.2));
        Assert.Equal(1, TransitionInstance.EaseProgress(curve, overshoot, 1));
        Assert.Equal(1, TransitionInstance.EaseProgress(curve, overshoot, 1.3));
        for (int i = 1; i < 100; i++)
        {
            Assert.InRange(TransitionInstance.EaseProgress(curve, overshoot, i / 100.0), 0, 1);
        }
    }

    [Fact]
    public void The_curve_uses_the_keyframe_formulas()
    {
        var ease = new BezierEase(0.1, 0.6, 0.3, 1);
        for (int i = 1; i < 20; i++)
        {
            double t = i / 20.0;
            Assert.Equal(KeyframeMath.Ease(Interpolation.Bezier, ease, t), TransitionInstance.EaseProgress(Interpolation.Bezier, ease, t), 12);
            Assert.Equal(KeyframeMath.Ease(Interpolation.EaseInOut, null, t), TransitionInstance.EaseProgress(Interpolation.EaseInOut, null, t), 12);
            Assert.Equal(t, TransitionInstance.EaseProgress(null, null, t), 12);
        }
    }

    [Fact]
    public void Several_durations_change_in_one_step_each_within_its_clips()
    {
        (Project project, UndoStack stack) = NewStack();
        string first = Add(stack, "clip-1", 0.5);
        string second = Add(stack, "clip-5", 0.5);

        RoundTrip(stack, Edits.SetTransitionDurations(SequenceId, [first, second], S(10)));

        // Centradas: la primera no pasa del clip más corto (2 s); la segunda, de «clip-6» (1 s).
        Assert.Equal([S(2), S(1)], V1(project).Transitions.Select(t => t.Duration));
        Assert.True(stack.Undo());
        Assert.Equal([S(0.5), S(0.5)], V1(project).Transitions.Select(t => t.Duration));
    }

    [Fact]
    public void Audio_transitions_only_go_on_audio_tracks()
    {
        (Project project, UndoStack stack) = NewStack();
        Assert.Throws<EditException>(() => stack.Execute(Edits.AddTransition(SequenceId, "clip-1", BuiltInTransitions.AudioPower)));
        string id = Add(stack, "clip-1", 1);
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetTransitionType(SequenceId, id, BuiltInTransitions.AudioGain)));

        Track a1 = Seq(project).AudioTracks[0];
        a1.Clips.Add(new Clip { Id = "clip-7", MediaId = "media-1", SourceIn = S(5), SourceOut = S(8), Start = S(5) });
        RoundTrip(stack, Edits.AddTransition(SequenceId, "clip-4", BuiltInTransitions.AudioExponential, S(1)));
        Assert.Equal(BuiltInTransitions.AudioExponential, Assert.Single(a1.Transitions).Type);
    }

    [Fact]
    public void Alignment_and_curve_survive_saving_and_a_centered_linear_one_writes_neither()
    {
        (Project project, UndoStack stack) = NewStack();
        string first = Add(stack, "clip-1", 1);
        Add(stack, "clip-5", 0.5);
        stack.Execute(Edits.SetTransitionAlignment(SequenceId, first, TransitionAlignment.Before));
        stack.Execute(Edits.SetTransitionCurve(SequenceId, first, Interpolation.EaseOut));

        string json = Dump(project);
        Project loaded = ProjectSerializer.Deserialize(json);

        TransitionInstance read = Seq(loaded).VideoTracks[0].Transitions[0];
        Assert.Equal((TransitionAlignment.Before, Interpolation.EaseOut), (read.Alignment, read.Curve));
        Assert.Equal(json, Dump(loaded));
        Assert.Contains("\"alignment\": \"before\"", json, StringComparison.Ordinal);
        Assert.Equal(2, json.Split("\"alignment\"").Length);
    }

    [Fact]
    public void A_format_9_file_with_transitions_opens_centered_and_linear()
    {
        (Project project, UndoStack stack) = NewStack();
        Add(stack, "clip-1", 1);
        string json = Dump(project).Replace($"\"schemaVersion\": {Project.CurrentSchemaVersion}", "\"schemaVersion\": 9", StringComparison.Ordinal);

        Project loaded = ProjectSerializer.Deserialize(json);

        TransitionInstance read = Seq(loaded).VideoTracks[0].Transitions[0];
        Assert.Equal((TransitionAlignment.Center, (Interpolation?)null, Project.CurrentSchemaVersion), (read.Alignment, read.Curve, loaded.SchemaVersion));
    }
}
