using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Settings;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    /// <summary>
    /// Transiciones: corta el primer clip, pone con Ctrl+T un giro suave de 1 s, lo muestra en el inspector (con su miniatura
    /// animada), comprueba que el visor mezcla en el corte y que las miniaturas salen de la GPU, lo alinea al final del clip
    /// que sale y deshace todo.
    /// </summary>
    private static async Task TransitionsAsync(MainWindow window, PlayerEngine engine)
    {
        EditorSession session = window.Session;
        if (session.MainTrack is not { Clips.Count: > 0 } main || main.Clips[0] is not { Generator: null } clip || clip.Duration < Time.FromSeconds(3))
        {
            return;
        }

        int steps = session.History.Count;
        int clips = main.Clips.Count;
        Time cut = clip.Start + Time.FromTicks(clip.Duration.Ticks / 2).SnapToFrame(session.Sequence.FrameRate);
        session.Select([]);
        session.Seek(cut);
        if (!session.SplitAtPlayhead())
        {
            throw new InvalidOperationException("La autoprueba no pudo cortar el clip para la transición.");
        }

        var preferences = new TransitionPreferences
        {
            Reference = BuiltInTransitions.Spin, Seconds = 1, Alignment = TransitionAlignment.Center, Curve = Interpolation.EaseInOut,
        };
        IReadOnlyList<string> cuts = DefaultTransition.Cuts(session.Sequence, [], cut, session.MainTrack);
        SequenceEditCommand add = DefaultTransition.Build(session.SequenceId, cuts, preferences);
        if (cuts.Count != 1 || !session.Execute(add))
        {
            throw new InvalidOperationException("Ctrl+T no puso la transición en el corte.");
        }

        string id = add.CreatedIds[0];
        session.SelectTransition(id);
        window.InspectorPane.Show(InspectorView.Tab.Properties);
        session.Seek(cut - Time.FromSeconds(0.75));
        await Task.Delay(300);
        FrameCapture before = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        session.Seek(cut);
        await Task.Delay(900);
        await CaptureAsync(window, "transicion");
        FrameCapture middle = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await SaveFrameAsync(middle, "transicion-visor");
        if (middle.Rgba.AsSpan().SequenceEqual(before.Rgba))
        {
            throw new InvalidOperationException("El visor no mezcló la transición en el corte.");
        }

        var previews = new TransitionPreviews(engine.RenderTransitionAsync, () => null);
        TransitionInstance transition = session.FindTransition(id)!.Value.Transition;
        TransitionAnimation animation = await previews.GetAsync(transition, 160, 90)
            ?? throw new InvalidOperationException("La miniatura animada de la transición no se compuso.");
        if (animation.Bgra.Count != TransitionPreviews.FrameCount || animation.Bgra[0].AsSpan().SequenceEqual(animation.Bgra[TransitionPreviews.FrameCount / 2]))
        {
            throw new InvalidOperationException("La miniatura animada de la transición no cambia entre cuadros.");
        }

        var editor = new TransitionEditor(session);
        if (!editor.SetAlignment(id, TransitionAlignment.Before) || session.FindTransition(id)!.Value.Transition.After != Time.Zero)
        {
            throw new InvalidOperationException("La autoprueba no pudo alinear la transición al final del clip que sale.");
        }

        await Task.Delay(300);
        await CaptureAsync(window, "transicion-alineada");
        Program.Metrics.Note($"autoprueba: transición {BuiltInTransitions.Spin} en el corte, vista previa de {animation.Bgra.Count} cuadros");
        session.SelectTransition(null);
        while (session.History.Count > steps && session.Undo())
        {
        }

        if (session.MainTrack!.Transitions.Count > 0 || session.MainTrack.Clips.Count != clips)
        {
            throw new InvalidOperationException("Deshacer no quitó la transición de la autoprueba.");
        }
    }
}
