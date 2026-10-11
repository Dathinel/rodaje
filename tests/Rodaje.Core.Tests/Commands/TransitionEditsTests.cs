using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Commands;

/// <summary>Transiciones: comandos con deshacer exacto y conciliación tras las demás ediciones.</summary>
public class TransitionEditsTests
{
    /// <summary>«clip-1» [0, 5) seguido de «clip-5» [5, 7) en Video 1; «clip-2» empieza en 10, tras un hueco.</summary>
    private static (Project Project, UndoStack Stack) NewStack()
    {
        Project project = Create();
        Track v1 = Seq(project).VideoTracks[0];
        v1.Clips.Insert(1, NewClip("clip-5", 5, 2));
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

    private static string Add(UndoStack stack, string type = BuiltInTransitions.Crossfade, Time? duration = null)
    {
        SequenceEditCommand add = Edits.AddTransition(SequenceId, "clip-1", type, duration, new SequentialIdGenerator());
        RoundTrip(stack, add);
        return Assert.Single(add.CreatedIds);
    }

    [Fact]
    public void Add_PutsItOnTheCutWithTheDefaultDuration()
    {
        (Project project, UndoStack stack) = NewStack();

        string id = Add(stack);

        TransitionInstance transition = Assert.Single(V1(project).Transitions);
        Assert.Equal((id, "clip-1", "clip-5", BuiltInTransitions.Crossfade), (transition.Id, transition.From, transition.To, transition.Type));
        Assert.Equal(EffectRegistry.DefaultTransitionDuration, transition.Duration);
    }

    [Fact]
    public void Add_KeepsTransitionsInCutOrder()
    {
        (Project project, UndoStack stack) = NewStack();
        V1(project).Clips.Insert(2, NewClip("clip-6", 7, 1));
        stack.Execute(Edits.AddTransition(SequenceId, "clip-5", BuiltInTransitions.Wipe));
        stack.Execute(Edits.AddTransition(SequenceId, "clip-1", BuiltInTransitions.Dip));

        Assert.Equal(["clip-1", "clip-5"], V1(project).Transitions.Select(t => t.From));
    }

    [Theory]
    [InlineData("clip-5", BuiltInTransitions.Crossfade, 0.5, "seguidos")]
    [InlineData("clip-2", BuiltInTransitions.Crossfade, 0.5, "seguidos")]
    [InlineData("clip-1", BuiltInEffects.Blur, 0.5, "no es una transición")]
    [InlineData("clip-1", "otro.tipo", 0.5, "no es una transición")]
    [InlineData("clip-1", BuiltInTransitions.Wipe, 0, "mayor que cero")]
    [InlineData("nada", BuiltInTransitions.Wipe, 0.5, "ya no está")]
    [InlineData("clip-3", BuiltInTransitions.Wipe, 0.5, "bloqueada")]
    public void Add_RejectsWhatCannotBe(string from, string type, double seconds, string expected)
    {
        (Project project, UndoStack stack) = NewStack();
        string before = Dump(project);

        var ex = Assert.Throws<EditException>(() => stack.Execute(Edits.AddTransition(SequenceId, from, type, S(seconds))));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, Dump(project));
    }

    [Fact]
    public void Add_RejectsASecondOnTheSameCut()
    {
        (_, UndoStack stack) = NewStack();
        Add(stack);

        var ex = Assert.Throws<EditException>(() => stack.Execute(Edits.AddTransition(SequenceId, "clip-1", BuiltInTransitions.Wipe)));
        Assert.Contains("ya tiene", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Changes_UndoExactly()
    {
        (Project project, UndoStack stack) = NewStack();
        string id = Add(stack, BuiltInTransitions.Wipe);

        RoundTrip(stack, Edits.SetTransitionParameter(SequenceId, id, BuiltInTransitions.WipeParameters.Angle, 900));
        Assert.Equal(360, V1(project).Transitions[0].Parameters[BuiltInTransitions.WipeParameters.Angle]);

        RoundTrip(stack, Edits.SetTransitionDuration(SequenceId, id, S(1)));
        Assert.Equal(S(1), V1(project).Transitions[0].Duration);

        RoundTrip(stack, Edits.SetTransitionType(SequenceId, id, BuiltInTransitions.Push));
        Assert.Empty(V1(project).Transitions[0].Parameters);

        RoundTrip(stack, Edits.SetTransitionOption(SequenceId, id, BuiltInTransitions.PushOptions.Direction, "up"));
        Assert.Equal("up", V1(project).Transitions[0].Options[BuiltInTransitions.PushOptions.Direction]);

        RoundTrip(stack, Edits.SetTransitionOption(SequenceId, id, BuiltInTransitions.PushOptions.Direction, ""));
        Assert.Empty(V1(project).Transitions[0].Options);

        RoundTrip(stack, Edits.RemoveTransition(SequenceId, id));
        Assert.Empty(V1(project).Transitions);
    }

    [Fact]
    public void SameType_IsANoOpAndKeepsParameters()
    {
        (Project project, UndoStack stack) = NewStack();
        string id = Add(stack, BuiltInTransitions.Wipe);
        stack.Execute(Edits.SetTransitionParameter(SequenceId, id, BuiltInTransitions.WipeParameters.Angle, 45));

        stack.Execute(Edits.SetTransitionType(SequenceId, id, BuiltInTransitions.Wipe));

        Assert.Equal(45, V1(project).Transitions[0].Parameters[BuiltInTransitions.WipeParameters.Angle]);
    }

    [Fact]
    public void Changes_RejectBadValues()
    {
        (_, UndoStack stack) = NewStack();
        string id = Add(stack, BuiltInTransitions.Push);

        Assert.Throws<EditException>(() => stack.Execute(Edits.SetTransitionOption(SequenceId, id, BuiltInTransitions.PushOptions.Direction, "diagonal")));
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetTransitionOption(SequenceId, id, "otra", "x")));
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetTransitionParameter(SequenceId, id, "angle", 1)));
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetTransitionDuration(SequenceId, id, Time.Zero)));
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetTransitionType(SequenceId, id, BuiltInEffects.Color)));
        Assert.Throws<EditException>(() => stack.Execute(Edits.RemoveTransition(SequenceId, "nada")));

        stack.Execute(Edits.SetTransitionType(SequenceId, id, BuiltInTransitions.Wipe));
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetTransitionParameter(SequenceId, id, BuiltInTransitions.WipeParameters.Angle, double.NaN)));
    }

    [Fact]
    public void SplittingTheOutgoingClip_KeepsTheCut()
    {
        (Project project, UndoStack stack) = NewStack();
        Add(stack);

        RoundTrip(stack, Edits.Split(SequenceId, "clip-1", S(2)));

        TransitionInstance transition = Assert.Single(V1(project).Transitions);
        Assert.NotEqual("clip-1", transition.From);
        Assert.Equal(S(5), V1(project).Clips.Single(c => c.Id == transition.From).End);
        Assert.Equal("clip-5", transition.To);
    }

    [Fact]
    public void SplittingTheIncomingClip_KeepsTheCut()
    {
        (Project project, UndoStack stack) = NewStack();
        Add(stack);

        stack.Execute(Edits.Split(SequenceId, "clip-5", S(6)));

        TransitionInstance transition = Assert.Single(V1(project).Transitions);
        Assert.Equal(("clip-1", "clip-5"), (transition.From, transition.To));
    }

    [Fact]
    public void OpeningAGap_RemovesTheTransition_AndUndoBringsItBack()
    {
        (Project project, UndoStack stack) = NewStack();
        Add(stack);

        RoundTrip(stack, Edits.Lift(SequenceId, ["clip-5"]));

        Assert.Empty(V1(project).Transitions);
    }

    [Fact]
    public void Duration_IsCappedByTheShorterClip()
    {
        (Project project, UndoStack stack) = NewStack();
        string id = Add(stack, BuiltInTransitions.Wipe, S(10));
        Assert.Equal(S(2), V1(project).Transitions[0].Duration);

        RoundTrip(stack, Edits.SetTransitionDuration(SequenceId, id, S(1)));
        RoundTrip(stack, Edits.SetTransitionDuration(SequenceId, id, S(3)));
        Assert.Equal(S(2), V1(project).Transitions[0].Duration);
    }

    [Fact]
    public void DefaultDuration_FitsShortClips()
    {
        Project project = Create();
        V1(project).Clips.Insert(1, NewClip("clip-5", 5, 0.2));
        var stack = new UndoStack(project);

        Add(stack);

        Assert.Equal(S(0.2), V1(project).Transitions[0].Duration);
    }

    [Fact]
    public void TrimmingAClipShorterThanTheTransition_ShortensIt_AndUndoRestoresIt()
    {
        (Project project, UndoStack stack) = NewStack();
        Add(stack, duration: S(2));

        RoundTrip(stack, Edits.Trim(SequenceId, "clip-5", TrimEdge.Out, S(-1.5)));
        Assert.Equal(S(0.5), V1(project).Transitions[0].Duration);

        stack.Undo();
        Assert.Equal(S(2), V1(project).Transitions[0].Duration);
    }

    [Fact]
    public void SplittingNearTheCut_ShortensTheTransitionToTheNewPiece()
    {
        (Project project, UndoStack stack) = NewStack();
        Add(stack, duration: S(1));

        stack.Execute(Edits.Split(SequenceId, "clip-1", S(4.75)));

        TransitionInstance transition = Assert.Single(V1(project).Transitions);
        Assert.Equal(S(0.25), transition.Duration);
        Assert.Equal(S(4.75), V1(project).Clips.Single(c => c.Id == transition.From).Start);
    }

    [Fact]
    public void TwoTransitionsOnOneClip_DoNotOverlap()
    {
        (Project project, UndoStack stack) = NewStack();
        V1(project).Clips.Insert(2, NewClip("clip-6", 7, 3));
        stack.Execute(Edits.AddTransition(SequenceId, "clip-1", BuiltInTransitions.Crossfade, S(2)));
        stack.Execute(Edits.AddTransition(SequenceId, "clip-5", BuiltInTransitions.Crossfade, S(2)));

        // «clip-5» dura 2 s: la mitad de cada transición cabe en él sin pisarse.
        Assert.All(V1(project).Transitions, t => Assert.Equal(S(2), t.Duration));
        Assert.Empty(ModelValidator.Validate(project));

        RoundTrip(stack, Edits.Roll(SequenceId, "clip-5", S(-1)));
        Assert.All(V1(project).Transitions, t => Assert.Equal(S(1), t.Duration));
    }

    [Fact]
    public void RippleDeletingTheIncomingClip_RemovesTheTransition_InsteadOfMovingItToTheNextCut()
    {
        (Project project, UndoStack stack) = NewStack();
        V1(project).Clips.Insert(2, NewClip("clip-6", 7, 1));
        Add(stack);

        RoundTrip(stack, Edits.RippleDelete(SequenceId, ["clip-5"], RippleScope.Track));

        Assert.Equal(S(5), V1(project).Clips.Single(c => c.Id == "clip-6").Start);
        Assert.Empty(V1(project).Transitions);
    }

    [Fact]
    public void Reordering_KeepsTheTransitionOnlyWhileItsCutExists()
    {
        (Project project, UndoStack stack) = NewStack();
        Track v1 = V1(project);
        v1.Clips.RemoveAt(2);
        v1.Clips.Add(NewClip("clip-6", 7, 1));
        Add(stack);

        RoundTrip(stack, Edits.ReorderOnMainTrack(SequenceId, "clip-6", 0));
        Assert.Equal(("clip-1", "clip-5"), (v1.Transitions[0].From, v1.Transitions[0].To));

        RoundTrip(stack, Edits.ReorderOnMainTrack(SequenceId, "clip-5", 0));
        Assert.Empty(v1.Transitions);
    }

    [Fact]
    public void MovingTheOutgoingClipAway_RemovesTheTransition()
    {
        (Project project, UndoStack stack) = NewStack();
        Add(stack);

        RoundTrip(stack, Edits.Move(SequenceId, "clip-1", S(20), null, PlacementMode.Strict));

        Assert.Empty(V1(project).Transitions);
    }

    [Fact]
    public void RollingTheCut_KeepsTheTransition()
    {
        (Project project, UndoStack stack) = NewStack();
        Add(stack, duration: S(1));

        RoundTrip(stack, Edits.Roll(SequenceId, "clip-1", S(-0.5)));

        Assert.Equal(("clip-1", "clip-5", S(1)), (V1(project).Transitions[0].From, V1(project).Transitions[0].To, V1(project).Transitions[0].Duration));
    }

    [Fact]
    public void MaxDuration_IsTheShorterClip()
    {
        Assert.Equal(S(2), TransitionOps.MaxDuration(NewClip("a", 0, 5), NewClip("b", 5, 2)));
        Assert.Throws<ArgumentNullException>(() => TransitionOps.MaxDuration(null!, NewClip("b", 5, 2)));
        Assert.Throws<ArgumentNullException>(() => TransitionOps.MaxDuration(NewClip("a", 0, 5), null!));
    }

    [Fact]
    public void Reconcile_DropsDuplicatesAndOrphans()
    {
        Project project = Create();
        Track v1 = Seq(project).VideoTracks[0];
        v1.Clips.Insert(1, NewClip("clip-5", 5, 2));
        v1.Transitions.AddRange(
        [
            new TransitionInstance { Id = "t-a", Type = BuiltInTransitions.Crossfade, From = "clip-1", To = "clip-5", Duration = S(1) },
            new TransitionInstance { Id = "t-b", Type = BuiltInTransitions.Crossfade, From = "clip-1", To = "clip-5", Duration = S(1) },
            new TransitionInstance { Id = "t-c", Type = BuiltInTransitions.Crossfade, From = "clip-5", To = "clip-2", Duration = S(1) },
            new TransitionInstance { Id = "t-d", Type = BuiltInTransitions.Crossfade, From = "nada", To = "tampoco", Duration = S(1) },
        ]);

        TransitionOps.Reconcile(Seq(project));

        Assert.Equal(["t-a"], v1.Transitions.Select(t => t.Id));
        Assert.Throws<ArgumentNullException>(() => TransitionOps.Reconcile(null!));
    }
}
