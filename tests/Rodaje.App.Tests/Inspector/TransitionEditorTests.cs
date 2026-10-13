using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Inspector;

public class TransitionEditorTests
{
    private static async Task<(EditorSession Session, TransitionEditor Editor, string Id)> WithTransition()
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        var command = Edits.AddTransition(session.SequenceId, session.MainTrack!.Clips[0].Id, BuiltInTransitions.Crossfade);
        Assert.True(session.Execute(command));
        return (session, new TransitionEditor(session), command.CreatedIds[0]);
    }

    [Fact]
    public void Seleccionada_PorSuIdEnLaSeleccion() => UiThread.Run(async () =>
    {
        (EditorSession session, TransitionEditor editor, string id) = await WithTransition();
        Assert.Null(editor.Selected());
        session.SelectTransition(id);
        TransitionTarget target = editor.Selected()!;
        Assert.Equal(id, target.Transition.Id);
        Assert.Same(session.MainTrack!.Clips[0], target.From);
        Assert.Same(session.MainTrack.Clips[1], target.To);
        Assert.Null(editor.Find("otra"));
    });

    [Fact]
    public void TipoDuracionParametrosYOpciones() => UiThread.Run(async () =>
    {
        (EditorSession session, TransitionEditor editor, string id) = await WithTransition();
        Assert.Equal(18, TransitionEditor.BuiltInTypes.Count);
        Assert.Equal(15, TransitionEditor.TypesFor(TrackKind.Video).Count);
        Assert.Equal(3, TransitionEditor.TypesFor(TrackKind.Audio).Count);

        Assert.True(editor.SetType(id, BuiltInTransitions.Wipe));
        Assert.False(editor.SetType(id, BuiltInTransitions.Wipe));
        TransitionInstance transition = editor.Find(id)!.Transition;
        Assert.Equal(0.05, TransitionEditor.Read(transition, BuiltInTransitions.WipeParameters.Softness));

        int steps = session.History.Count;
        Assert.True(editor.SetParameter(id, BuiltInTransitions.WipeParameters.Angle, 45));
        Assert.True(editor.SetParameter(id, BuiltInTransitions.WipeParameters.Angle, 90));
        Assert.False(editor.SetParameter(id, BuiltInTransitions.WipeParameters.Angle, 90));
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal(90, editor.Find(id)!.Transition.Parameters[BuiltInTransitions.WipeParameters.Angle]);

        Assert.True(editor.SetDuration(id, 1.01));
        Assert.Equal(Time.FromSeconds(1), editor.Find(id)!.Transition.Duration);
        (double minimum, double maximum) = editor.DurationRange(editor.Find(id)!);
        Assert.Equal(1 / 30.0, minimum, 9);
        Assert.Equal(10, maximum);

        Assert.True(editor.SetType(id, BuiltInTransitions.Push));
        Assert.Equal("left", TransitionEditor.ReadOption(editor.Find(id)!.Transition, BuiltInTransitions.PushOptions.Direction));
        Assert.True(editor.SetOption(id, BuiltInTransitions.PushOptions.Direction, "up"));
        Assert.False(editor.SetOption(id, BuiltInTransitions.PushOptions.Direction, "up"));

        Assert.True(editor.SetPreset(id, "p@1.0.0/zoom"));
        Assert.False(editor.SetPreset(id, "p@1.0.0/zoom"));
        transition = editor.Find(id)!.Transition;
        Assert.Equal(BuiltInTransitions.Preset, transition.Type);
        Assert.Equal("p@1.0.0/zoom", transition.Options[BuiltInTransitions.PresetOptions.Preset]);

        Assert.True(editor.Remove(id));
        Assert.Empty(session.MainTrack!.Transitions);
        Assert.False(editor.Remove(id));
    });
}
