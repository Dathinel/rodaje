using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Player;

/// <summary>Reproductor: el <see cref="SwapChainPanel"/> que llena el hilo de render, los medidores y el transporte.</summary>
internal sealed partial class PlayerView : UserControl, IEditorView
{
    private const string PlayGlyphText = "";
    private const string PauseGlyphText = "";

    private PlayerEngine? _engine;
    private ShortcutRouter? _shortcuts;
    private string? _error;

    public PlayerView()
    {
        InitializeComponent();
    }

    public EditorSession? Session { get; private set; }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
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

        VideoPanel.SizeChanged += (_, _) => PushPanelSize();
        VideoPanel.CompositionScaleChanged += (_, _) => PushPanelSize();
        Session.TransportChanged += (_, _) => UpdateStatus();
        Session.ProjectOpened += (_, _) => UpdateFormat();
        Session.ProjectEdited += (_, _) => UpdateFormat();
        services.ShortcutRegistry.Changed += (_, _) => UpdateToolTips();
        UpdateFormat();
        UpdateToolTips();
        Bindings.Update();
    }

    public static string PlayGlyph(bool isPlaying) => isPlaying ? PauseGlyphText : PlayGlyphText;

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

    private void UpdateStatus()
    {
        if (_error is not null || Session is null)
        {
            return;
        }

        PlaybackSnapshot transport = Session.Transport;
        StatusText.Text = transport.IsPlaying || Session.Sequence.Duration.Ticks > 0 ? "" : "Importa un video y haz doble clic en él para empezar.";
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
        FormatText.Text = string.Create(CultureInfo.InvariantCulture,
            $"{sequence.Width / divisor}:{sequence.Height / divisor} · {sequence.Width}×{sequence.Height} · {fps:0.###} fps");
        UpdateStatus();
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
    }

    private void SetToolTip(Button button, string label, string actionId)
    {
        string keys = _shortcuts!.Registry.GetKeyText(actionId);
        ToolTipService.SetToolTip(button, keys.Length == 0 ? label : $"{label} ({keys})");
    }

    private void ShowError(string message)
    {
        _error = message;
        StatusText.Text = "Error: " + message;
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
