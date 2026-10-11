using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services;
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
}

/// <summary>
/// Reproductor: cabecera con el formato, el zoom y los avisos (lanzadera, proxy), el <see cref="SwapChainPanel"/> que
/// llena el hilo de render con los márgenes seguros encima, los medidores y el transporte.
/// </summary>
internal sealed partial class PlayerView : UserControl, IEditorView
{
    private const string PlayGlyphText = "";
    private const string PauseGlyphText = "";

    // Márgenes seguros de EBU R 95: acción al 93 % y gráficos (títulos) al 90 % de la imagen.
    private const double ActionSafeFraction = 0.93;
    private const double TitleSafeFraction = 0.90;

    private readonly List<IDisposable> _bindings = [];
    private EditorServices? _services;
    private PlayerEngine? _engine;
    private ShortcutRouter? _shortcuts;
    private string? _error;
    private double _zoom;
    private bool _usesProxy;
    private int _shuttleRate;

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
        VideoPanel.SizeChanged += (_, _) => OnPanelSizeChanged();
        VideoPanel.CompositionScaleChanged += (_, _) => OnPanelSizeChanged();
        Session.TransportChanged += (_, _) => UpdateTransport();
        Session.ProjectOpened += (_, _) => UpdateFormat();
        Session.ProjectEdited += (_, _) => UpdateFormat();
        services.ShortcutRegistry.Changed += (_, _) => UpdateToolTips();
        SafeAreasButton.IsChecked = services.Settings.Current.ShowSafeAreas;
        UpdateSafeAreas();
        Meters.Attach(services);
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
        TransportRow.Height = new GridLength(fullScreen ? 0 : 44);
        var margin = new Thickness(fullScreen ? 0 : 8);
        VideoPanel.Margin = margin;
        SafeAreaCanvas.Margin = margin;
        _services.Shell.SetPlayerFullScreen(fullScreen);
    }

    private void RegisterActions(EditorServices services)
    {
        ShortcutRegistry registry = services.ShortcutRegistry;
        registry.Register(new ShortcutAction(PlayerActionIds.ShuttleReverse, "Lanzadera hacia atrás (más rápido con cada pulsación)", ShortcutGroups.Playback, ["J"]));
        registry.Register(new ShortcutAction(PlayerActionIds.ShuttleStop, "Lanzadera: detener", ShortcutGroups.Playback, ["K"]));
        registry.Register(new ShortcutAction(PlayerActionIds.ShuttleForward, "Lanzadera hacia adelante (más rápido con cada pulsación)", ShortcutGroups.Playback, ["L"]));
        registry.Register(new ShortcutAction(PlayerActionIds.FullScreen, "Reproductor en pantalla completa", ShortcutGroups.Windows, ["F"]));
        registry.Register(new ShortcutAction(PlayerActionIds.SafeAreas, "Mostrar los márgenes seguros", ShortcutGroups.Windows, []));

        ShortcutRouter router = services.Shortcuts;
        _bindings.Add(router.Bind(PlayerActionIds.ShuttleReverse, () => _engine!.ShuttleKey(ShuttleKey.Reverse)));
        _bindings.Add(router.Bind(PlayerActionIds.ShuttleStop, () => _engine!.ShuttleKey(ShuttleKey.Stop)));
        _bindings.Add(router.Bind(PlayerActionIds.ShuttleForward, () => _engine!.ShuttleKey(ShuttleKey.Forward)));
        _bindings.Add(router.Bind(PlayerActionIds.FullScreen, () => SetFullScreen(!IsFullScreen)));
        _bindings.Add(router.Bind(PlayerActionIds.SafeAreas, () =>
        {
            SafeAreasButton.IsChecked = SafeAreasButton.IsChecked != true;
            OnSafeAreasChanged();
        }));
    }

    private void OnPanelSizeChanged()
    {
        PushPanelSize();
        UpdateSafeAreas();
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
            StatusText.Text = transport.IsPlaying || Session.Sequence.Duration.Ticks > 0 ? "" : "Importa un video y haz doble clic en él para empezar.";
        }

        if (transport.UsesProxy != _usesProxy)
        {
            _usesProxy = transport.UsesProxy;
            ProxyBadge.Visibility = _usesProxy ? Visibility.Visible : Visibility.Collapsed;
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

        Sequence sequence = Session.Sequence;
        int divisor = Gcd(sequence.Width, sequence.Height);
        double fps = sequence.FrameRate.Num / (double)sequence.FrameRate.Den;
        AspectText.Text = string.Create(CultureInfo.InvariantCulture, $"{sequence.Width / divisor}:{sequence.Height / divisor}");
        // La app corre con globalización invariante: la coma decimal se pone a mano.
        string rate = fps.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');
        ToolTipService.SetToolTip(AspectText, string.Create(CultureInfo.InvariantCulture, $"{sequence.Width} × {sequence.Height} · {rate} fps"));
        UpdateTransport();
        UpdateSafeAreas();
    }

    /// <summary>Dibuja los márgenes seguros sobre el rectángulo donde el render pone la imagen, con el zoom actual.</summary>
    private void UpdateSafeAreas()
    {
        bool show = SafeAreasButton.IsChecked == true && Session is not null;
        SafeAreaCanvas.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        Sequence sequence = Session!.Sequence;
        double width = VideoPanel.ActualWidth;
        double height = VideoPanel.ActualHeight;
        if (width <= 0 || height <= 0 || sequence.Width <= 0 || sequence.Height <= 0)
        {
            return;
        }

        double imageWidth;
        double imageHeight;
        if (_zoom > 0)
        {
            // El render pone un píxel de la secuencia por píxel físico multiplicado por el zoom.
            imageWidth = sequence.Width * _zoom / Math.Max(0.01, VideoPanel.CompositionScaleX);
            imageHeight = sequence.Height * _zoom / Math.Max(0.01, VideoPanel.CompositionScaleY);
        }
        else
        {
            double scale = Math.Min(width / sequence.Width, height / sequence.Height);
            imageWidth = sequence.Width * scale;
            imageHeight = sequence.Height * scale;
        }

        double left = (width - imageWidth) / 2;
        double top = (height - imageHeight) / 2;
        PlaceSafe(ActionSafe, left, top, imageWidth, imageHeight, ActionSafeFraction);
        PlaceSafe(TitleSafe, left, top, imageWidth, imageHeight, TitleSafeFraction);
        double centerX = left + (imageWidth / 2);
        double centerY = top + (imageHeight / 2);
        Canvas.SetLeft(CenterH, centerX - (CenterH.Width / 2));
        Canvas.SetTop(CenterH, centerY);
        Canvas.SetLeft(CenterV, centerX);
        Canvas.SetTop(CenterV, centerY - (CenterV.Height / 2));
    }

    private static void PlaceSafe(FrameworkElement rectangle, double left, double top, double width, double height, double fraction)
    {
        rectangle.Width = width * fraction;
        rectangle.Height = height * fraction;
        Canvas.SetLeft(rectangle, left + (width * (1 - fraction) / 2));
        Canvas.SetTop(rectangle, top + (height * (1 - fraction) / 2));
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
        SetToolTip(SafeAreasButton, "Márgenes seguros de acción (93 %) y de títulos (90 %)", PlayerActionIds.SafeAreas);
    }

    private void SetToolTip(FrameworkElement element, string label, string actionId)
    {
        string keys = _shortcuts!.Registry.GetKeyText(actionId);
        ToolTipService.SetToolTip(element, keys.Length == 0 ? label : $"{label} ({keys})");
    }

    private void ShowError(string message)
    {
        _error = message;
        StatusText.Text = "Error: " + message;
    }

    private void OnSafeAreasChanged()
    {
        if (_services is not null)
        {
            _services.Settings.Current.ShowSafeAreas = SafeAreasButton.IsChecked == true;
        }

        UpdateSafeAreas();
    }

    private void OnZoomClick(object sender, RoutedEventArgs e)
    {
        (_zoom, string label) = ReferenceEquals(sender, ZoomHalfItem) ? (0.5, "50 %") : ReferenceEquals(sender, ZoomFullItem) ? (1.0, "100 %") : (0.0, "Ajustar");
        ZoomButton.Content = label;
        _engine?.SetZoom(_zoom);
        UpdateSafeAreas();
    }

    private void OnSafeAreasClick(object sender, RoutedEventArgs e) => OnSafeAreasChanged();

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => _shortcuts?.Invoke(PlayerActionIds.FullScreen);

    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        SetFullScreen(!IsFullScreen);
    }

    private void OnPlay(object sender, RoutedEventArgs e) => _shortcuts?.Invoke(ActionIds.TogglePlay);

    private void OnPreviousFrame(object sender, RoutedEventArgs e) => _shortcuts?.Invoke(ActionIds.PreviousFrame);

    private void OnNextFrame(object sender, RoutedEventArgs e) => _shortcuts?.Invoke(ActionIds.NextFrame);

    private static int Gcd(int a, int b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return Math.Max(1, a);
    }
}
