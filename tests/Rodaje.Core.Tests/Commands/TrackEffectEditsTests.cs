using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Commands;

/// <summary>Efectos de pista (volumen con keyframes en tiempo de la secuencia) y opciones de los efectos.</summary>
public class TrackEffectEditsTests
{
    private const string A1 = "track-a1";
    private const string V1 = "track-v1";

    private static (Project Project, UndoStack Stack) NewStack()
    {
        Project project = Create();
        return (project, new UndoStack(project));
    }

    private static void RoundTrip(UndoStack stack, ICommand command)
    {
        string before = ProjectSerializer.Serialize(stack.Project);
        stack.Execute(command);
        string after = ProjectSerializer.Serialize(stack.Project);
        Assert.NotEqual(before, after);
        Assert.Empty(ModelValidator.Validate(stack.Project));
        Assert.True(stack.Undo());
        Assert.Equal(before, ProjectSerializer.Serialize(stack.Project));
        Assert.True(stack.Redo());
        Assert.Equal(after, ProjectSerializer.Serialize(stack.Project));
    }

    private static readonly SequentialIdGenerator Ids = new();

    private static string AddTo(UndoStack stack, string owner, string type)
    {
        SequenceEditCommand add = Edits.AddEffect(SequenceId, owner, type, ids: Ids);
        RoundTrip(stack, add);
        return Assert.Single(add.CreatedIds);
    }

    [Fact]
    public void TrackVolume_TakesKeyframesInSequenceTime()
    {
        (Project project, UndoStack stack) = NewStack();
        string fx = AddTo(stack, A1, BuiltInEffects.Audio);
        const string gain = BuiltInEffects.AudioParameters.GainDb;

        RoundTrip(stack, Edits.SetKeyframe(SequenceId, A1, fx, gain, S(30), -6));
        RoundTrip(stack, Edits.SetKeyframe(SequenceId, A1, fx, gain, S(0), 0));
        RoundTrip(stack, Edits.SetParameter(SequenceId, A1, fx, gain, -3, S(30)));
        RoundTrip(stack, Edits.SetEffectEnabled(SequenceId, A1, fx, false));

        Parameter parameter = Seq(project).GetTrack(A1).Effects[0].Parameters[gain];
        Assert.Equal([S(0), S(30)], parameter.Keyframes.Select(k => k.Time));
        Assert.Equal(-3, parameter.Keyframes[1].Value);
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetKeyframe(SequenceId, A1, fx, gain, S(-1), 0)));

        RoundTrip(stack, Edits.RemoveEffect(SequenceId, A1, fx));
        Assert.Empty(Seq(project).GetTrack(A1).Effects);
    }

    [Theory]
    [InlineData(A1, BuiltInEffects.Color, "pista de audio")]
    [InlineData(V1, BuiltInEffects.Audio, "pista de video")]
    [InlineData(A1, "otro.efecto", "pista de audio")]
    [InlineData(A1, BuiltInTransitions.Crossfade, "es una transición")]
    [InlineData("clip-1", BuiltInTransitions.Wipe, "es una transición")]
    [InlineData("track-v2", BuiltInEffects.Color, "bloqueada")]
    [InlineData("nada", BuiltInEffects.Color, "ya no está")]
    public void AddEffect_RejectsWhatDoesNotFit(string owner, string type, string expected)
    {
        (Project project, UndoStack stack) = NewStack();
        string before = ProjectSerializer.Serialize(project);

        var ex = Assert.Throws<EditException>(() => stack.Execute(Edits.AddEffect(SequenceId, owner, type)));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, ProjectSerializer.Serialize(project));
    }

    [Fact]
    public void Clips_StillTakeUnknownTypes()
    {
        (Project project, UndoStack stack) = NewStack();
        AddTo(stack, "clip-1", "otro.programa.efecto");
        Assert.Equal("otro.programa.efecto", Seq(project).GetClip("clip-1").Clip.Effects[0].Type);
    }

    [Fact]
    public void TrackEffects_MoveWithinTheTrackStack()
    {
        (Project project, UndoStack stack) = NewStack();
        string first = AddTo(stack, V1, BuiltInEffects.Color);
        AddTo(stack, V1, BuiltInEffects.Vignette);

        RoundTrip(stack, Edits.MoveEffect(SequenceId, V1, first, 1));

        Assert.Equal([BuiltInEffects.Vignette, BuiltInEffects.Color], Seq(project).GetTrack(V1).Effects.Select(e => e.Type));
        Assert.Throws<EditException>(() => stack.Execute(Edits.MoveEffect(SequenceId, V1, first, 2)));
        Assert.Throws<EditException>(() => stack.Execute(Edits.AddEffect(SequenceId, V1, BuiltInEffects.Color, 5)));
        Assert.Throws<EditException>(() => stack.Execute(Edits.RemoveEffect(SequenceId, V1, "nada")));
    }

    [Fact]
    public void Options_FollowTheRegistry_AndUndoExactly()
    {
        (Project project, UndoStack stack) = NewStack();
        string blend = AddTo(stack, "clip-1", BuiltInEffects.Blend);
        string color = AddTo(stack, "clip-1", BuiltInEffects.Color);
        EffectInstance Effect(string id) => Seq(project).GetClip("clip-1").Clip.Effects.Single(e => e.Id == id);

        RoundTrip(stack, Edits.SetEffectOption(SequenceId, "clip-1", blend, BuiltInEffects.BlendOptions.Mode, "screen"));
        RoundTrip(stack, Edits.SetEffectOption(SequenceId, "clip-1", color, BuiltInEffects.ColorOptions.Lut, "rodaje-filtros@1.0.0/frio"));
        RoundTrip(stack, Edits.SetEffectOption(SequenceId, "clip-1", color, BuiltInEffects.CommonOptions.Source, "rodaje-filtros@1.0.0/look-frio"));
        Assert.Equal("screen", Effect(blend).Options[BuiltInEffects.BlendOptions.Mode]);
        Assert.Equal(2, Effect(color).Options.Count);

        RoundTrip(stack, Edits.SetEffectOption(SequenceId, "clip-1", color, BuiltInEffects.ColorOptions.Lut, ""));
        Assert.False(Effect(color).Options.ContainsKey(BuiltInEffects.ColorOptions.Lut));
    }

    [Theory]
    [InlineData(BuiltInEffects.Blend, BuiltInEffects.BlendOptions.Mode, "Screen", "no es un valor")]
    [InlineData(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "calido.cube", "no es un valor")]
    [InlineData(BuiltInEffects.Color, BuiltInEffects.CommonOptions.Source, "mal", "no es un valor")]
    [InlineData(BuiltInEffects.Color, "otra", "x", "no tiene la opción")]
    [InlineData("otro.efecto", BuiltInEffects.BlendOptions.Mode, "screen", "no tiene la opción")]
    public void Options_RejectBadValues(string type, string name, string value, string expected)
    {
        (Project project, UndoStack stack) = NewStack();
        string fx = AddTo(stack, "clip-1", type);
        string before = ProjectSerializer.Serialize(project);

        var ex = Assert.Throws<EditException>(() => stack.Execute(Edits.SetEffectOption(SequenceId, "clip-1", fx, name, value)));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, ProjectSerializer.Serialize(project));
    }

    [Fact]
    public void Options_CheckArguments()
    {
        (Project project, _) = NewStack();
        var ctx = new EditContext(project, Seq(project), new SequentialIdGenerator());
        Assert.Throws<ArgumentNullException>(() => EffectEdits.SetOption(null!, "clip-2", "fx-1", "a", "b"));
        Assert.Throws<ArgumentNullException>(() => EffectEdits.SetOption(ctx, "clip-2", "fx-1", null!, "b"));
        Assert.Throws<ArgumentNullException>(() => EffectEdits.SetOption(ctx, "clip-2", "fx-1", "a", null!));
    }

    [Fact]
    public void LockedTrack_RejectsTrackEffectEdits()
    {
        (Project project, UndoStack stack) = NewStack();
        Track v2 = Seq(project).GetTrack("track-v2");
        v2.Locked = false;
        string fx = AddTo(stack, "track-v2", BuiltInEffects.Color);
        v2.Locked = true;

        Assert.Throws<EditException>(() => stack.Execute(Edits.SetParameter(SequenceId, "track-v2", fx, "exposure", 1, Time.Zero)));
    }
}
