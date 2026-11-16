using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

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

    /// <summary>Un clip a la velocidad dada con mezcla o flujo compone la capa a la fracción esperada y difiere del cuadro más cercano.</summary>
    private static async Task SampledAsync(MainWindow window, PlayerEngine engine, Rational rate, FrameSampling sampling, float fraction)
    {
        EditorSession session = window.Session;
        if (session.MainTrack is not { Clips.Count: > 0 } main)
        {
            return;
        }

        Clip clip = main.Clips[0];
        int steps = session.History.Count;
        Time position = session.EditPosition;
        Time target = clip.Start + Time.FromFrames(15, session.Sequence.FrameRate);
        try
        {
            if (!session.Execute(Edits.SetSpeed(session.SequenceId, clip.Id, rate, session.SpeedScope)))
            {
                throw new InvalidOperationException($"La autoprueba no pudo poner el clip a {rate}.");
            }

            // El clip de prueba y la secuencia tienen la misma cadencia: la fracción del cuadro 15 sale de la velocidad.
            session.Seek(target);
            FrameCapture nearest = await CaptureAtAsync(target, FrameSampling.Nearest);
            if (nearest.Composition.LayersDrawn == 0 || nearest.Composition.LayersSkipped != 0)
            {
                throw new InvalidOperationException("La autoprueba no recibió la capa con Nearest.");
            }

            if (!session.Execute(new SequenceEditCommand("Cuadros intermedios (autoprueba)", session.SequenceId,
                ctx => ctx.Sequence.GetClip(clip.Id).Clip.Speed!.FrameSampling = sampling)))
            {
                throw new InvalidOperationException($"La autoprueba no pudo activar {sampling}.");
            }

            FrameCapture blended = await CaptureAtAsync(target, sampling);
            if (blended.Composition.LayersDrawn == 0 || blended.Composition.LayersSkipped != 0
                || nearest.Rgba.AsSpan().SequenceEqual(blended.Rgba))
            {
                throw new InvalidOperationException($"{sampling} a {rate} no compuso la capa o no cambió los píxeles frente a Nearest.");
            }

            Program.Metrics.Note($"autoprueba: {sampling} a {rate}, t = {fraction}; capa compuesta y distinta de Nearest");
        }
        finally
        {
            while (session.History.Count > steps) session.Undo();
            session.Seek(position);
            await CaptureAtAsync(position);
        }

        async Task<FrameCapture> CaptureAtAsync(Time at, FrameSampling? mode = null)
        {
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(100);
                FrameCapture capture = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (capture.TimelineTime == at && session.EditPosition == at
                    && (mode is null || capture.Samples.Any(s => s.ClipId == clip.Id && s.Sampling == mode
                        && (mode == FrameSampling.Nearest || (s.HasNext && Math.Abs(s.Fraction - fraction) < 0.0001f)))))
                {
                    return capture;
                }
            }

            throw new InvalidOperationException($"La autoprueba no recibió el cuadro de interpolación con {mode} en el instante pedido.");
        }
    }
}
