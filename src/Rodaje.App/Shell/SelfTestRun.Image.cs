using Rodaje.App.Services;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using W = Rodaje.Core.Model.BuiltInEffects.ColorWheelsParameters;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    /// <summary>
    /// Ruedas y curvas en el primer clip y antes/después al 50 %: la mitad izquierda tiene que volver a la imagen sin color y
    /// la derecha seguir corregida. Deshace al terminar.
    /// </summary>
    private static async Task ImageAsync(MainWindow window, PlayerEngine engine)
    {
        EditorSession session = window.Session;
        if (session.MainTrack is not { Clips.Count: > 0 } main)
        {
            return;
        }

        string clipId = main.Clips[0].Id;
        int steps = session.History.Count;
        session.Select([clipId]);
        if (!session.Execute(new SequenceEditCommand("Ruedas y curvas (autoprueba)", session.SequenceId, ctx =>
        {
            string wheels = EffectEdits.AddEffect(ctx, clipId, BuiltInEffects.ColorWheels);
            EffectEdits.SetParameter(ctx, clipId, wheels, W.Gain, -0.5, Core.Timing.Time.Zero);
            EffectEdits.SetParameter(ctx, clipId, wheels, W.LiftBlue, 0.1, Core.Timing.Time.Zero);
            string curves = EffectEdits.AddEffect(ctx, clipId, BuiltInEffects.Curves);
            EffectEdits.SetOption(ctx, clipId, curves, BuiltInEffects.CurvesOptions.Master, "0,0;0.25,0.15;0.75,0.85;1,1");
        })))
        {
            throw new InvalidOperationException("La autoprueba no pudo añadir ruedas y curvas.");
        }

        window.InspectorPane.Show(InspectorView.Tab.Filters);
        await Task.Delay(400);
        FrameCapture graded = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        engine.SetCompare(0.5f);
        await Task.Delay(250);
        FrameCapture compared = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await SaveFrameAsync(compared, "antes-despues-visor");
        await CaptureAsync(window, "ruedas-y-curvas");
        engine.SetCompare(null);
        session.Undo();
        if (session.History.Count != steps)
        {
            throw new InvalidOperationException("Deshacer no quitó las ruedas y curvas de la autoprueba.");
        }

        double leftGraded = MeanLuma(graded, 0, 0.45), leftCompared = MeanLuma(compared, 0, 0.45);
        double rightGraded = MeanLuma(graded, 0.55, 1), rightCompared = MeanLuma(compared, 0.55, 1);
        Program.Metrics.Note($"autoprueba: antes/después, izquierda {leftGraded:0.0} → {leftCompared:0.0}, derecha {rightGraded:0.0} → {rightCompared:0.0}");
        if (leftCompared < 2)
        {
            return;
        }

        if (leftCompared - leftGraded < 3 || Math.Abs(rightCompared - rightGraded) > 1.5)
        {
            throw new InvalidOperationException(
                $"Antes/después no divide la imagen: izquierda {leftGraded:0.0} → {leftCompared:0.0}, derecha {rightGraded:0.0} → {rightCompared:0.0}.");
        }
    }

    private static double MeanLuma(FrameCapture frame, double from, double to)
    {
        double sum = 0;
        long count = 0;
        int x0 = (int)(from * frame.Width), x1 = (int)(to * frame.Width);
        for (int y = 0; y < frame.Height; y += 4)
        {
            for (int x = x0; x < x1; x += 4)
            {
                int i = ((y * frame.Width) + x) * 4;
                sum += (0.2126 * frame.Rgba[i]) + (0.7152 * frame.Rgba[i + 1]) + (0.0722 * frame.Rgba[i + 2]);
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }
}
