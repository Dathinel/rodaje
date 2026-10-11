using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Windows.Foundation;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Herramienta de envolvente (E) sobre los clips de audio: un clic pone un punto y lo arrastra, agarrar un punto lo
/// mueve, y el clic derecho sobre un punto lo quita. Cada arrastre es un paso del historial.
/// </summary>
internal sealed partial class TimelineView
{
    private string? _envelopeClipId;
    private Time _envelopePoint;
    private int _envelopeIndex;

    /// <summary>Si el clic va a la envolvente: herramienta E, clip de audio y envolventes sin bloquear.</summary>
    private bool EditsEnvelope(TimelineHit hit) =>
        State.Tool == TimelineTool.Envelope && !State.LockEnvelopes
        && hit.Clip is not null && hit.Row is { Kind: TrackKind.Audio } row && !row.Track.Locked;

    private void PressEnvelope(TimelineHit hit, Point p, bool rightButton, PointerRoutedEventArgs e)
    {
        EditorSession session = Session!;
        Clip clip = hit.Clip!;
        TrackRow row = hit.Row!;
        double y = ContentY(p.Y);
        Time? grabbed = EnvelopeEdits.PointAt(clip, row, Viewport, p.X, y);
        if (rightButton)
        {
            if (grabbed is { } point)
            {
                session.Execute(EnvelopeEdits.RemovePoint(session.SequenceId, clip.Id, point));
            }

            return;
        }

        if (!session.SelectedClipIds.Contains(clip.Id))
        {
            session.Select([clip.Id]);
        }

        Time at = grabbed ?? Clamp(hit.Time.SnapToFrame(session.Sequence.FrameRate), clip.Start, clip.End);
        session.BeginGesture("Envolvente de volumen");
        if (!session.TryExecute(EnvelopeEdits.SetPoint(session.SequenceId, clip.Id, grabbed, at, EnvelopeEdits.GainDbAtY(row, y))))
        {
            session.CancelGesture();
            return;
        }

        _envelopeClipId = clip.Id;
        List<Time> points = PointTimes(clip.Id);
        _envelopeIndex = Math.Max(0, points.IndexOf(at));
        _envelopePoint = at;
        _mode = PointerMode.DraggingEnvelope;
        Capture(LanesHost, e);
    }

    private void ContinueEnvelopeDrag(Point p, TrackLayout layout)
    {
        EditorSession session = Session!;
        if (_envelopeClipId is not { } id || session.Sequence.FindClip(id) is not { } found || layout.Find(found.Track.Id) is not { } row)
        {
            return;
        }

        Clip clip = found.Clip;
        Time at = Clamp(Viewport.XToTime(p.X).SnapToFrame(session.Sequence.FrameRate), clip.Start, clip.End);
        double gainDb = EnvelopeEdits.GainDbAtY(row, ContentY(p.Y));
        if (session.TryExecute(EnvelopeEdits.SetPoint(session.SequenceId, id, _envelopePoint, at, gainDb)))
        {
            // El punto no pasa a sus vecinos, así que conserva su lugar en la lista: se sigue desde donde quedó.
            List<Time> points = PointTimes(id);
            if (_envelopeIndex < points.Count)
            {
                _envelopePoint = points[_envelopeIndex];
            }
        }
    }

    private List<Time> PointTimes(string clipId) =>
        Session!.Sequence.FindClip(clipId) is { } found ? [.. ClipDecorations.GainPoints(found.Clip).Select(p => p.Time)] : [];

    private void FinishEnvelopeDrag()
    {
        _envelopeClipId = null;
        if (Session!.IsInGesture)
        {
            Session.EndGesture();
        }
    }

    private static Time Clamp(Time value, Time low, Time high) => value < low ? low : value > high ? high : value;
}
