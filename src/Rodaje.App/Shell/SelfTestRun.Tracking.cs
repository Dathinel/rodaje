using Rodaje.App.Services;
using Rodaje.App.Services.Tracking;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    /// <summary>
    /// Seguimiento en el primer clip: cuatro esquinas seguidas 20 cuadros con el decodificador de verdad, la pestaña con su
    /// gráfica y los puntos en el visor, y una zona pixelada que las sigue; deshace al terminar.
    /// </summary>
    private static async Task TrackingAsync(MainWindow window, PlayerEngine engine)
    {
        EditorSession session = window.Session;
        if (session.MainTrack is not { Clips.Count: > 0 } main || main.Clips[0] is not { Generator: null } clip
            || session.Project.FindMedia(clip.MediaId) is not { HasVideo: true, Width: > 0, Height: > 0, FrameRate: { } rate } media)
        {
            return;
        }

        int steps = session.History.Count;
        session.Select([clip.Id]);
        session.Seek(clip.Start);
        SequenceEditCommand create = TrackEdits.Create(session.SequenceId, clip.Id, PointTrackKind.Corners,
            TrackEdits.DefaultPoints(PointTrackKind.Corners, (double)media.Width / media.Height), "Autoprueba", TrackEdits.Palette[0]);
        if (!session.Execute(create))
        {
            throw new InvalidOperationException("La autoprueba no pudo crear el seguimiento.");
        }

        string trackId = create.CreatedIds[0];
        EffectInstance track = session.Sequence.FindClip(clip.Id)!.Value.Clip.Effects.First(e => e.Id == trackId);
        Time end = Time.Min(clip.SourceOut - Time.FromFrames(1, rate), clip.SourceIn + Time.FromFrames(20, rate));
        var request = new PointTrackRequest(media.Path, clip.SourceIn, end, true, PointTrackKind.Corners, TrackEdits.StartAt(track, clip.SourceIn), 0.7f,
            _ => CropRect.None);
        var frames = new List<PointTrackFrame>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await PointTracking.RunAsync(request, frames.Add, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        double fps = frames.Count / Math.Max(1e-3, watch.Elapsed.TotalSeconds);
        if (frames.Count < 2)
        {
            throw new InvalidOperationException("El seguimiento no entregó cuadros.");
        }

        var samples = new PointTrack([.. frames.Select(f => f.Time)], [.. frames.Select(f => f.Samples)]);
        session.Execute(TrackEdits.SetSamples(session.SequenceId, clip.Id, trackId, samples, "Seguir (autoprueba)"));
        window.InspectorPane.Show(InspectorView.Tab.Tracking);
        session.Seek(clip.TimeMap.TimelineOf(frames[frames.Count / 2].Time));
        await Task.Delay(400);
        await CaptureAsync(window, "seguimiento");
        await SaveFrameAsync(await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5)), "seguimiento-visor");

        TrackApplication? zone = TrackApply.Prepare(session.Project, session.Sequence, session.Sequence.FindClip(clip.Id)!.Value.Clip, trackId,
            TrackUse.PixelateZone, session.Position, null, false, out string problem);
        if (zone is null || !session.Execute(zone.Command))
        {
            throw new InvalidOperationException("La autoprueba no pudo aplicar el seguimiento: " + problem);
        }

        await Task.Delay(250);
        await SaveFrameAsync(await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5)), "seguimiento-zona-visor");
        double confidence = frames.Skip(1).Average(f => f.Samples.Min(s => s.Confidence));
        Program.Metrics.Note($"autoprueba: seguimiento de 4 esquinas, {frames.Count} cuadros a {fps:0} cuadros/s, confianza media {confidence:0.00}");
        while (session.History.Count > steps && session.Undo())
        {
        }

        if (session.Sequence.FindClip(clip.Id)!.Value.Clip.Effects.Exists(e => e.Type == BuiltInEffects.Track))
        {
            throw new InvalidOperationException("Deshacer no quitó el seguimiento de la autoprueba.");
        }
    }
}
