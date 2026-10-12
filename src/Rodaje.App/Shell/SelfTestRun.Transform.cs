using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    /// <summary>
    /// Transformación del primer clip: una trayectoria curva con keyframes de X e Y, media escala e inclinación; después el
    /// desenfoque de movimiento a 180°, que tiene que cambiar la imagen del visor. Deja capturas de Propiedades (secciones,
    /// animaciones y gráfica), del visor con las asas y la trayectoria y del cuadro con el desenfoque, y lo deshace todo.
    /// </summary>
    private static async Task TransformAsync(MainWindow window, PlayerEngine engine)
    {
        EditorSession session = window.Session;
        if (session.MainTrack is not { Clips.Count: > 0 } main || main.Clips[0].Duration < Time.FromSeconds(1))
        {
            return;
        }

        Clip clip = main.Clips[0];
        string clipId = clip.Id;
        int steps = session.History.Count;
        session.Select([clipId]);
        Time first = EffectCommands.KeyframeTime(clip, clip.Start);
        // Un recorrido rápido (1,5 s de lado a lado del cuadro): el obturador a 180° deja una estela de unos 15 px.
        Time last = EffectCommands.KeyframeTime(clip, clip.Start + Time.Min(clip.Duration - Time.FromTicks(1), Time.FromSeconds(1.5)));
        double w = session.Sequence.Width;
        double h = session.Sequence.Height;
        if (!session.Execute(new SequenceEditCommand("Trayectoria (autoprueba)", session.SequenceId, ctx =>
        {
            string id = EffectEdits.EnsureEffect(ctx, clipId, BuiltInEffects.Transform)!;
            EffectEdits.SetParameter(ctx, clipId, id, T.Scale, 0.45, first);
            EffectEdits.SetParameter(ctx, clipId, id, T.SkewX, 8, first);
            EffectEdits.ReplaceKeyframes(ctx, clipId, id, T.X,
            [
                new Keyframe(first, -0.3 * w, Interpolation.Bezier) { Ease = new BezierEase(0.3, 0, 0.2, 1), SpatialOut = 0.1 * w },
                new Keyframe(last, 0.3 * w) { SpatialIn = -0.1 * w },
            ]);
            EffectEdits.ReplaceKeyframes(ctx, clipId, id, T.Y,
            [
                new Keyframe(first, 0.15 * h) { SpatialOut = -0.45 * h },
                new Keyframe(last, 0.15 * h) { SpatialIn = -0.45 * h },
            ]);
        })))
        {
            throw new InvalidOperationException("La autoprueba no pudo animar la transformación.");
        }

        session.Seek(clip.Start + Time.FromSeconds(Math.Min(0.6, clip.Duration.ToSeconds() / 3)));
        window.InspectorPane.Show(InspectorView.Tab.Properties);
        await WaitForVideoBoxAsync(engine, clipId);
        await Task.Delay(400);
        await CaptureAsync(window, "transformacion-propiedades");
        FrameCapture still = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await SaveClientAsync(window, "transformacion-visor");

        var editor = new ClipPropertyEditor(session);
        if (!editor.SetValue(session.Sequence.GetClip(clipId).Clip, BuiltInEffects.Transform, T.ShutterAngle, 180, "Desenfoque de movimiento (autoprueba)"))
        {
            throw new InvalidOperationException("La autoprueba no pudo encender el desenfoque de movimiento.");
        }

        await Task.Delay(400);
        FrameCapture blurred = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await SaveFrameAsync(blurred, "desenfoque-movimiento-visor");
        window.InspectorPane.Properties.ShowAnimationForSelfTest(true);
        await Task.Delay(300);
        await CaptureAsync(window, "transformacion-animacion");
        window.InspectorPane.Properties.ShowAnimationForSelfTest(true, graphOnly: true);
        await Task.Delay(300);
        await CaptureAsync(window, "transformacion-grafica");
        window.InspectorPane.Properties.ShowAnimationForSelfTest(false);
        int changed = 0;
        for (int i = 0; i + 3 < Math.Min(still.Rgba.Length, blurred.Rgba.Length); i += 4)
        {
            if (Math.Abs(still.Rgba[i] - blurred.Rgba[i]) + Math.Abs(still.Rgba[i + 1] - blurred.Rgba[i + 1]) > 24)
            {
                changed++;
            }
        }

        Program.Metrics.Note($"autoprueba: el desenfoque de movimiento cambia {changed} píxeles del visor");

        // Las asas solo se ven con Propiedades a la vista: con otra pestaña el visor queda limpio para lo que sigue.
        window.InspectorPane.Show(InspectorView.Tab.History);
        session.Undo();
        session.Undo();
        if (session.History.Count != steps || ClipPropertyEditor.FindEffect(session.Sequence.GetClip(clipId).Clip, BuiltInEffects.Transform) is not null)
        {
            throw new InvalidOperationException("Deshacer no quitó la transformación de la autoprueba.");
        }

        if (changed < 500 && MeanLuma(still, 0, 1) > 2)
        {
            throw new InvalidOperationException($"El desenfoque de movimiento no se ve en el visor: solo cambia {changed} píxeles.");
        }
    }

    private static async Task WaitForVideoBoxAsync(PlayerEngine engine, string clipId)
    {
        for (int i = 0; i < 50; i++)
        {
            if (engine.VideoBoxes.Any(b => b.ClipId == clipId && b.Transform.Shape is not null))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException("El visor no recibió la capa transformada en 5 s.");
    }

    /// <summary>La ventana compuesta de verdad (con la imagen de la cadena de intercambio debajo de las asas).</summary>
    private static async Task SaveClientAsync(MainWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable(CapturesVariable) is { Length: > 0 } folder && WindowCapture.Client(window.Hwnd) is { } shot)
        {
            await SavePngAsync(folder, name, shot.Bgra, shot.Width, shot.Height);
        }
    }
}
