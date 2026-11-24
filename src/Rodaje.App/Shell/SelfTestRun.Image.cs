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
        double bare = MeanLuma(await CaptureSettledAsync(engine), 0, 1);
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
        // El cambio de la sesión llega al hilo de render uno o varios cuadros después: se espera al cuadro con las ruedas (ganancia −0,5).
        FrameCapture graded = await CaptureSettledAsync(engine, c => Math.Abs(MeanLuma(c, 0, 1) - bare) >= 3);
        double gradedLeft = MeanLuma(graded, 0, 0.45);
        // Por el botón: al abrir el inspector cambia el tamaño del visor y el visor vuelve a aplicar el estado del botón al motor.
        window.PlayerPane.SetCompareForSelfTest(true);
        // El cuadro con la comparación puede tardar uno o dos cuadros en llegar: se espera a que la mitad izquierda cambie.
        FrameCapture compared = await CaptureSettledAsync(engine, c => MeanLuma(c, 0, 0.45) - gradedLeft >= 3 || MeanLuma(c, 0, 0.45) < 2);
        await SaveFrameAsync(compared, "antes-despues-visor");
        await CaptureAsync(window, "ruedas-y-curvas");
        window.PlayerPane.SetCompareForSelfTest(false);
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
                $"Antes/después no divide la imagen: izquierda {leftGraded:0.0} → {leftCompared:0.0}, derecha {rightGraded:0.0} → {rightCompared:0.0} " +
                $"(sin ruedas {bare:0.0}; efectos saltados {graded.Composition.EffectsSkipped} → {compared.Composition.EffectsSkipped}, intermedios {compared.Composition.PoolBytes / (1024 * 1024)} MB).");
        }
    }

    /// <summary>
    /// Un cuadro con todos sus efectos: los sombreadores nuevos (ruedas, curvas…) se compilan en segundo plano y, mientras tanto, el
    /// cuadro sale sin ellos. Con la máquina cargada eso pasaba de los 400 ms de espera y la comparación antes/después medía dos cuadros
    /// sin color (intermitente, también antes de D-066).
    /// </summary>
    internal static async Task<FrameCapture> CaptureSettledAsync(PlayerEngine engine, Func<FrameCapture, bool>? done = null)
    {
        FrameCapture? last = null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(250);
            try
            {
                last = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                continue;
            }

            if (last.Composition.EffectsSkipped == 0 && (done?.Invoke(last) ?? true))
            {
                return last;
            }
        }

        return last ?? await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
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
