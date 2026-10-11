using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Foundation;
using Windows.System;

namespace Rodaje.App.Views.Timeline;

/// <summary>Ratón, rueda y soltar medios sobre la línea de tiempo.</summary>
internal sealed partial class TimelineView
{
    private const double DragThreshold = 4;
    private const double WheelStep = 1.2;
    private const double EdgeScrollZone = 24;

    private static readonly InputCursor ArrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private static readonly InputCursor ResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputCursor MoveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private static readonly InputCursor CrossCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);

    private PointerMode _mode;
    private Point _pressPoint;
    private Time _pressTime;
    private TimelineHit _pressHit;
    private bool _pressCtrl;
    private ClipDragSession? _drag;
    private string? _markerId;
    private string? _dropMediaId;
    private Pointer? _captured;
    private UIElement? _captureOwner;

    private enum PointerMode
    {
        None,
        PendingClip,
        DraggingClip,
        Marquee,
        Scrub,
        DraggingMarker,
        Pan,
        DraggingTransition,
        DraggingEnvelope,
    }

    /// <summary>Esc o un proyecto nuevo cortan lo que el puntero estaba haciendo (la sesión ya revirtió el gesto).</summary>
    private void AbortPointerGesture()
    {
        if (_mode is PointerMode.DraggingMarker or PointerMode.DraggingTransition or PointerMode.DraggingEnvelope && Session!.IsInGesture)
        {
            Session.CancelGesture();
        }

        _transitionId = null;
        _envelopeClipId = null;
        _drag = null;
        _markerId = null;
        _mode = PointerMode.None;
        ClearDragOverlay();
        _overlay.Marquee = null;
        ReleaseCapture();
        Invalidate();
    }

    private void ClearDragOverlay()
    {
        _overlay.Ghost = null;
        _overlay.LinkedGhost = null;
        _overlay.InsertLine = null;
        _overlay.SnapGuide = null;
        _overlay.Balloon = null;
    }

    private void Capture(UIElement owner, PointerRoutedEventArgs e)
    {
        if (owner.CapturePointer(e.Pointer))
        {
            _captured = e.Pointer;
            _captureOwner = owner;
        }
    }

    private void ReleaseCapture()
    {
        if (_captured is not null && _captureOwner is not null)
        {
            _captureOwner.ReleasePointerCapture(_captured);
        }

        _captured = null;
        _captureOwner = null;
    }

    private DragOptions Options() => new()
    {
        Snapping = State.Snapping,
        MagneticTrackId = State.MagneticMainTrack ? Session!.MainTrack?.Id : null,
        AutoRipple = State.AutoRipple,
        RippleScope = State.RippleScope,
        IgnoreGroups = State.IgnoreGroups,
        PixelsPerSecond = Viewport.PixelsPerSecond,
    };

    private double ContentY(double y) => y + _renderer!.ScrollY;

    private void OnLanesPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Session is null || _renderer?.Layout is not { } layout || _mode != PointerMode.None)
        {
            return;
        }

        TakeKeyboardFocus(FocusState.Pointer);
        PointerPoint point = e.GetCurrentPoint(LanesHost);
        PointerPointProperties props = point.Properties;
        Point p = point.Position;
        bool ctrl = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        e.Handled = true;

        if (props.IsMiddleButtonPressed)
        {
            _mode = PointerMode.Pan;
            _pressPoint = p;
            Capture(LanesHost, e);
            return;
        }

        TimelineHit hit = TimelineHitTest.Hit(layout, Viewport, p.X, ContentY(p.Y));
        if (EditsEnvelope(hit) && (props.IsLeftButtonPressed || props.IsRightButtonPressed))
        {
            PressEnvelope(hit, p, props.IsRightButtonPressed, e);
            return;
        }

        if (State.Tool != TimelineTool.Zoom && (props.IsLeftButtonPressed || props.IsRightButtonPressed) && TryPressTransition(layout, p, e))
        {
            return;
        }

        if (props.IsRightButtonPressed)
        {
            if (hit.Clip is { } clicked && !Session.SelectedClipIds.Contains(clicked.Id))
            {
                Session.Select([clicked.Id]);
            }

            return;
        }

        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        if (State.Tool == TimelineTool.Zoom)
        {
            bool zoomOut = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu);
            ZoomAt(p.X, zoomOut ? 1 / TimelineState.ZoomStep / TimelineState.ZoomStep : TimelineState.ZoomStep * TimelineState.ZoomStep);
            return;
        }

        _pressPoint = p;
        _pressTime = hit.Time;
        _pressHit = hit;
        _pressCtrl = ctrl;
        if (hit.Clip is { } clip)
        {
            if (ctrl)
            {
                Session.ToggleSelection(clip.Id);
            }
            else if (!Session.SelectedClipIds.Contains(clip.Id))
            {
                Session.Select([clip.Id]);
            }

            _mode = State.Tool == TimelineTool.Selection ? PointerMode.Marquee : PointerMode.PendingClip;
        }
        else
        {
            if (!ctrl)
            {
                Session.ClearSelection();
            }

            _mode = PointerMode.Marquee;
        }

        Capture(LanesHost, e);
    }

    private void OnLanesPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Session is null || _renderer?.Layout is not { } layout)
        {
            return;
        }

        Point p = e.GetCurrentPoint(LanesHost).Position;
        switch (_mode)
        {
            case PointerMode.None:
                UpdateCursor(TimelineHitTest.Hit(layout, Viewport, p.X, ContentY(p.Y)), HitTransition(layout, p).Kind);
                return;
            case PointerMode.Pan:
                Viewport.ScrollX -= p.X - _pressPoint.X;
                Viewport.ClampScroll(Session.Sequence.Duration);
                SetScrollY(_renderer.ScrollY - (p.Y - _pressPoint.Y));
                _pressPoint = p;
                Invalidate();
                return;
            case PointerMode.PendingClip:
                if (Distance(p, _pressPoint) >= DragThreshold)
                {
                    StartClipDrag(e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu));
                    ContinueClipDrag(p, layout);
                }

                return;
            case PointerMode.DraggingClip:
                EdgeScroll(p.X);
                ContinueClipDrag(p, layout);
                return;
            case PointerMode.DraggingTransition:
                ContinueTransitionDrag(p);
                return;
            case PointerMode.DraggingEnvelope:
                ContinueEnvelopeDrag(p, layout);
                return;
            case PointerMode.Marquee:
                if (Distance(p, _pressPoint) >= DragThreshold)
                {
                    EdgeScroll(p.X);
                    double x0 = Viewport.TimeToX(_pressTime);
                    _overlay.Marquee = new Rect(new Point(x0, ContentY(_pressPoint.Y)), new Point(p.X, ContentY(p.Y)));
                    Invalidate();
                }

                return;
            default:
                return;
        }
    }

    private void OnLanesPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (Session is null || _renderer?.Layout is not { } layout)
        {
            return;
        }

        Point p = e.GetCurrentPoint(LanesHost).Position;
        PointerMode mode = _mode;
        _mode = PointerMode.None;
        switch (mode)
        {
            case PointerMode.DraggingClip:
                FinishClipDrag();
                break;
            case PointerMode.Marquee:
                FinishMarquee(p, layout);
                break;
            case PointerMode.DraggingTransition:
                FinishTransitionDrag();
                break;
            case PointerMode.DraggingEnvelope:
                FinishEnvelopeDrag();
                break;
            default:
                break;
        }

        ReleaseCapture();
        e.Handled = true;
    }

    private void OnLanesPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_mode == PointerMode.DraggingClip)
        {
            FinishClipDrag();
        }
        else if (_mode == PointerMode.DraggingTransition)
        {
            FinishTransitionDrag();
        }
        else if (_mode == PointerMode.DraggingEnvelope)
        {
            FinishEnvelopeDrag();
        }

        _mode = PointerMode.None;
        _overlay.Marquee = null;
        _captured = null;
        _captureOwner = null;
        Invalidate();
    }

    private void OnLanesPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_mode == PointerMode.None)
        {
            UpdateCursor(null);
        }
    }

    private void StartClipDrag(bool alt)
    {
        EditorSession session = Session!;
        Clip clip = _pressHit.Clip!;
        if (_pressHit.Row is { } row && row.Track.Locked)
        {
            _mode = PointerMode.None;
            return;
        }

        DragOperation operation = _pressHit.Kind switch
        {
            HitKind.ClipStart => alt ? DragOperation.StretchStart : DragOperation.TrimStart,
            HitKind.ClipEnd => alt ? DragOperation.StretchEnd : DragOperation.TrimEnd,
            _ => alt ? DragOperation.Slip : DragOperation.Move,
        };
        var drag = new ClipDrag(session.Project, session.SequenceId, clip.Id, operation, _pressTime, Options(), session.Position);
        _drag = new ClipDragSession(session, drag);
        _drag.Begin();
        _mode = PointerMode.DraggingClip;
    }

    private void ContinueClipDrag(Point p, TrackLayout layout)
    {
        if (_drag is null)
        {
            return;
        }

        DragPlan plan = _drag.Move(Viewport.XToTime(p.X), layout.RowAt(ContentY(p.Y)));
        bool showGhost = !plan.IsLive || plan.InsertAt is not null;
        _overlay.Ghost = showGhost ? (plan.GhostTrackId, plan.GhostStart, plan.GhostEnd) : null;
        _overlay.InsertLine = plan.InsertAt is { } at ? (plan.GhostTrackId, at) : null;
        _overlay.SnapGuide = plan.SnapGuide;
        _overlay.Balloon = plan.Balloon is { } balloon ? (plan.GhostTrackId, balloon.At, balloon.Text) : null;
        Invalidate();
    }

    private void FinishClipDrag()
    {
        ClipDragSession? drag = _drag;
        _drag = null;
        ClearDragOverlay();
        drag?.Complete();
        Invalidate();
    }

    private void FinishMarquee(Point p, TrackLayout layout)
    {
        EditorSession session = Session!;
        _overlay.Marquee = null;
        if (Distance(p, _pressPoint) < DragThreshold)
        {
            if (_pressHit.Clip is null)
            {
                _overlay.TimeRange = null;
                session.Seek(_pressTime.SnapToFrame(session.Sequence.FrameRate));
            }

            Invalidate();
            return;
        }

        double x0 = Viewport.TimeToX(_pressTime);
        List<string> ids = TimelineHitTest.Marquee(layout, Viewport, x0, ContentY(_pressPoint.Y), p.X, ContentY(p.Y));
        session.Select(_pressCtrl ? session.SelectedClipIds.Concat(ids) : ids);
        Time a = _pressTime.SnapToFrame(session.Sequence.FrameRate);
        Time b = Viewport.XToTime(p.X).SnapToFrame(session.Sequence.FrameRate);
        _overlay.TimeRange = a == b ? null : (Time.Min(a, b), Time.Max(a, b));
        Invalidate();
    }

    /// <summary>Al arrastrar cerca de un borde, la vista se corre sola.</summary>
    private void EdgeScroll(double x)
    {
        double step = x < EdgeScrollZone ? x - EdgeScrollZone : x > Viewport.Width - EdgeScrollZone ? x - (Viewport.Width - EdgeScrollZone) : 0;
        if (step != 0)
        {
            Viewport.ScrollX += step * 0.5;
            Viewport.ClampScroll(Session!.Sequence.Duration + Viewport.PixelsToDuration(Viewport.Width));
        }
    }

    private void UpdateCursor(TimelineHit? hit, TransitionHitKind transition = TransitionHitKind.None)
    {
        InputCursor cursor = State.Tool == TimelineTool.Zoom ? CrossCursor
            : hit is { } h && EditsEnvelope(h) ? CrossCursor
            : transition is TransitionHitKind.StartHandle or TransitionHitKind.EndHandle ? ResizeCursor
            : transition is TransitionHitKind.Body or TransitionHitKind.AutoCrossfade ? ArrowCursor
            : hit?.Kind is HitKind.ClipStart or HitKind.ClipEnd && State.Tool != TimelineTool.Selection ? ResizeCursor
            : hit?.Kind is HitKind.ClipBody && State.Tool == TimelineTool.Normal ? MoveCursor
            : ArrowCursor;
        if (!ReferenceEquals(ProtectedCursor, cursor))
        {
            ProtectedCursor = cursor;
        }
    }

    private static double Distance(Point a, Point b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void ZoomAt(double anchor, double factor)
    {
        _zoomAnchor = anchor;
        try
        {
            State.PixelsPerSecond = Viewport.PixelsPerSecond * factor;
        }
        finally
        {
            _zoomAnchor = null;
        }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_renderer is null || Session is null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(LanesHost);
        int delta = point.Properties.MouseWheelDelta;
        VirtualKeyModifiers keys = e.KeyModifiers;
        e.Handled = true;
        if (point.Properties.IsHorizontalMouseWheel)
        {
            Viewport.ScrollX += delta;
        }
        else if (keys.HasFlag(VirtualKeyModifiers.Control))
        {
            double anchor = ReferenceEquals(sender, HeadersCanvas) ? Viewport.Width / 2 : Math.Clamp(point.Position.X, 0, Viewport.Width);
            ZoomAt(anchor, Math.Pow(WheelStep, delta / 120.0));
            return;
        }
        else if (keys.HasFlag(VirtualKeyModifiers.Shift) || ReferenceEquals(sender, HeadersCanvas))
        {
            SetScrollY(_renderer.ScrollY - (delta * 0.5));
            return;
        }
        else
        {
            Viewport.ScrollX -= delta;
        }

        Viewport.ClampScroll(Session.Sequence.Duration);
        Invalidate();
    }

    private void OnRulerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Session is null || _renderer is null || _mode != PointerMode.None)
        {
            return;
        }

        TakeKeyboardFocus(FocusState.Pointer);
        PointerPoint point = e.GetCurrentPoint(RulerCanvas);
        double x = point.Position.X;
        e.Handled = true;
        Marker? marker = Session.Sequence.Markers
            .Where(m => Math.Abs(Viewport.TimeToX(m.Time) + 3 - x) <= 6)
            .MinBy(m => Math.Abs(Viewport.TimeToX(m.Time) - x));
        if (marker is not null && point.Properties.IsRightButtonPressed)
        {
            Session.Execute(Edits.RemoveMarker(Session.SequenceId, marker.Id));
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (marker is not null && point.Position.Y < 14)
        {
            _markerId = marker.Id;
            _mode = PointerMode.DraggingMarker;
            Session.BeginGesture("Mover marcador");
        }
        else
        {
            _mode = PointerMode.Scrub;
            Session.Seek(Viewport.XToTime(x));
        }

        Capture(RulerCanvas, e);
    }

    private void OnRulerPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Session is null || _renderer is null)
        {
            return;
        }

        double x = e.GetCurrentPoint(RulerCanvas).Position.X;
        if (_mode == PointerMode.Scrub)
        {
            EdgeScroll(x);
            Session.Seek(Viewport.XToTime(x));
            Invalidate();
        }
        else if (_mode == PointerMode.DraggingMarker && _markerId is { } id)
        {
            Time at = Viewport.XToTime(x).SnapToFrame(Session.Sequence.FrameRate);
            if (State.Snapping)
            {
                SnapPoints snaps = SnapPoints.Collect(Session.Sequence, new HashSet<string>(), Session.Position);
                at = snaps.Nearest(at, Viewport.PixelsToDuration(SnapPoints.ThresholdPixels)) ?? at;
            }

            if (Session.Sequence.Markers.Find(m => m.Id == id) is { } marker && marker.Time != at)
            {
                Session.Execute(Edits.MoveMarker(Session.SequenceId, id, at));
            }
        }
    }

    private void OnRulerPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndRulerGesture();
        ReleaseCapture();
        e.Handled = true;
    }

    private void OnRulerPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndRulerGesture();
        _captured = null;
        _captureOwner = null;
    }

    private void EndRulerGesture()
    {
        if (_mode == PointerMode.DraggingMarker && Session!.IsInGesture)
        {
            Session.EndGesture();
        }

        if (_mode is PointerMode.DraggingMarker or PointerMode.Scrub)
        {
            _mode = PointerMode.None;
            _markerId = null;
        }
    }

    private async void OnLanesDragEnter(object sender, DragEventArgs e)
    {
        _dropMediaId = null;
        if (!e.DataView.Contains(MediaIdFormat))
        {
            if (CarriesElement(e.DataView))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                ReadDroppedTransition(e);
            }

            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            _dropMediaId = (await e.DataView.GetDataAsync(MediaIdFormat))?.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _dropMediaId = null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnLanesDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(MediaIdFormat))
        {
            if (CarriesElement(e.DataView))
            {
                DragOverTransition(e);
            }

            return;
        }

        e.Handled = true;
        DropPlan? plan = PlanDrop(e);
        e.AcceptedOperation = plan is null && _dropMediaId is not null ? DataPackageOperation.None : DataPackageOperation.Copy;
        if (plan is not null)
        {
            e.DragUIOverride.Caption = plan.Mode == PlacementMode.Insert ? "Insertar" : "Sobrescribir";
            _overlay.Ghost = (plan.TrackId, plan.At, plan.At + plan.Duration);
            _overlay.LinkedGhost = plan.LinkedTrackId is { } linked ? (linked, plan.At, plan.At + plan.Duration) : null;
            _overlay.InsertLine = plan.TrackId == Options().MagneticTrackId ? (plan.TrackId, plan.At) : null;
        }
        else
        {
            ClearDragOverlay();
        }

        Invalidate();
    }

    private void OnLanesDragLeave(object sender, DragEventArgs e)
    {
        ClearDragOverlay();
        Invalidate();
    }

    private async void OnLanesDrop(object sender, DragEventArgs e)
    {
        if (Session is null)
        {
            return;
        }

        if (!e.DataView.Contains(MediaIdFormat))
        {
            if (CarriesElement(e.DataView))
            {
                DropTransition(e);
            }

            return;
        }

        e.Handled = true;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            _dropMediaId ??= (await e.DataView.GetDataAsync(MediaIdFormat))?.ToString();
            if (PlanDrop(e) is { } plan)
            {
                SequenceEditCommand command = plan.Build();
                if (Session.Execute(command) && command.CreatedIds.Count > 0)
                {
                    Session.Select([command.CreatedIds[0]]);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Session.Notify(NotificationSeverity.Warning, "No se pudo soltar", ex.Message);
        }
        finally
        {
            _dropMediaId = null;
            ClearDragOverlay();
            Invalidate();
            deferral.Complete();
        }
    }

    private DropPlan? PlanDrop(DragEventArgs e)
    {
        if (_dropMediaId is not { } id || Session!.Project.FindMedia(id) is not { } media || _renderer?.Layout is not { } layout)
        {
            return null;
        }

        Point p = e.GetPosition(LanesHost);
        bool insert = e.Modifiers.HasFlag(DragDropModifiers.Control);
        return TimelinePlanning.PlanDrop(Session.Project, Session.Sequence, media, layout.RowAt(ContentY(p.Y)), Viewport.XToTime(p.X), insert, Options(), Session.Position);
    }
}
