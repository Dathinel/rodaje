using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Model;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    /// <summary>
    /// Interpolación F5a: selecciona un clip de video, pone «Flujo óptico» con el <see cref="SpeedEditor"/>,
    /// comprueba el modo en el modelo y la marca «≈», deshace y comprueba que volvió a Nearest y sin marca.
    /// </summary>
    private static async Task InterpolationAsync(MainWindow window)
    {
        EditorSession session = window.Session;
        if (session.MainTrack is not { Clips.Count: > 0 } main)
        {
            return;
        }

        string id = main.Clips[0].Id;
        session.Select([id]);
        window.InspectorPane.Show(InspectorView.Tab.Speed);
        await Task.Delay(250);

        var editor = new SpeedEditor(session, SpeedCommands.Default);
        if (!editor.SetFrameSampling(FrameSampling.OpticalFlow))
        {
            throw new InvalidOperationException("La autoprueba no pudo poner flujo óptico.");
        }

        Clip clip = session.Sequence.GetClip(id).Clip;
        if (clip.Speed?.FrameSampling != FrameSampling.OpticalFlow)
        {
            throw new InvalidOperationException("El clip no guardó el muestreo de flujo óptico.");
        }

        string? badge = SpeedBadge.Text(clip);
        if (badge is null || !badge.Contains(SpeedBadge.Interpolated, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"La marca de velocidad no lleva «≈»: «{badge}».");
        }

        await Task.Delay(100);
        await CaptureAsync(window, "velocidad-flujo-optico");

        session.Undo();
        Clip undoneClip = session.Sequence.GetClip(id).Clip;
        if (undoneClip.Speed is not null && undoneClip.Speed.FrameSampling != FrameSampling.Nearest)
        {
            throw new InvalidOperationException("Deshacer no devolvió el muestreo a Nearest.");
        }

        if (SpeedBadge.Text(undoneClip) is not null)
        {
            throw new InvalidOperationException($"El clip conservó la marca tras deshacer: «{SpeedBadge.Text(undoneClip)}».");
        }

        await Task.Delay(100);
    }
}
