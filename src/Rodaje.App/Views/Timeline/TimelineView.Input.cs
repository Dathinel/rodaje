using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Shortcuts;
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
    private const double TrackHeightStep = 8;

    private static readonly InputCursor ArrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private static readonly InputCursor ResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputCursor MoveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private static readonly InputCursor CrossCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);

    private const string RollBadge = "]|[";
    private const string SlideBadge = "[↔]";
    private const string BladeBadge = "✂";
    private const string RippleBadge = "]»";
    private const string SlipBadge = "«»";
    private const string RangeBadge = "[ ]";
    private static readonly InputCursor IBeamCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);

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
    private Point _edgePoint;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _edgeTimer;

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

        /// <summary>La herramienta de rango marca la entrada y la salida de la secuencia.</summary>
        Range,
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
        if (Session is { } session)
        {
            session.CurrentTrimPreview = null;
        }

        _renderer?.PlaceCursorBadge(null, 0, 0);
        ClearDragOverlay();
        _overlay.Marquee = null;
        ReleaseCapture();
        Invalidate();
    }

    private void ClearDragOverlay()
    {
        _overlay.Ghost = null;
        _overlay.LinkedGhost = null;
        _overlay.ExtraGhosts = null;
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
        AutoRipple = State.AutoRipple || State.Tool == TimelineTool.Ripple,
        RippleScope = State.RippleScope,
        IgnoreGroups = State.IgnoreGroups,
        PixelsPerSecond = Viewport.PixelsPerSecond,
        LinkedAudio = Session!.LinkedAudioTracks,
    };

    private double ContentY(double y) => y + _renderer!.ScrollY;

    private void OnLanesPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Session is null || _renderer?.Layout is not { } layout || _mode != PointerMode.None)
        {
            return;
        }

        TakeKeyboardFocus(FocusState.Pointer);
        _headers?.Select(null);
        PointerPoint point = e.GetCurrentPoint(LanesHost);
        PointerPointProperties props = point.Properties;
        Point p = point.Position;
        bool ctrl = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        e.Handled = true;

        if (Views.Shortcuts.MouseShortcutHost.PressedButton(props) is { } button)
        {
            // El central y los laterales hacen lo que diga el ratón de la línea de tiempo; las acciones de teclado las lanza MouseShortcutHost.
            if (Views.Shortcuts.MouseShortcutHost.Resolve(_services!.ShortcutRegistry, MouseContext.Timeline, button, e.KeyModifiers) != MouseGestures.Pan)
            {
                return;
            }

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

            if (hit.Clip is not null)
            {
                ShowClipMenu(p);
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

        Session.SelectedEdge = null;
        if (State.Tool == TimelineTool.Blade)
        {
            BladeAt(hit, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
            return;
        }

        if (State.Tool == TimelineTool.Range)
        {
            _pressPoint = p;
            _pressTime = hit.Time;
            _pressHit = hit;
            _mode = PointerMode.Range;
            Capture(LanesHost, e);
            return;
        }

        if (IsCompoundDoubleClick(hit.Clip))
        {
            Session.EnterCompound(hit.Clip!.Id);
            return;
        }

        _pressPoint = p;
        _pressTime = hit.Time;
        _pressHit = hit;
        _pressCtrl = ctrl;
        if (hit.Clip is { } clip)
        {
            // Ctrl+Alt es el gesto de roll o slide, no Ctrl+clic.
            if (ctrl && !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu))
            {
                Session.ToggleSelection(clip.Id);
            }
            else if (!Session.SelectedClipIds.Contains(clip.Id))
            {
                // Un clip agrupado se elige con todo su grupo.
                Session.Select(State.IgnoreGroups || clip.GroupId is null
                    ? [clip.Id]
                    : TimelineEdits.ExpandWithGroups(Session.Sequence, [clip.Id]).Select(m => m.Clip.Id));
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
                TimelineHit hover = TimelineHitTest.Hit(layout, Viewport, p.X, ContentY(p.Y));
                UpdateCursor(hover, HitTransition(layout, p).Kind);
                _renderer.PlaceCursorBadge(Badge(hover, e.KeyModifiers), p.X, p.Y);
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
                    StartClipDrag(e.KeyModifiers);
                    ContinueClipDrag(p, layout);
                }

                return;
            case PointerMode.DraggingClip:
                FollowEdges(p);
                ContinueClipDrag(p, layout);
                if (_drag?.Drag.Operation is DragOperation.Roll or DragOperation.Slide)
                {
                    _renderer.PlaceCursorBadge(_drag.Drag.Operation == DragOperation.Roll ? RollBadge : SlideBadge, p.X, p.Y);
                }

                return;
            case PointerMode.DraggingTransition:
                ContinueTransitionDrag(p);
                return;
            case PointerMode.DraggingEnvelope:
                ContinueEnvelopeDrag(p, layout);
                return;
            case PointerMode.Range:
                EdgeScroll(p.X);
                _overlay.TimeRange = RangeSpan(p);
                Invalidate();
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
            case PointerMode.Range:
                FinishRange(p);
                break;
            case PointerMode.PendingClip:
                SelectEdge(_pressHit);
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
            _renderer?.PlaceCursorBadge(null, 0, 0);
        }
    }

    private void StartClipDrag(VirtualKeyModifiers modifiers)
    {
        EditorSession session = Session!;
        Clip clip = _pressHit.Clip!;
        if (_pressHit.Row is { } row && row.Track.Locked)
        {
            _mode = PointerMode.None;
            return;
        }

        bool alt = modifiers.HasFlag(VirtualKeyModifiers.Menu) || State.Tool == TimelineTool.Slip;
        (DragOperation operation, string clipId) = State.Tool == TimelineTool.Slip ? (DragOperation.Slip, clip.Id) : TrimGesture(_pressHit, modifiers) switch
        {
            DragOperation.Roll => RollTarget(_pressHit, _pressTime),
            DragOperation.Slide => (DragOperation.Slide, clip.Id),
            _ => (_pressHit.Kind switch
            {
                HitKind.ClipStart => alt ? DragOperation.StretchStart : DragOperation.TrimStart,
                HitKind.ClipEnd => alt ? DragOperation.StretchEnd : DragOperation.TrimEnd,
                _ => alt ? DragOperation.Slip : DragOperation.Move,
            }, clip.Id),
        };
        var drag = new ClipDrag(session.Project, session.SequenceId, clipId, operation, _pressTime, Options(), session.ProgramPosition, selection: session.SelectedClipIds);
        _drag = new ClipDragSession(session, drag);
        _drag.Begin();
        _mode = PointerMode.DraggingClip;
    }

    /// <summary>
    /// Roll o slide, si el arrastre lo es: con su herramienta, o con Ctrl+Alt en la normal (sobre un borde, roll; sobre
    /// el cuerpo, slide). <c>null</c> en los demás casos.
    /// </summary>
    private DragOperation? TrimGesture(TimelineHit hit, VirtualKeyModifiers modifiers)
    {
        if (hit.Clip is null)
        {
            return null;
        }

        bool chord = State.Tool == TimelineTool.Normal && modifiers.HasFlag(VirtualKeyModifiers.Control) && modifiers.HasFlag(VirtualKeyModifiers.Menu);
        return State.Tool == TimelineTool.Roll || (chord && hit.Kind is HitKind.ClipStart or HitKind.ClipEnd) ? DragOperation.Roll
            : State.Tool == TimelineTool.Slide || (chord && hit.Kind == HitKind.ClipBody) ? DragOperation.Slide
            : null;
    }

    private string? Badge(TimelineHit hit, VirtualKeyModifiers modifiers) => State.Tool switch
    {
        TimelineTool.Blade => BladeBadge,
        TimelineTool.Range => RangeBadge,
        TimelineTool.Slip when hit.Clip is not null => SlipBadge,
        TimelineTool.Ripple when hit.Kind is HitKind.ClipStart or HitKind.ClipEnd => RippleBadge,
        _ => TrimGesture(hit, modifiers) switch
        {
            DragOperation.Roll => RollBadge,
            DragOperation.Slide => SlideBadge,
            _ => null,
        },
    };

    /// <summary>
    /// Un clic sin arrastrar en un borde lo elige para recortarlo con «,» y «.» (con la herramienta de ripple, recorte
    /// ripple); en cualquier otro lugar, la elección se quita.
    /// </summary>
    private void SelectEdge(TimelineHit hit)
    {
        EditorSession session = Session!;
        bool edgeTool = State.Tool is TimelineTool.Normal or TimelineTool.Ripple or TimelineTool.Roll;
        session.SelectedEdge = edgeTool && hit.Clip is { } clip && hit.Kind is HitKind.ClipStart or HitKind.ClipEnd
            ? new SelectedEdge(clip.Id, hit.Kind == HitKind.ClipStart ? TrimEdge.In : TrimEdge.Out, State.Tool == TimelineTool.Ripple)
            : null;
    }

    private (Time Start, Time End)? RangeSpan(Point p)
    {
        Rational rate = Session!.Sequence.FrameRate;
        Time a = _pressTime.SnapToFrame(rate);
        Time b = Viewport.XToTime(p.X).SnapToFrame(rate);
        if (a > b)
        {
            (a, b) = (b, a);
        }

        return b > a ? (Time.Max(Time.Zero, a), b) : null;
    }

    /// <summary>Herramienta de rango: el tramo arrastrado pasa a ser la entrada y la salida de la secuencia; un clic las quita.</summary>
    private void FinishRange(Point p)
    {
        EditorSession session = Session!;
        if (RangeSpan(p) is { } span && Distance(p, _pressPoint) >= DragThreshold)
        {
            session.SetSequenceRange(span.Start, span.End);
            _overlay.TimeRange = span;
        }
        else
        {
            session.SetSequenceRange(Time.Zero, Time.Zero);
            _overlay.TimeRange = null;
            SeekProgram(_pressTime.SnapToFrame(session.Sequence.FrameRate));
        }

        Invalidate();
    }

    /// <summary>
    /// El corte que mueve un roll: el de salida del clip, o el de entrada si se agarró el borde de entrada o la mitad
    /// izquierda (ese corte es la salida del clip anterior pegado). Sin anterior pegado, el borde de entrada se recorta.
    /// </summary>
    private static (DragOperation Operation, string ClipId) RollTarget(TimelineHit hit, Time at)
    {
        Clip clip = hit.Clip!;
        bool entry = hit.Kind == HitKind.ClipStart || (hit.Kind == HitKind.ClipBody && at < clip.Start + Time.FromTicks(clip.Duration.Ticks / 2));
        if (!entry)
        {
            return (DragOperation.Roll, clip.Id);
        }

        Track? track = hit.Row?.Track;
        int index = track?.Clips.IndexOf(clip) ?? -1;
        return index > 0 && track!.Clips[index - 1].End == clip.Start
            ? (DragOperation.Roll, track.Clips[index - 1].Id)
            : (DragOperation.TrimStart, clip.Id);
    }

    private void ContinueClipDrag(Point p, TrackLayout layout)
    {
        if (_drag is null)
        {
            return;
        }

        DragPlan plan = _drag.Move(Viewport.XToTime(p.X), layout.RowAt(ContentY(p.Y)));
        bool showGhost = !plan.IsLive || plan.InsertAt is not null || plan.Ghosts is { Count: > 1 };
        _overlay.Ghost = showGhost ? (plan.GhostTrackId, plan.GhostStart, plan.GhostEnd) : null;
        _overlay.ExtraGhosts = showGhost && plan.Ghosts is { Count: > 1 }
            ? plan.Ghosts.Where(g => !(g.TrackId == plan.GhostTrackId && g.Start == plan.GhostStart && g.End == plan.GhostEnd)).ToList()
            : null;
        _overlay.InsertLine = plan.InsertAt is { } at ? (plan.GhostTrackId, at) : null;
        _overlay.SnapGuide = plan.SnapGuide;
        _overlay.Balloon = plan.Balloon is { } balloon ? (plan.GhostTrackId, balloon.At, balloon.Text) : null;
        if (_drag.Drag.Operation is DragOperation.Roll or DragOperation.Slide)
        {
            Session!.CurrentTrimPreview = TrimPreview.Of(Session.Sequence, _drag.Drag.ClipId, _drag.Drag.Operation == DragOperation.Roll);
        }

        Invalidate();
    }

    private void FinishClipDrag()
    {
        ClipDragSession? drag = _drag;
        _drag = null;
        Session!.CurrentTrimPreview = null;
        _renderer?.PlaceCursorBadge(null, 0, 0);
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
                SeekProgram(_pressTime.SnapToFrame(session.Sequence.FrameRate));
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

    /// <summary>Al arrastrar cerca del borde izquierdo o derecho, la vista se corre sola (un paso por movimiento).</summary>
    private void EdgeScroll(double x)
    {
        double step = AutoScroll.Step(x, Viewport.Width);
        if (step != 0)
        {
            Viewport.ScrollX += step;
            Viewport.ClampScroll(Session!.Sequence.Duration + Viewport.PixelsToDuration(Viewport.Width));
        }
    }

    /// <summary>
    /// Un clip arrastrado cerca de cualquier borde de las pistas corre la vista en ese sentido, también con el ratón
    /// quieto: un temporizador sigue mientras el puntero esté en la franja del borde.
    /// </summary>
    private void FollowEdges(Point p)
    {
        _edgePoint = p;
        if (ScrollTowardEdges(p) && _edgeTimer is not { IsRunning: true })
        {
            if (_edgeTimer is null)
            {
                _edgeTimer = DispatcherQueue.CreateTimer();
                _edgeTimer.Interval = TimeSpan.FromMilliseconds(16);
                _edgeTimer.Tick += (_, _) => OnEdgeTimer();
            }

            _edgeTimer.Start();
        }
    }

    private void OnEdgeTimer()
    {
        if (_mode != PointerMode.DraggingClip || Session is null || _renderer?.Layout is not { } layout || !ScrollTowardEdges(_edgePoint))
        {
            _edgeTimer?.Stop();
            return;
        }

        ContinueClipDrag(_edgePoint, layout);
        Invalidate();
    }

    /// <summary>Un paso de desplazamiento hacia el borde cercano a <paramref name="p"/>; <c>false</c> si no está cerca de ninguno.</summary>
    private bool ScrollTowardEdges(Point p)
    {
        TimelineRenderer renderer = _renderer!;
        double dx = AutoScroll.Step(p.X, Viewport.Width);
        double dy = AutoScroll.Step(p.Y, renderer.LanesHeight);
        if (dx != 0)
        {
            Viewport.ScrollX += dx;
            Viewport.ClampScroll(Session!.Sequence.Duration + Viewport.PixelsToDuration(Viewport.Width));
        }

        if (dy != 0)
        {
            SetScrollY(renderer.ScrollY + dy);
        }

        return dx != 0 || dy != 0;
    }

    private void UpdateCursor(TimelineHit? hit, TransitionHitKind transition = TransitionHitKind.None)
    {
        InputCursor cursor = State.Tool is TimelineTool.Zoom or TimelineTool.Blade ? CrossCursor
            : State.Tool == TimelineTool.Range ? IBeamCursor
            : hit?.Clip is not null && State.Tool == TimelineTool.Slip ? ResizeCursor
            : hit is { } h && EditsEnvelope(h) ? CrossCursor
            : transition is TransitionHitKind.StartHandle or TransitionHitKind.EndHandle ? ResizeCursor
            : transition is TransitionHitKind.Body or TransitionHitKind.AutoCrossfade ? ArrowCursor
            : hit?.Clip is not null && State.Tool == TimelineTool.Roll ? ResizeCursor
            : hit?.Clip is not null && State.Tool == TimelineTool.Slide ? MoveCursor
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
        bool tilt = point.Properties.IsHorizontalMouseWheel;
        e.Handled = true;

        // Lo que haga la rueda sale del ratón del estilo (Ajustes de atajos); el paso positivo es abajo o a la derecha.
        int signed = tilt ? delta : -delta;
        string? gesture = Views.Shortcuts.MouseShortcutHost.Resolve(
            _services!.ShortcutRegistry, MouseContext.Timeline, tilt ? MouseInput.Tilt : MouseInput.Wheel, e.KeyModifiers);
        bool overHeaders = ReferenceEquals(sender, HeadersViewport);
        bool allTracksVisible = (_renderer.Layout?.TotalHeight ?? 0) <= _renderer.LanesHeight;
        gesture = MouseGestures.EffectiveTimelineWheel(gesture, overHeaders, tilt, allTracksVisible);

        switch (gesture)
        {
            case MouseGestures.Zoom:
                double anchor = overHeaders ? Viewport.Width / 2 : Math.Clamp(point.Position.X, 0, Viewport.Width);
                ZoomAt(anchor, Math.Pow(WheelStep, -signed / 120.0));
                return;
            case MouseGestures.ScrollTracks:
                SetScrollY(_renderer.ScrollY + (signed * 0.5));
                return;
            case MouseGestures.TrackHeight:
                ResizeTrackAt(point.Position.Y, -signed);
                return;
            case MouseGestures.Frames:
                Views.Shortcuts.MouseShortcutHost.StepFrames(_services.Shortcuts, Views.Shortcuts.MouseShortcutHost.Steps(point.Properties));
                return;
            case MouseGestures.ScrollTime:
                Viewport.ScrollX += signed;
                break;
            default:
                return;
        }

        Viewport.ClampScroll(Session.Sequence.Duration);
        Invalidate();
    }

    /// <summary>Alt+rueda: la pista bajo el puntero crece o se achica 8 px por paso; los pasos seguidos son un solo deshacer.</summary>
    private void ResizeTrackAt(double y, int delta)
    {
        EditorSession session = Session!;
        if (_renderer!.Layout?.RowStrictlyAt(ContentY(y)) is not { } row)
        {
            return;
        }

        int height = (int)Math.Round(row.Height + (Math.Sign(delta) * TrackHeightStep));
        if (height != (int)row.Height)
        {
            session.Execute(Edits.SetTrackHeight(session.SequenceId, row.TrackId, Math.Clamp(height, Track.MinHeight, Track.MaxHeight)));
        }
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
            var menu = new MenuFlyout();
            var edit = new MenuFlyoutItem { Text = "Editar…" };
            edit.Click += (_, _) => MarkerContextRequested?.Invoke(this, marker.Id);
            var remove = new MenuFlyoutItem { Text = "Quitar" };
            remove.Click += (_, _) => Session.Execute(Edits.RemoveMarker(Session.SequenceId, marker.Id));
            menu.Items.Add(edit);
            menu.Items.Add(remove);
            menu.ShowAt(RulerCanvas, point.Position);
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
            SeekProgram(Viewport.XToTime(x));
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
            SeekProgram(Viewport.XToTime(x));
            Invalidate();
        }
        else if (_mode == PointerMode.DraggingMarker && _markerId is { } id)
        {
            Time at = Viewport.XToTime(x).SnapToFrame(Session.Sequence.FrameRate);
            if (State.Snapping)
            {
                SnapPoints snaps = SnapPoints.Collect(Session.Sequence, new HashSet<string>(), Session.ProgramPosition);
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

    // Sin async void: lo que no atrapan los casos esperados lo informa UiTask (informe y aviso). La parte hasta el primer
    // await (AcceptedOperation, GetDeferral) sigue corriendo dentro del evento.
    private void OnLanesDragEnter(object sender, DragEventArgs e) => UiTask.Run(OnLanesDragEnterAsync(e));

    private async Task OnLanesDragEnterAsync(DragEventArgs e)
    {
        _dropMediaId = null;
        if (!e.DataView.Contains(MediaIdFormat))
        {
            if (CarriesSound(e.DataView))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                await ReadDroppedSoundAsync(e);
            }
            else if (CarriesTitle(e.DataView))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                await ReadDroppedTitleAsync(e);
            }
            else if (CarriesElement(e.DataView))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                await ReadDroppedTransitionAsync(e);
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
            if (CarriesSound(e.DataView))
            {
                DragOverSound(e);
            }
            else if (CarriesTitle(e.DataView))
            {
                DragOverTitle(e);
            }
            else if (CarriesElement(e.DataView))
            {
                DragOverTransition(e);
            }

            return;
        }

        e.Handled = true;
        if (_renderer is not null && Session is not null)
        {
            ScrollTowardEdges(e.GetPosition(LanesHost));
        }

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

    private void OnLanesDrop(object sender, DragEventArgs e) => UiTask.Run(OnLanesDropAsync(e));

    private async Task OnLanesDropAsync(DragEventArgs e)
    {
        if (Session is null)
        {
            return;
        }

        if (!e.DataView.Contains(MediaIdFormat))
        {
            if (CarriesSound(e.DataView))
            {
                await DropSoundAsync(e);
            }
            else if (CarriesTitle(e.DataView))
            {
                await DropTitleAsync(e);
            }
            else if (CarriesElement(e.DataView))
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
        if (MediaDrop.Parse(_dropMediaId) is not { } drop || Session!.Project.FindMedia(drop.MediaId) is not { } media || _renderer?.Layout is not { } layout)
        {
            return null;
        }

        Point p = e.GetPosition(LanesHost);
        bool insert = e.Modifiers.HasFlag(DragDropModifiers.Control);
        return TimelinePlanning.PlanDrop(Session.Project, Session.Sequence, media, layout.RowAt(ContentY(p.Y)), Viewport.XToTime(p.X), insert, Options(), Session.ProgramPosition, range: drop.Range);
    }
}
