using Rodaje.App.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Shell;

/// <summary>
/// <c>--autoprueba</c>: lo mínimo para saber que el exe publicado arranca de verdad. Con el clip de prueba en la pista
/// principal y seleccionado, muestra cada pestaña del inspector (y la curva «Bala» en Velocidad), reproduce un segundo y cierra. Sale con 0 si llegó el
/// primer cuadro y nada falló; cualquier excepción queda en <c>rodaje-error.txt</c> y el código es distinto de 0.
/// </summary>
internal static class SelfTestRun
{
    public static async Task RunAsync(MainWindow window, PlayerEngine engine, Metrics metrics)
    {
        int code = 1;
        try
        {
            SelectFirstClip(window);
            foreach (InspectorView.Tab tab in Enum.GetValues<InspectorView.Tab>())
            {
                window.InspectorPane.Show(tab);
                await Task.Delay(250);
            }

            await ShowCurveAsync(window);
            window.InspectorPane.Show(InspectorView.Tab.Properties);
            if (window.Session.Sequence.Duration.Ticks > 0)
            {
                window.Session.Play();
                for (int i = 0; i < 100 && !metrics.HasMilestone("primer_cuadro") && engine.FailureMessage is null; i++)
                {
                    await Task.Delay(100);
                }

                await Task.Delay(1000);
                window.Session.Pause();
            }

            code = engine.FailureMessage is null && (window.Session.Sequence.Duration.Ticks == 0 || metrics.HasMilestone("primer_cuadro")) ? 0 : 1;
            if (code != 0)
            {
                Program.WriteCrash("autoprueba: " + (engine.FailureMessage ?? "no llegó el primer cuadro en 10 s"));
            }
        }
#pragma warning disable CA1031 // La autoprueba informa cualquier fallo con el código de salida.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Program.WriteCrash("autoprueba: " + ex);
            code = 2;
        }

        Program.ExitCode = code;
        window.Close();
    }

    /// <summary>Pone «Bala» al clip para dibujar el editor de curvas y la mini curva con el exe de AOT, y lo deshace.</summary>
    private static async Task ShowCurveAsync(MainWindow window)
    {
        EditorSession session = window.Session;
        if (session.MainTrack is not { Clips.Count: > 0 } main
            || !session.Execute(Edits.ApplySpeedPreset(session.SequenceId, main.Clips[0].Id, SpeedPreset.Bullet, session.SpeedScope)))
        {
            return;
        }

        window.InspectorPane.Show(InspectorView.Tab.Speed);
        await Task.Delay(250);
        session.Undo();
    }

    /// <summary>Selecciona el primer clip de la principal para que las pestañas muestren un clip, no el estado vacío.</summary>
    private static void SelectFirstClip(MainWindow window)
    {
        if (window.Session.MainTrack is { Clips.Count: > 0 } main)
        {
            window.Session.Select([main.Clips[0].Id]);
        }
    }
}
