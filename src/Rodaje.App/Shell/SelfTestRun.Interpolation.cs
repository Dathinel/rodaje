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

    /// <summary>Interpolación F2: un clip a 0,5× con mezcla compone la capa a t = 0,5 y difiere del cuadro más cercano.</summary>
    private static async Task BlendAsync(MainWindow window, PlayerEngine engine)
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
            if (!session.Execute(Edits.SetSpeed(session.SequenceId, clip.Id, new Rational(1, 2), session.SpeedScope)))
            {
                throw new InvalidOperationException("La autoprueba no pudo poner el clip a 0,5×.");
            }

            // El clip de prueba y la secuencia tienen la misma cadencia: un cuadro impar a 0,5× cae a mitad del par.
            session.Seek(target);
            FrameCapture nearest = await CaptureAtAsync(target, FrameSampling.Nearest);
            if (nearest.Composition.LayersDrawn == 0 || nearest.Composition.LayersSkipped != 0)
            {
                throw new InvalidOperationException("La autoprueba no recibió la capa con Nearest.");
            }

            if (!session.Execute(new SequenceEditCommand("Mezcla de cuadros (autoprueba)", session.SequenceId,
                ctx => ctx.Sequence.GetClip(clip.Id).Clip.Speed!.FrameSampling = FrameSampling.Blend)))
            {
                throw new InvalidOperationException("La autoprueba no pudo activar la mezcla de cuadros.");
            }

            FrameCapture blended = await CaptureAtAsync(target, FrameSampling.Blend);
            if (blended.Composition.LayersDrawn == 0 || blended.Composition.LayersSkipped != 0
                || nearest.Rgba.AsSpan().SequenceEqual(blended.Rgba))
            {
                throw new InvalidOperationException("La mezcla a t = 0,5 no compuso la capa o no cambió los píxeles frente a Nearest.");
            }

            Program.Metrics.Note("autoprueba: interpolación a 0,5×, t = 0,5; capa compuesta y distinta de Nearest");
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
                        && (mode == FrameSampling.Nearest || (s.HasNext && Math.Abs(s.Fraction - 0.5f) < 0.0001f)))))
                {
                    return capture;
                }
            }

            throw new InvalidOperationException($"La autoprueba no recibió el cuadro de interpolación con {mode} en el instante pedido.");
        }
    }

    /// <summary>Interpolación F4: un clip a 0,25× con flujo compone la capa a t = 0,75 y difiere del cuadro más cercano.</summary>
    private static async Task OpticalFlowAsync(MainWindow window, PlayerEngine engine)
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
            if (!session.Execute(Edits.SetSpeed(session.SequenceId, clip.Id, new Rational(1, 4), session.SpeedScope)))
            {
                throw new InvalidOperationException("La autoprueba no pudo poner el clip a 0,25×.");
            }

            // El clip de prueba y la secuencia tienen la misma cadencia: el cuadro 15 a 0,25× cae a tres cuartos del par.
            session.Seek(target);
            FrameCapture nearest = await CaptureAtAsync(target, FrameSampling.Nearest);
            if (nearest.Composition.LayersDrawn == 0 || nearest.Composition.LayersSkipped != 0)
            {
                throw new InvalidOperationException("La autoprueba no recibió la capa con Nearest.");
            }

            if (!session.Execute(new SequenceEditCommand("Flujo óptico (autoprueba)", session.SequenceId,
                ctx => ctx.Sequence.GetClip(clip.Id).Clip.Speed!.FrameSampling = FrameSampling.OpticalFlow)))
            {
                throw new InvalidOperationException("La autoprueba no pudo activar el flujo óptico.");
            }

            FrameCapture blended = await CaptureAtAsync(target, FrameSampling.OpticalFlow);
            if (blended.Composition.LayersDrawn == 0 || blended.Composition.LayersSkipped != 0
                || nearest.Rgba.AsSpan().SequenceEqual(blended.Rgba))
            {
                throw new InvalidOperationException("El flujo a t = 0,75 no compuso la capa o no cambió los píxeles frente a Nearest.");
            }

            Program.Metrics.Note("autoprueba: interpolación a 0,25×, t = 0,75; capa compuesta y distinta de Nearest");
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
                        && (mode == FrameSampling.Nearest || (s.HasNext && Math.Abs(s.Fraction - 0.75f) < 0.0001f)))))
                {
                    return capture;
                }
            }

            throw new InvalidOperationException($"La autoprueba no recibió el cuadro de interpolación con {mode} en el instante pedido.");
        }
    }
}
