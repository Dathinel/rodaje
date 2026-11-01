using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.App.Views.Inspector;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Pantalla completa con controles que aparecen al mover el ratón (reproducir, tiempo y barra de posición), y «Visor en otra
/// pantalla»: una ventana sin bordes en el segundo monitor con la misma imagen que el visor.
/// </summary>
internal sealed partial class PlayerView
{
    public const string SecondScreenActionId = PlayerShortcuts.SecondScreen;

    private readonly OverlayAutoHide _overlay = new(OverlayAutoHide.DefaultDelay);
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private DispatcherQueueTimer? _overlayTimer;
    private bool _updatingSeek;
    private Window? _externalWindow;

    /// <summary>La ventana de la otra pantalla está abierta.</summary>
    public bool IsExternalOpen => _externalWindow is not null;

    private void AttachFullScreenControls(EditorServices services)
    {
        _overlayTimer = DispatcherQueue.CreateTimer();
        _overlayTimer.Interval = TimeSpan.FromMilliseconds(250);
        _overlayTimer.Tick += (_, _) => UpdateOverlay();
        ViewerArea.AddHandler(PointerMovedEvent, new PointerEventHandler((_, _) => OnOverlayPointerMoved()), handledEventsToo: true);
        FullScreenControls.PointerEntered += (_, _) => _overlay.Hovered = true;
        FullScreenControls.PointerExited += (_, _) => _overlay.Hovered = false;
        services.Session.TransportChanged += (_, _) =>
        {
            if (FullScreenControls.Visibility == Visibility.Visible)
            {
                ShowOverlayTransport();
            }
        };
        _bindings.Add(services.Shortcuts.Bind(SecondScreenActionId, ToggleExternal));
        _engine!.ExternalSwapChainCreated += swapChain => DispatcherQueue.RunLater(() =>
        {
            if (_externalWindow?.Content is Grid { Children: [SwapChainPanel panel, ..] })
            {
                SwapChainPanelNative.SetSwapChain(panel, swapChain);
            }
        });
    }

    private void OnFullScreenChanged(bool fullScreen)
    {
        _overlay.Hovered = false;
        if (fullScreen)
        {
            _overlay.Moved(_clock.Elapsed);
            _overlayTimer?.Start();
        }
        else
        {
            _overlay.Hide();
            _overlayTimer?.Stop();
        }

        UpdateOverlay();
    }

    private void OnOverlayPointerMoved()
    {
        if (IsFullScreen)
        {
            _overlay.Moved(_clock.Elapsed);
            UpdateOverlay();
        }
    }

    private void UpdateOverlay()
    {
        bool visible = IsFullScreen && _overlay.IsVisible(_clock.Elapsed);
        Visibility wanted = visible ? Visibility.Visible : Visibility.Collapsed;
        if (FullScreenControls.Visibility != wanted)
        {
            FullScreenControls.Visibility = wanted;
            if (visible)
            {
                ShowOverlayTransport();
            }
        }
    }

    private void ShowOverlayTransport()
    {
        if (Session is not { } session)
        {
            return;
        }

        OverlayPlayButton.Content = PlayGlyph(session.IsPlaying);
        OverlayPosition.Text = session.PositionText;
        OverlayDuration.Text = session.DurationText;
        _updatingSeek = true;
        OverlaySeek.Value = OverlayAutoHide.Fraction(session.Position.ToSeconds(), session.Duration.ToSeconds());
        _updatingSeek = false;
    }

    private void OnOverlaySeek(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || Session is not { } session || session.Duration.Ticks <= 0)
        {
            return;
        }

        session.Seek(Time.FromSeconds(e.NewValue * session.Duration.ToSeconds()));
        _overlay.Moved(_clock.Elapsed);
    }

    private void ToggleExternal()
    {
        if (_externalWindow is { } open)
        {
            open.Close();
            return;
        }

        if (XamlRoot is null || _engine is null)
        {
            return;
        }

        AppWindow main = AppWindow.GetFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        IReadOnlyList<DisplayArea> displays = DisplayArea.FindAll();
        var screens = new List<ScreenRect>();
        for (int i = 0; i < displays.Count; i++)
        {
            Windows.Graphics.RectInt32 bounds = displays[i].OuterBounds;
            screens.Add(new ScreenRect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        }

        int index = SecondScreen.Pick(screens, new ScreenRect(main.Position.X, main.Position.Y, main.Size.Width, main.Size.Height));
        if (index < 0)
        {
            Session?.Notify(NotificationSeverity.Informational, "Visor en otra pantalla",
                "Solo hay una pantalla conectada. Conecta otra (o pasa esta ventana a la principal) y vuelve a intentarlo.");
            return;
        }

        OpenExternal(screens[index]);
    }

    private void OpenExternal(ScreenRect screen)
    {
        var panel = new SwapChainPanel();
        var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Children = { panel } };
        var window = new Window { Title = "Rodaje · Visor", Content = root };
        _externalWindow = window;
        PlayerEngine engine = _engine!;
        void Push()
        {
            float sx = panel.CompositionScaleX > 0 ? panel.CompositionScaleX : 1;
            float sy = panel.CompositionScaleY > 0 ? panel.CompositionScaleY : 1;
            if (panel.ActualWidth > 0 && panel.ActualHeight > 0)
            {
                engine.RequestExternalResize((int)Math.Round(panel.ActualWidth * sx), (int)Math.Round(panel.ActualHeight * sy), sx, sy);
            }
        }

        panel.SizeChanged += (_, _) => Push();
        panel.CompositionScaleChanged += (_, _) => Push();
        root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                window.Close();
            }
        };
        root.DoubleTapped += (_, _) => window.Close();
        window.Closed += (_, _) =>
        {
            engine.CloseExternal();
            _externalWindow = null;
        };

        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(screen.X, screen.Y, screen.Width, screen.Height));
        window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        window.Activate();
        Session?.Notify(NotificationSeverity.Informational, "Visor en otra pantalla",
            "La otra pantalla muestra la imagen en vivo. Se cierra con Esc o doble clic sobre ella, o desde Ver.");
    }

    /// <summary>Al cerrar la ventana del editor se cierra también la de la otra pantalla.</summary>
    public void CloseExternal() => _externalWindow?.Close();
}
