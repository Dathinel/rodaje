using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Player;

/// <summary>Identificadores de las acciones del reproductor; se guardan en <c>atajos.json</c>, no se renombran.</summary>
internal static class PlayerActionIds
{
    public const string ShuttleReverse = ActionIds.ShuttleReverse;
    public const string ShuttleStop = ActionIds.ShuttleStop;
    public const string ShuttleForward = ActionIds.ShuttleForward;
    public const string FullScreen = "view.playerFullScreen";
    public const string SafeAreas = "view.safeAreas";
    public const string ProjectFormat = "project.format";
}

/// <summary>
/// Reproductor: cabecera con el formato, el zoom y los avisos (lanzadera, proxy), el <see cref="SwapChainPanel"/> que
/// llena el hilo de render con los márgenes seguros encima, los medidores y el transporte.
/// </summary>
internal sealed partial class PlayerView : UserControl, IEditorView
{
    private const string PlayGlyphText = "";
    private const string PauseGlyphText = "";

    private readonly List<IDisposable> _bindings = [];
    private EditorServices? _services;
    private PlayerEngine? _engine;
    private ShortcutRouter? _shortcuts;
    private string? _error;
    private double _zoom;
    private bool _usesProxy;
    private int _shuttleRate;
    private int _previewDivisor = 1;
    private bool _hintForSelfTest;

    public PlayerView()
    {
        InitializeComponent();
    }

    public EditorSession? Session { get; private set; }

    public bool IsFullScreen { get; private set; }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Session = services.Session;
        _shortcuts = services.Shortcuts;
        _engine = services.Player;
        DispatcherQueue dispatcher = DispatcherQueue;
        _engine.SwapChainCreated += swapChain => dispatcher.TryEnqueue(() =>
        {
            SwapChainPanelNative.SetSwapChain(VideoPanel, swapChain);
            Program.Metrics.Milestone("cadena_en_panel");
        });
        _engine.Failed += message => dispatcher.TryEnqueue(() => ShowError(message));
        _engine.Warning += message => dispatcher.TryEnqueue(() =>
            Session.Notify(NotificationSeverity.Warning, "Reproductor", message));
        if (_engine.FailureMessage is { } early)
        {
            ShowError(early);
        }

        RegisterActions(services);
        AttachTrimPreview(services);
        AttachFullScreenControls(services);
        AttachFormat(services);
        Shortcuts.MouseShortcutHost.Attach(ViewerArea, MouseContext.Viewer, services, wheel: true,
            axis: (action, _, steps) => action == MouseGestures.ViewerZoom && StepZoom(steps));
        VideoPanel.SizeChanged += (_, _) => OnPanelSizeChanged();
        VideoPanel.CompositionScaleChanged += (_, _) => OnPanelSizeChanged();
        Session.TransportChanged += (_, _) => UpdateTransport();
        Session.ProjectOpened += (_, _) => UpdateFormat();
        Session.ProjectEdited += (_, _) => UpdateFormat();
        services.ShortcutRegistry.Changed += (_, _) => UpdateToolTips();
        services.PreviewQualityChanged += (_, _) => UpdateQuality();
        _engine.SetPreviewQuality(services.Settings.Current.PreviewQuality);
        UpdateQuality();
        AttachSafeZones();
        UpdateSafeAreas();
        AttachTitleHandles();
        AttachMaskHandles();
        AttachTrackHandles();
        AttachImageTools();
        Meters.Attach(services);
        AttachScopes(services);
        UpdateFormat();
        UpdateToolTips();
        Bindings.Update();
    }

    public static string PlayGlyph(bool isPlaying) => isPlaying ? PauseGlyphText : PlayGlyphText;

    /// <summary>Solo la imagen en toda la pantalla, sin cabecera, medidores ni transporte. La ventana oculta lo demás.</summary>
    public void SetFullScreen(bool fullScreen)
    {
        if (fullScreen == IsFullScreen || _services is null)
        {
            return;
        }

        IsFullScreen = fullScreen;
        Visibility chrome = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        Header.Visibility = chrome;
        Transport.Visibility = chrome;
        Meters.Visibility = chrome;
        Scopes.Visibility = fullScreen || ScopesButton.IsChecked != true ? Visibility.Collapsed : Visibility.Visible;
        TransportRow.Height = new GridLength(fullScreen ? 0 : 44);
        Stage.Margin = new Thickness(fullScreen ? 0 : 8);
        _services.Shell.SetPlayerFullScreen(fullScreen);
        OnFullScreenChanged(fullScreen);
    }

    private void RegisterActions(EditorServices services)
    {
        ShortcutRegistry registry = services.ShortcutRegistry;
        registry.Register(new ShortcutAction(PlayerActionIds.ShuttleReverse, "Lanzadera hacia atrás (más rápido con cada pulsación)", ShortcutGroups.Playback, ["J"]));
        registry.Register(new ShortcutAction(PlayerActionIds.ShuttleStop, "Lanzadera: detener", ShortcutGroups.Playback, ["K"]));
        registry.Register(new ShortcutAction(PlayerActionIds.ShuttleForward, "Lanzadera hacia adelante (más rápido con cada pulsación)", ShortcutGroups.Playback, ["L"]));
        registry.Register(new ShortcutAction(PlayerActionIds.FullScreen, "Reproductor en pantalla completa", ShortcutGroups.Windows, ["F"]));
        registry.Register(new ShortcutAction(PlayerActionIds.SafeAreas, "Mostrar u ocultar las zonas seguras", ShortcutGroups.Windows, []));

        ShortcutRouter router = services.Shortcuts;
        _bindings.Add(router.Bind(PlayerActionIds.ShuttleReverse, () => _engine!.ShuttleKey(ShuttleKey.Reverse)));
        _bindings.Add(router.Bind(PlayerActionIds.ShuttleStop, () => _engine!.ShuttleKey(ShuttleKey.Stop)));
        _bindings.Add(router.Bind(PlayerActionIds.ShuttleForward, () => _engine!.ShuttleKey(ShuttleKey.Forward)));
        _bindings.Add(router.Bind(PlayerActionIds.FullScreen, () => SetFullScreen(!IsFullScreen)));
        _bindings.Add(router.Bind(PlayerActionIds.SafeAreas, ToggleSafeZones));
    }

    private void OnPanelSizeChanged()
    {
        PushPanelSize();
        UpdateSafeAreas();
        UpdateTitleHandles();
        UpdateCropHandles();
        UpdateCompare();
    }

    private void PushPanelSize()
    {
        float sx = VideoPanel.CompositionScaleX;
        float sy = VideoPanel.CompositionScaleY;
        int width = (int)Math.Round(VideoPanel.ActualWidth * sx);
        int height = (int)Math.Round(VideoPanel.ActualHeight * sy);
        if (width > 0 && height > 0)
        {
            _engine?.RequestResize(width, height, sx, sy);
        }
    }

    private void UpdateTransport()
    {
        if (Session is null)
        {
            return;
        }

        PlaybackSnapshot transport = Session.Transport;
        if (_error is null)
        {
            bool empty = !transport.IsPlaying && Session.Sequence.Duration.Ticks == 0;
            SetStatus(empty || _hintForSelfTest ? "Importa un video y haz doble clic en él para empezar." : "");
        }

        if (transport.UsesProxy != _usesProxy)
        {
            _usesProxy = transport.UsesProxy;
            ProxyBadge.Visibility = _usesProxy ? Visibility.Visible : Visibility.Collapsed;
        }

        if (transport.PreviewDivisor != _previewDivisor)
        {
            _previewDivisor = transport.PreviewDivisor;
            UpdateQuality();
        }

        if (transport.ShuttleRate != _shuttleRate)
        {
            _shuttleRate = transport.ShuttleRate;
            string label = Shuttle.Label(_shuttleRate);
            ShuttleText.Text = label;
            ShuttleBadge.Visibility = label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateFormat()
    {
        if (Session is null)
        {
            return;
        }

        UpdateFormatButton();
        UpdateTransport();
        UpdateSafeAreas();
        UpdateTitleHandles();
    }

    private static void PlaceRect(FrameworkElement element, PanelRect rect)
    {
        element.Width = rect.Width;
        element.Height = rect.Height;
        Canvas.SetLeft(element, rect.Left);
        Canvas.SetTop(element, rect.Top);
    }

    private void UpdateToolTips()
    {
        if (_shortcuts is null)
        {
            return;
        }

        SetToolTip(PreviousFrameButton, "Cuadro anterior", ActionIds.PreviousFrame);
        SetToolTip(PlayButton, "Reproducir o pausar", ActionIds.TogglePlay);
        SetToolTip(NextFrameButton, "Cuadro siguiente", ActionIds.NextFrame);
        SetToolTip(FullScreenButton, "Pantalla completa (Esc o doble clic para salir)", PlayerActionIds.FullScreen);
        SetToolTip(SafeAreasButton, SafeZonesToolTip(), PlayerActionIds.SafeAreas);
    }

    private void SetToolTip(FrameworkElement element, string label, string actionId)
    {
        string keys = _shortcuts!.Registry.GetKeyText(actionId);
        ToolTipService.SetToolTip(element, keys.Length == 0 ? label : $"{label} ({keys})");
    }

    private void ShowError(string message)
    {
        _error = message;
        SetStatus("Error: " + message);
    }

    private void OnZoomClick(object sender, RoutedEventArgs e) =>
        SetZoom(ReferenceEquals(sender, ZoomHalfItem) ? ZoomHalfItem : ReferenceEquals(sender, ZoomFullItem) ? ZoomFullItem : ZoomFitItem);

    /// <summary>La rueda del ratón pasa entre Ajustar, 50 % y 100 % (abajo aleja).</summary>
    private bool StepZoom(int steps)
    {
        RadioMenuFlyoutItem[] order = [ZoomFitItem, ZoomHalfItem, ZoomFullItem];
        int at = Array.FindIndex(order, item => item.IsChecked);
        int next = Math.Clamp(at - Math.Sign(steps), 0, order.Length - 1);
        if (next != at)
        {
            SetZoom(order[next]);
        }

        return true;
    }

    private void SetZoom(RadioMenuFlyoutItem item)
    {
        item.IsChecked = true;
        (_zoom, string label) = ReferenceEquals(item, ZoomHalfItem) ? (0.5, "50 %") : ReferenceEquals(item, ZoomFullItem) ? (1.0, "100 %") : (0.0, "Ajustar");
        ZoomButton.Content = label;
        _engine?.SetZoom(_zoom);
        UpdateSafeAreas();
        UpdateTitleHandles();
    }

    private void OnQualityClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out PreviewQuality quality))
        {
            _services?.SetPreviewQuality(quality);
        }
    }

    /// <summary>La calidad elegida y, si Auto bajó la resolución, a cuánto («Auto · ½»).</summary>
    private void UpdateQuality()
    {
        if (_services is null)
        {
            return;
        }

        PreviewQuality quality = _services.Settings.Current.PreviewQuality;
        QualityAutoItem.IsChecked = quality == PreviewQuality.Auto;
        QualityFullItem.IsChecked = quality == PreviewQuality.Full;
        QualityHalfItem.IsChecked = quality == PreviewQuality.Half;
        QualityQuarterItem.IsChecked = quality == PreviewQuality.Quarter;
        QualityButton.Content = quality switch
        {
            PreviewQuality.Full => "Completa",
            PreviewQuality.Half => "Mitad",
            PreviewQuality.Quarter => "Cuarto",
            _ => _previewDivisor switch { 2 => "Auto · ½", 4 => "Auto · ¼", _ => "Auto" },
        };
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => _shortcuts?.Invoke(PlayerActionIds.FullScreen);

    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        // Con las máscaras, doble clic es de ellas; sobre un título, escribir; en el resto del visor, pantalla completa.
        if (!MaskDoubleTapped(e.GetPosition(VideoPanel)) && !TryEditTitleAt(e.GetPosition(VideoPanel)))
        {
            SetFullScreen(!IsFullScreen);
        }
    }

    private void OnPlay(object sender, RoutedEventArgs e) => _shortcuts?.Invoke(ActionIds.TogglePlay);

    private void OnPreviousFrame(object sender, RoutedEventArgs e) => _shortcuts?.Invoke(ActionIds.PreviousFrame);

    private void OnNextFrame(object sender, RoutedEventArgs e) => _shortcuts?.Invoke(ActionIds.NextFrame);
}
