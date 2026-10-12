using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Media;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;

namespace Rodaje.App.Views.Timeline;

/// <summary>Acciones que registra la línea de tiempo (además de las del armazón).</summary>
internal static class TimelineActionIds
{
    public const string AutoCrossfade = "timeline.autoCrossfade";
    public const string LockEnvelopes = "timeline.lockEnvelopes";
    public const string IgnoreGroups = "timeline.ignoreGroups";
    public const string CreateCompound = "edit.compound";
    public const string DecomposeCompound = "edit.decompose";
    public const string LeaveCompound = "timeline.leaveCompound";
}

/// <summary>
/// La línea de tiempo: barra de herramientas, regla, cabeceras y pistas. Lee el modelo de la sesión y lo cambia solo con
/// comandos; los arrastres son gestos (un paso del historial) con pasos en vivo que el reproductor ve enseguida.
/// </summary>
internal sealed partial class TimelineView : UserControl, IEditorView
{
    /// <summary>Formato del arrastre desde la biblioteca: el texto es el identificador del medio.</summary>
    public const string MediaIdFormat = "rodaje/media-id";

    private readonly TimelineOverlay _overlay = new();
    private readonly HashSet<string> _selection = new(StringComparer.Ordinal);
    private TimelineRenderer? _renderer;
    private TrackHeaders? _headers;
    private EditorServices? _services;
    private IClipVisuals? _visuals;
    private bool _renderQueued;
    private bool _syncingBars;
    private bool _syncingZoom;
    private double? _zoomAnchor;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomSettle;
    private bool _zoomAnimating;
    private long _zoomAnimationStart;
    private ViewFrame _zoomFrom;
    private ViewFrame _zoomTo;
    private ViewFrame _zoomLast;

    public TimelineView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public EditorSession? Session { get; private set; }

    /// <summary>Miniaturas y formas de onda; sin proveedor se dibujan marcadores de posición.</summary>
    public IClipVisuals? ClipVisuals
    {
        get => _visuals;
        set
        {
            if (ReferenceEquals(value, _visuals))
            {
                return;
            }

            if (_visuals is not null)
            {
                _visuals.Changed -= OnVisualsChanged;
            }

            _visuals = value;
            if (_visuals is not null)
            {
                _visuals.Changed += OnVisualsChanged;
            }

            Invalidate(content: true);
        }
    }

    internal TimelineRenderer? Renderer => _renderer;

    internal EditorServices? Services => _services;

    /// <summary>Rearmar en cada paso del zoom en vez de estirar lo dibujado (para medir).</summary>
    internal bool DisableZoomPreview { get; set; }

    private TimelineState State => Session!.Timeline;

    private TimelineViewport Viewport => _renderer!.Viewport;

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Session = services.Session;
        var brushes = new TimelineBrushes();
        _renderer = new TimelineRenderer(brushes, RowsCanvas, ClipsCanvas, OverlayCanvas, RulerCanvas, PlayheadLayer);
        _headers = new TrackHeaders(HeadersCanvas, brushes, (Style)Application.Current.Resources["ToolToggleButtonStyle"]);
        _headers.Toggled += OnTrackToggled;
        _headers.GainChanging += OnTrackGainChanging;
        _headers.GainCommitted += OnTrackGainCommitted;
        _headers.SubtitleTrackRequested += () => Session?.AddSubtitleTrack();
        _headers.SubtitleRoleRequested += (trackId, subtitles) => Session?.SetSubtitleRole(trackId, subtitles);
        _headers.VoiceOverRequested += trackId => Voice.VoiceCommands.RecordVoiceOver(services, trackId);
        _headers.SubtitleFileRequested += trackId =>
        {
            if (trackId is null)
            {
                services.Shortcuts.Invoke(Subtitles.SubtitleFileCommands.ImportId);
            }
            else
            {
                Subtitles.SubtitleFileCommands.ExportTrack(services, trackId);
            }
        };
        _headers.DynamicsRequested += trackId => services.Session.ShowDynamics(new Rodaje.App.Services.Audio.DynamicsOwner(Rodaje.App.Services.Audio.DynamicsOwnerKind.Track, trackId));
        _headers.AudioPresetRequested += (trackId, preset) =>
        {
            if (Session is { } session)
            {
                session.Execute(Rodaje.App.Services.Audio.AudioPresetCommands.Apply(session.SequenceId, trackId, preset));
            }
        };

        services.Settings.Current.Timeline.ApplyTo(State);
        Viewport.PixelsPerSecond = State.PixelsPerSecond;

        RegisterActions(services);
        Views.Shortcuts.MouseShortcutHost.Attach(this, MouseContext.Timeline, services);
        services.ShortcutRegistry.Changed += (_, _) => UpdateTooltips();
        Session.ProjectOpened += (_, _) => OnProjectOpened();
        Session.ProjectEdited += OnProjectEdited;
        Session.SelectionChanged += (_, _) => OnSelectionChanged();
        Session.TransportChanged += (_, _) => OnTransportChanged();
        State.PropertyChanged += OnStateChanged;

        LanesHost.SizeChanged += (_, _) => OnLanesResized();
        RulerCanvas.SizeChanged += (_, _) => ClipToBounds(RulerCanvas);
        HeadersCanvas.SizeChanged += (_, _) => ClipToBounds(HeadersCanvas);
        PlayheadLayer.SizeChanged += (_, _) => ClipToBounds(PlayheadLayer);

        foreach (ButtonBase button in ToolButtons().Select(b => (ButtonBase)b.Button)
            .Concat(ToggleButtons().Select(b => (ButtonBase)b.Button))
            .Concat(ActionButtons().Select(b => (ButtonBase)b.Button)))
        {
            KeepOffFocus(button);
        }

        UpdateToolbar();
        UpdateTooltips();
        SyncZoomSlider();
        RebuildLayout();
        Bindings.Update();

        if (TimelineBenchmark.RequestedReportPath is { } report)
        {
            _ = TimelineBenchmark.RunAsync(this, report);
        }
    }

    /// <summary>
    /// Los botones de la barra y de las cabeceras no se quedan el foco: si lo tuvieran, Espacio los pulsaría y, al
    /// rearmar las cabeceras, el foco saltaría al menú Archivo.
    /// </summary>
    internal static void KeepOffFocus(Control control)
    {
        control.AllowFocusOnInteraction = false;
        control.IsTabStop = false;
    }

    /// <summary>El foco de teclado pasa a la línea de tiempo (sin marco visible), así Espacio y los atajos llegan a la ventana.</summary>
    internal void TakeKeyboardFocus(FocusState state = FocusState.Programmatic)
    {
        if (FocusState == FocusState.Unfocused)
        {
            Focus(state);
        }
    }

    /// <summary>Pide un dibujo para el próximo cuadro (varios pedidos en el mismo cuadro se juntan).</summary>
    internal void Invalidate(bool content = false)
    {
        if (content)
        {
            _renderer?.InvalidateContent();
        }

        if (!_renderQueued && _renderer is not null)
        {
            _renderQueued = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    /// <summary>Dibuja ya, sin esperar al cuadro (lo usa la medida).</summary>
    internal void RenderNow()
    {
        if (_renderer is null || Session is null)
        {
            return;
        }

        UpdateScrollBars();
        _renderer.Render(Scene(), _overlay);
    }

    internal void ScrollTo(double scrollX, double? pixelsPerSecond = null, double? anchor = null)
    {
        if (pixelsPerSecond is { } pps)
        {
            BeginZoomPreview();
            Viewport.ZoomAround(anchor ?? (Viewport.Width / 2), pps);
            _syncingZoom = true;
            State.PixelsPerSecond = Viewport.PixelsPerSecond;
            _syncingZoom = false;
            SyncZoomSlider();
        }
        else
        {
            Viewport.ScrollX = scrollX;
        }

        Viewport.ClampScroll(Session!.Sequence.Duration);
        Invalidate();
    }

    private void OnRendering(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _renderQueued = false;
        RenderNow();
    }

    private TimelineScene Scene()
    {
        EditorSession session = Session!;
        _selection.Clear();
        _selection.UnionWith(session.SelectedClipIds);
        return new TimelineScene
        {
            Project = session.Project,
            Sequence = session.Sequence,
            MainTrack = session.MainTrack,
            Selection = _selection,
            Position = session.ProgramPosition,
            ShowEnvelopes = State.Tool == TimelineTool.Envelope,
            AutoCrossfade = State.AutoCrossfade,
            SelectedTransitionId = session.SelectedTransitionId,
            Visuals = _visuals,
        };
    }

    private void RebuildLayout()
    {
        EditorSession session = Session!;
        TrackLayout layout = TrackLayout.Build(session.Sequence, session.MainTrack);
        _renderer!.Layout = layout;
        _headers!.Update(layout, State.MagneticMainTrack, HeadersCanvas.ActualWidth > 0 ? HeadersCanvas.ActualWidth : 172);
        _headers.RefreshGains(track => EnvelopeEdits.TrackGainDb(track, session.ProgramPosition), layout);
        int clips = session.Sequence.AllTracks.Sum(t => t.Clips.Count);
        SummaryText.Text = string.Create(CultureInfo.InvariantCulture, $"{clips} clip{(clips == 1 ? "" : "s")} · {Timecode.Format(session.Sequence.Duration)}");
        Invalidate(content: true);
    }

    private void OnProjectOpened()
    {
        AbortPointerGesture();
        _overlay.TimeRange = null;
        Viewport.ScrollX = 0;
        _renderer!.ScrollY = 0;
        _headers!.ScrollY = 0;
        RebuildLayout();
        RefreshContextBar();
    }

    private void OnProjectEdited(object? sender, ProjectEditedEventArgs e)
    {
        if (e.Kind == EditKind.SequenceChanged)
        {
            OnProjectOpened();
            return;
        }

        if (e.Kind == EditKind.GestureCancelled)
        {
            AbortPointerGesture();
        }

        RebuildLayout();
        RefreshContextBar();
    }

    private void OnSelectionChanged() => Invalidate(content: true);

    private void OnVisualsChanged(object? sender, ClipVisualsChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() => Invalidate(content: true));

    private void OnTransportChanged()
    {
        if (_renderer is null || Session is null)
        {
            return;
        }

        Time position = Session.ProgramPosition;
        if (Session.IsPlaying && _mode == PointerMode.None)
        {
            double x = Viewport.TimeToX(position);
            if (x > Viewport.Width - 24 || x < 0)
            {
                Viewport.ScrollX += x - 48;
                Viewport.ClampScroll(Session.Sequence.Duration);
                Invalidate();
                return;
            }
        }

        _renderer.RenderPlayhead(position);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        _services!.Settings.Current.Timeline.CopyFrom(State);
        switch (e.PropertyName)
        {
            case nameof(TimelineState.PixelsPerSecond):
                if (!_syncingZoom)
                {
                    BeginZoomPreview();
                    double anchor = _zoomAnchor ?? PlayheadAnchor();
                    Viewport.ZoomAround(anchor, State.PixelsPerSecond);
                    Viewport.ClampScroll(Session!.Sequence.Duration);
                    SyncZoomSlider();
                    Invalidate();
                }

                break;
            case nameof(TimelineState.Tool):
                UpdateToolbar();
                UpdateCursor(null);
                Invalidate(content: true);
                break;
            case nameof(TimelineState.MagneticMainTrack):
                UpdateToolbar();
                RebuildLayout();
                break;
            default:
                UpdateToolbar();
                Invalidate(content: true);
                break;
        }
    }

    /// <summary>
    /// Un paso de zoom continuo: mientras sigan llegando, lo dibujado se estira (un cambio de transformación) y 150 ms
    /// después del último se rearma nítido con el zoom final.
    /// </summary>
    private void BeginZoomPreview()
    {
        if (DisableZoomPreview || _renderer is null)
        {
            return;
        }

        _renderer.ZoomPreview = true;
        if (_zoomSettle is null)
        {
            _zoomSettle = DispatcherQueue.CreateTimer();
            _zoomSettle.Interval = TimeSpan.FromMilliseconds(150);
            _zoomSettle.IsRepeating = false;
            _zoomSettle.Tick += (_, _) =>
            {
                _renderer.ZoomPreview = false;
                SyncZoomSlider();
                Invalidate();
            };
        }

        _zoomSettle.Stop();
        _zoomSettle.Start();
    }

    /// <summary>Al hacer zoom con el teclado o la barra, queda quieto el cursor si se ve; si no, el centro.</summary>
    private double PlayheadAnchor()
    {
        double x = Viewport.TimeToX(Session!.Position);
        return x >= 0 && x <= Viewport.Width ? x : Viewport.Width / 2;
    }

    private void OnLanesResized()
    {
        ClipToBounds(LanesHost);
        Viewport.Width = LanesHost.ActualWidth;
        _renderer!.LanesHeight = LanesHost.ActualHeight;
        Viewport.ClampScroll(Session!.Sequence.Duration);
        Invalidate();
    }

    private static void ClipToBounds(FrameworkElement element) =>
        element.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight) };

    private void UpdateScrollBars()
    {
        TimelineRenderer renderer = _renderer!;
        _syncingBars = true;
        double content = Viewport.ContentWidth(Session!.Sequence.Duration);
        HorizontalBar.Maximum = Math.Max(0, content - Viewport.Width);
        HorizontalBar.ViewportSize = Math.Max(1, Viewport.Width);
        HorizontalBar.LargeChange = Math.Max(1, Viewport.Width * 0.8);
        HorizontalBar.Value = Math.Min(Viewport.ScrollX, HorizontalBar.Maximum);

        double height = renderer.Layout?.TotalHeight ?? 0;
        double maxY = Math.Max(0, height - renderer.LanesHeight);
        if (renderer.ScrollY > maxY)
        {
            renderer.ScrollY = maxY;
            _headers!.ScrollY = maxY;
        }

        VerticalBar.Maximum = maxY;
        VerticalBar.ViewportSize = Math.Max(1, renderer.LanesHeight);
        VerticalBar.LargeChange = Math.Max(1, renderer.LanesHeight * 0.8);
        VerticalBar.Value = renderer.ScrollY;
        VerticalBar.Visibility = maxY > 0 ? Visibility.Visible : Visibility.Collapsed;
        _syncingBars = false;
    }

    private void OnHorizontalBarChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_syncingBars && _renderer is not null)
        {
            Viewport.ScrollX = e.NewValue;
            Invalidate();
        }
    }

    private void OnVerticalBarChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_syncingBars && _renderer is not null)
        {
            SetScrollY(e.NewValue);
        }
    }

    private void SetScrollY(double value)
    {
        TimelineRenderer renderer = _renderer!;
        double max = Math.Max(0, (renderer.Layout?.TotalHeight ?? 0) - renderer.LanesHeight);
        renderer.ScrollY = Math.Clamp(value, 0, max);
        _headers!.ScrollY = renderer.ScrollY;
        Invalidate();
    }

    private void SyncZoomSlider()
    {
        if (_renderer?.ZoomPreview == true)
        {
            // Mover el deslizador en cada paso del zoom cuesta un pase de diseño de la barra; se pone al terminar.
            return;
        }

        double range = Math.Log(TimelineState.MaxPixelsPerSecond / TimelineState.MinPixelsPerSecond);
        _syncingZoom = true;
        ZoomSlider.Value = Math.Round(1000 * Math.Log(State.PixelsPerSecond / TimelineState.MinPixelsPerSecond) / range);
        _syncingZoom = false;
    }

    private void OnZoomSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingZoom || Session is null)
        {
            return;
        }

        double range = Math.Log(TimelineState.MaxPixelsPerSecond / TimelineState.MinPixelsPerSecond);
        State.PixelsPerSecond = TimelineState.MinPixelsPerSecond * Math.Exp(e.NewValue / 1000 * range);
    }

    private void RegisterActions(EditorServices services)
    {
        ShortcutRegistry registry = services.ShortcutRegistry;
        registry.Register(new ShortcutAction(TimelineActionIds.AutoCrossfade, "Fundido cruzado automático", ShortcutGroups.Timeline, []) { ShortLabel = "Fundido auto", Description = "Fundido cruzado en cada corte entre clips pegados de la pista principal." });
        registry.Register(new ShortcutAction(TimelineActionIds.LockEnvelopes, "Bloquear envolventes", ShortcutGroups.Timeline, []) { ShortLabel = "Bloq. envolv.", Description = "Las envolventes viajan con su clip y no se editan por accidente." });
        registry.Register(new ShortcutAction(TimelineActionIds.IgnoreGroups, "Ignorar grupos", ShortcutGroups.Timeline, []) { ShortLabel = "Sin grupos", Description = "Mover y recortar solo el clip agarrado, sin su audio o video enlazado." });

        ShortcutRouter router = services.Shortcuts;
        router.Bind(TimelineActionIds.AutoCrossfade, () => State.AutoCrossfade = !State.AutoCrossfade);
        router.Bind(TimelineActionIds.LockEnvelopes, () => State.LockEnvelopes = !State.LockEnvelopes);
        router.Bind(TimelineActionIds.IgnoreGroups, () => State.IgnoreGroups = !State.IgnoreGroups);
        router.Bind(ActionIds.AddMarker, AddMarker);
        router.Bind(ActionIds.AddRegion, AddRegion);
        router.Bind(ActionIds.ZoomToFit, ZoomToFit);
        router.Bind(ActionIds.ZoomToSelection, ZoomToSelection, () => Session is not null && SelectionSpan() is not null);
        RegisterCompoundActions(services);
        RegisterThreePointActions(services);
    }

    private void AddMarker()
    {
        EditorSession session = Session!;
        string name = TimelinePlanning.NextName("Marcador", session.Sequence.Markers.Select(m => m.Name));
        session.Execute(Edits.AddMarker(session.SequenceId, session.EditPosition, name));
    }

    private void AddRegion()
    {
        EditorSession session = Session!;
        (Time start, Time end) = TimelinePlanning.RegionSpan(session.Sequence, session.SelectedClipIds, _overlay.TimeRange, session.MainTrack, session.EditPosition,
            (session.SequenceIn, session.SequenceOut));
        string name = TimelinePlanning.NextName("Región", session.Sequence.Regions.Select(r => r.Name));
        if (session.Execute(Edits.AddRegion(session.SequenceId, start, end, name)))
        {
            _overlay.TimeRange = null;
            Invalidate();
        }
    }

    /// <summary>«Ajustar a la ventana»: toda la secuencia visible, con una animación corta.</summary>
    private void ZoomToFit() => AnimateZoom(ZoomFit.Whole(Session!.Sequence.Duration, Viewport.Width));

    /// <summary>«Zoom a la selección»: los clips seleccionados (o el tramo marcado) llenan la vista.</summary>
    private void ZoomToSelection()
    {
        if (SelectionSpan() is { } span)
        {
            AnimateZoom(ZoomFit.Span(span.Start, span.End, Viewport.Width));
        }
    }

    private (Time Start, Time End)? SelectionSpan()
    {
        EditorSession session = Session!;
        return ZoomFit.SelectionSpan(session.Sequence, session.SelectedClipIds, _overlay.TimeRange);
    }

    /// <summary>
    /// Lleva la vista al encuadre en <see cref="ZoomAnimation.Duration"/>, un paso por cuadro; lo dibujado se estira
    /// mientras dura (como el zoom con la rueda) y se rearma nítido al terminar.
    /// </summary>
    private void AnimateZoom(ViewFrame target)
    {
        if (_renderer is null || Viewport.Width <= 0)
        {
            return;
        }

        _zoomFrom = new ViewFrame(Viewport.PixelsPerSecond, Viewport.ScrollX);
        _zoomTo = target;
        _zoomLast = _zoomFrom;
        _zoomAnimationStart = Stopwatch.GetTimestamp();
        if (!_zoomAnimating)
        {
            _zoomAnimating = true;
            CompositionTarget.Rendering += OnZoomAnimationFrame;
        }
    }

    private void OnZoomAnimationFrame(object? sender, object e)
    {
        // Si algo movió la vista desde el último paso (rueda, barra, el cursor al reproducir), la animación cede.
        if (Session is null || Viewport.PixelsPerSecond != _zoomLast.PixelsPerSecond || Viewport.ScrollX != _zoomLast.ScrollX)
        {
            StopZoomAnimation();
            return;
        }

        double progress = Stopwatch.GetElapsedTime(_zoomAnimationStart) / ZoomAnimation.Duration;
        ViewFrame frame = ZoomAnimation.At(_zoomFrom, _zoomTo, Viewport.Width, progress);
        BeginZoomPreview();
        Viewport.PixelsPerSecond = frame.PixelsPerSecond;
        Viewport.ScrollX = frame.ScrollX;
        Viewport.ClampScroll(Session.Sequence.Duration);
        _syncingZoom = true;
        State.PixelsPerSecond = Viewport.PixelsPerSecond;
        _syncingZoom = false;
        _zoomLast = new ViewFrame(Viewport.PixelsPerSecond, Viewport.ScrollX);
        Invalidate();
        if (progress >= 1)
        {
            StopZoomAnimation();
            SyncZoomSlider();
        }
    }

    /// <summary>Una animación de zoom a medias no sigue enganchada a <see cref="CompositionTarget.Rendering"/> con la vista fuera.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e) => StopZoomAnimation();

    private void StopZoomAnimation()
    {
        if (_zoomAnimating)
        {
            _zoomAnimating = false;
            CompositionTarget.Rendering -= OnZoomAnimationFrame;
        }
    }

    private void OnTrackToggled(string trackId, TrackToggle toggle)
    {
        EditorSession session = Session!;
        if (session.Sequence.FindTrack(trackId) is not { } track)
        {
            return;
        }

        session.Execute(toggle switch
        {
            TrackToggle.Hidden => Edits.SetTrackState(session.SequenceId, trackId, hidden: !track.Hidden),
            TrackToggle.Muted => Edits.SetTrackState(session.SequenceId, trackId, muted: !track.Muted),
            _ => Edits.SetTrackState(session.SequenceId, trackId, locked: !track.Locked),
        });

        // Si el comando no se pudo (por ejemplo, ocultar una pista bloqueada), el botón vuelve a su estado.
        RebuildLayout();
        TakeKeyboardFocus();
    }

    private void OnTrackGainChanging(string trackId, double gainDb)
    {
        EditorSession session = Session!;
        if (!session.IsInGesture)
        {
            session.BeginGesture("Volumen de la pista");
        }

        session.TryExecute(EnvelopeEdits.SetTrackGain(session.SequenceId, trackId, gainDb, session.EditPosition));
    }

    private void OnTrackGainCommitted(string trackId)
    {
        if (Session!.IsInGesture)
        {
            Session.EndGesture();
        }

        TakeKeyboardFocus();
    }

    private (ToggleButton Button, string ActionId)[] ToolButtons() =>
    [
        (NormalToolButton, ActionIds.NormalTool),
        (EnvelopeToolButton, ActionIds.EnvelopeTool),
        (SelectionToolButton, ActionIds.SelectionTool),
        (ZoomToolButton, ActionIds.ZoomTool),
        (RollToolButton, ActionIds.RollTool),
        (SlideToolButton, ActionIds.SlideTool),
    ];

    private (ToggleButton Button, string ActionId)[] ToggleButtons() =>
    [
        (MagneticButton, ActionIds.ToggleMagneticMain),
        (SnappingButton, ActionIds.ToggleSnapping),
        (AutoRippleButton, ActionIds.ToggleAutoRipple),
        (CrossfadeButton, TimelineActionIds.AutoCrossfade),
        (LockEnvelopesButton, TimelineActionIds.LockEnvelopes),
        (IgnoreGroupsButton, TimelineActionIds.IgnoreGroups),
    ];

    private (Button Button, string ActionId)[] ActionButtons() =>
    [
        (SplitButton, ActionIds.Split),
        (TrimStartButton, ActionIds.TrimStart),
        (TrimEndButton, ActionIds.TrimEnd),
        (DeleteButton, ActionIds.Delete),
        (MarkerButton, ActionIds.AddMarker),
        (RegionButton, ActionIds.AddRegion),
        (UndoButton, ActionIds.Undo),
        (RedoButton, ActionIds.Redo),
        (ZoomOutButton, ActionIds.ZoomOut),
        (ZoomInButton, ActionIds.ZoomIn),
        (ZoomFitButton, ActionIds.ZoomToFit),
        (ZoomSelectionButton, ActionIds.ZoomToSelection),
    ];

    private void OnToolClick(object sender, RoutedEventArgs e) => InvokeFor(sender, ToolButtons());

    private void OnToggleClick(object sender, RoutedEventArgs e) => InvokeFor(sender, ToggleButtons());

    private void OnActionClick(object sender, RoutedEventArgs e) => InvokeFor(sender, ActionButtons());

    private void InvokeFor<T>(object sender, (T Button, string ActionId)[] map)
        where T : class
    {
        foreach ((T button, string actionId) in map)
        {
            if (ReferenceEquals(button, sender))
            {
                _services!.Shortcuts.Invoke(actionId);
                break;
            }
        }

        UpdateToolbar();
        TakeKeyboardFocus();
    }

    private void OnRippleModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Session is not null && RippleModeBox.SelectedIndex >= 0)
        {
            State.RippleMode = (RippleMode)RippleModeBox.SelectedIndex;
        }
    }

    private void UpdateToolbar()
    {
        if (Session is null)
        {
            return;
        }

        TimelineState state = State;
        NormalToolButton.IsChecked = state.Tool == TimelineTool.Normal;
        EnvelopeToolButton.IsChecked = state.Tool == TimelineTool.Envelope;
        SelectionToolButton.IsChecked = state.Tool == TimelineTool.Selection;
        ZoomToolButton.IsChecked = state.Tool == TimelineTool.Zoom;
        RollToolButton.IsChecked = state.Tool == TimelineTool.Roll;
        SlideToolButton.IsChecked = state.Tool == TimelineTool.Slide;
        MagneticButton.IsChecked = state.MagneticMainTrack;
        SnappingButton.IsChecked = state.Snapping;
        AutoRippleButton.IsChecked = state.AutoRipple;
        CrossfadeButton.IsChecked = state.AutoCrossfade;
        LockEnvelopesButton.IsChecked = state.LockEnvelopes;
        IgnoreGroupsButton.IsChecked = state.IgnoreGroups;
        RippleModeBox.SelectedIndex = (int)state.RippleMode;
    }

    private void UpdateTooltips()
    {
        ShortcutRegistry registry = _services!.ShortcutRegistry;
        foreach ((ToggleButton button, string id) in ToolButtons().Concat(ToggleButtons()))
        {
            ToolTipService.SetToolTip(button, Tip(registry, id));
        }

        foreach ((Button button, string id) in ActionButtons())
        {
            ToolTipService.SetToolTip(button, Tip(registry, id));
        }

        ToolTipService.SetToolTip(RippleModeBox, "Qué se corre con el ripple: solo las pistas de lo que se edita, todas, o también marcadores y regiones");
        ToolTipService.SetToolTip(ZoomSlider, "Zoom (Ctrl+rueda sobre las pistas)");
    }

    private static string Tip(ShortcutRegistry registry, string actionId)
    {
        string label = registry.Find(actionId)?.Label ?? actionId;
        string key = registry.GetKeyText(actionId);
        return key.Length == 0 ? label : $"{label} ({key})";
    }
}
