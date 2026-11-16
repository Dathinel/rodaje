using Rodaje.App.Services;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    private static async Task InterpolationAsync(MainWindow window, PlayerEngine engine)
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
}
