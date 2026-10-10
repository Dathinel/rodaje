using System.Diagnostics;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Diagnostics;
using Rodaje.App.Player;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using TerraFX.Interop.Windows;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App;

// CA1001: el motor es de Program (using); la ventana lo detiene en Closed para leer las cifras finales.
#pragma warning disable CA1001
public sealed partial class MainWindow : Window
#pragma warning restore CA1001
{
    private readonly AppOptions _options;
    private readonly Metrics _metrics = Program.Metrics;
    private readonly PlayerEngine _engine;
    private readonly DispatcherQueue _dispatcher;
    private readonly HWND _hwnd;
    private bool _activatedOnce;
    private bool _closed;
    private long _inputSentAt;
    private int _exportClicks;
    private int _spaceToggles;
    private string _inputMode = "ninguno";
    private volatile bool _pinging;
    private PlayerSnapshot? _shown;
    private string? _fatal;
    private DispatcherQueueTimer? _uiTimer;

    internal MainWindow(AppOptions options, PlayerEngine engine)
    {
        _options = options;
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);
        State = new PlayerState();
        if (options.Files.Count > 0)
        {
            State.SetMedia(Path.GetFileName(options.Files[0]), Path.GetFileNameWithoutExtension(options.Files[0]));
        }

        SetUpTitleBar();
        uint dpi = GetDpiForWindow(_hwnd);
        AppWindow.Resize(new SizeInt32(MulDiv(1440, (int)dpi, 96), MulDiv(900, (int)dpi, 96)));

        // Atajos globales: se interceptan en el túnel (Preview) antes de que un botón con el foco se los quede.
        Root.PreviewKeyDown += OnPreviewKeyDown;
        Root.PreviewKeyUp += OnPreviewKeyUp;
        VideoPanel.SizeChanged += (_, _) => PushPanelSize();
        VideoPanel.CompositionScaleChanged += (_, _) => PushPanelSize();
        Activated += OnActivated;
        Closed += OnClosed;
        if (options.UiHz <= 0)
        {
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            _uiTimer = _dispatcher.CreateTimer();
            _uiTimer.Interval = TimeSpan.FromSeconds(1.0 / options.UiHz);
            _uiTimer.Tick += (_, _) => OnRendering(null, EventArgs.Empty);
            _uiTimer.Start();
        }

        _engine = engine;
        _engine.SwapChainCreated += swapChain => _dispatcher.TryEnqueue(() =>
        {
            SwapChainPanelNative.SetSwapChain(VideoPanel, swapChain);
            _metrics.Milestone("cadena_en_panel");
        });
        _engine.Failed += message => _dispatcher.TryEnqueue(() =>
        {
            _fatal = message;
            State.SetStatus("Error: " + message);
        });
        if (_engine.FailureMessage is { } early)
        {
            _fatal = early;
            State.SetStatus("Error: " + early);
        }
    }

    internal PlayerState State { get; }

    private void SetUpTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        AppWindowTitleBar bar = AppWindow.TitleBar;
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = ColorHelper.FromArgb(0xFF, 0xD8, 0xD8, 0xD8);
        bar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(0xFF, 0x23, 0x23, 0x23);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_activatedOnce)
        {
            return;
        }

        _activatedOnce = true;
        _metrics.Milestone("ventana_activada");
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        CaptionInset.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        if (_options.MeasureSeconds is { } seconds)
        {
            _ = MeasureAsync(seconds);
        }
    }

    private void PushPanelSize()
    {
        float sx = VideoPanel.CompositionScaleX;
        float sy = VideoPanel.CompositionScaleY;
        int width = (int)Math.Round(VideoPanel.ActualWidth * sx);
        int height = (int)Math.Round(VideoPanel.ActualHeight * sy);
        if (width > 0 && height > 0)
        {
            _engine.RequestResize(width, height, sx, sy);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || IsTextInputFocused())
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_inputSentAt != 0)
        {
            _metrics.InputToHandler(Stopwatch.GetElapsedTime(_inputSentAt, now).TotalMilliseconds);
            _inputSentAt = 0;
        }

        if (!e.KeyStatus.WasKeyDown)
        {
            _spaceToggles++;
            _engine.TogglePlayPause(now);
        }

        e.Handled = true;
    }

    // Button se activa con Espacio al soltar la tecla: también hay que quedarse con el KeyUp.
    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space && !IsTextInputFocused())
        {
            e.Handled = true;
        }
    }

    private bool IsTextInputFocused() =>
        Root.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox;

    private void OnPlayClicked() => _engine.TogglePlayPause(Stopwatch.GetTimestamp());

    private void OnExport() => _exportClicks++;

    private void OnTracksSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = Math.Max(100, e.NewSize.Width - 20);
        MainClip.Width = width;
        AudioClip.Width = width;
    }

    // Tiempo y cursor de la línea de tiempo: con --ui-hz 0 en cada cuadro de XAML (CompositionTarget.Rendering), si no
    // con un temporizador. Es la carga real del hilo de la interfaz mientras se reproduce.
    private void OnRendering(object? sender, object e)
    {
        PlayerSnapshot snapshot = _engine.Snapshot;
        if (ReferenceEquals(snapshot, _shown))
        {
            return;
        }

        _shown = snapshot;
        if (_fatal is null)
        {
            State.SetStatus(snapshot.IsPlaying ? "" : snapshot.Status);
        }

        State.SetPlaying(snapshot.IsPlaying);
        State.SetTimecode($"{PlayerEngine.FormatTime(snapshot.Position)} / {PlayerEngine.FormatTime(snapshot.Duration)}");
        double fraction = snapshot.Duration > Time.Zero ? snapshot.Position.ToSeconds() / snapshot.Duration.ToSeconds() : 0;
        PlayheadOffset.X = Math.Clamp(fraction, 0, 1) * MainClip.Width;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _pinging = false;
        CompositionTarget.Rendering -= OnRendering;
        _uiTimer?.Stop();
        _engine.Dispose();
        if (_options.MeasureSeconds is not null)
        {
            WriteReport();
        }
    }

    /// <summary>
    /// Recorrido de medida: espera el primer cuadro, deja 1,5 s de calentamiento, mide N s de reproducción limpia con un
    /// "ping" a la cola de la interfaz cada 50 ms; luego inyecta Espacio con SendInput (con el foco en EXPORTAR, para
    /// comprobar que el atajo no lo pulsa) y redimensiona la ventana tres veces.
    /// </summary>
    private async Task MeasureAsync(double seconds)
    {
        var deadline = Stopwatch.StartNew();
        while (!_metrics.HasMilestone("primer_cuadro") && _fatal is null && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(20);
        }

        if (!_metrics.HasMilestone("primer_cuadro"))
        {
            _metrics.Note("no llegó el primer cuadro en 30 s" + (_fatal is null ? "" : ": " + _fatal));
            Program.ExitCode = 1;
            Close();
            return;
        }

        await Task.Delay(1500);
        _metrics.ResetSteadyState();
        TimeSpan cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        _pinging = true;
        var pinger = new Thread(Ping) { IsBackground = true, Name = "Rodaje ping" };
        pinger.Start();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        _metrics.FreezeSteadyState();
        _pinging = false;
        using (Process self = Process.GetCurrentProcess())
        {
            double cpu = (self.TotalProcessorTime - cpuStart).TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100;
            _metrics.Note(string.Create(CultureInfo.InvariantCulture,
                $"CPU del proceso en reproducción: {cpu:0.0} % de un núcleo; memoria privada {self.PrivateMemorySize64 / 1048576.0:0} MB, conjunto de trabajo {self.WorkingSet64 / 1048576.0:0} MB"));
        }

        // Teclas reales por la cola de entrada de Windows: Espacio 6 veces (pausa, sigue, ...), con el foco en EXPORTAR.
        _ = SetForegroundWindow(_hwnd);
        ExportButton.Focus(FocusState.Keyboard);
        await Task.Delay(300);
        int togglesBefore = _spaceToggles;
        for (int i = 0; i < 6; i++)
        {
            SendSpace();
            await Task.Delay(400);
        }

        _metrics.Note($"Espacio inyectado 6 veces con el foco en EXPORTAR: {_spaceToggles - togglesBefore} cambios de reproducción, {_exportClicks} clics en EXPORTAR (debe ser 0); vía: {_inputMode}");

        // Redimensionar: el reproductor debe seguir presentando al tamaño nuevo.
        SizeInt32 original = AppWindow.Size;
        foreach ((int w, int h) in new[] { (1100, 700), (1700, 1000), (1300, 820) })
        {
            AppWindow.Resize(new SizeInt32(w, h));
            await Task.Delay(500);
        }

        AppWindow.Resize(original);
        await Task.Delay(500);

        // Escala de composición distinta de 1 sin tocar la configuración de Windows: una ScaleTransform sobre el panel
        // cambia CompositionScaleX igual que un monitor al 150 %; el búfer debe crecer a píxeles físicos.
        VideoPanel.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        VideoPanel.RenderTransform = new ScaleTransform { ScaleX = 1.5, ScaleY = 1.5 };
        await Task.Delay(500);
        _metrics.Note(string.Create(CultureInfo.InvariantCulture,
            $"con escala 1,5: CompositionScale {VideoPanel.CompositionScaleX:0.##}, panel {VideoPanel.ActualWidth:0}x{VideoPanel.ActualHeight:0} DIP, cadena {_engine.SwapChainWidth}x{_engine.SwapChainHeight} px (debe ser 1,5 veces el panel)"));
        VideoPanel.RenderTransform = null;
        await Task.Delay(300);
        _metrics.Note(string.Create(CultureInfo.InvariantCulture,
            $"cadena final {_engine.SwapChainWidth}x{_engine.SwapChainHeight} px, panel {VideoPanel.ActualWidth:0}x{VideoPanel.ActualHeight:0} DIP, escala {VideoPanel.CompositionScaleX:0.##}"));
        Program.ExitCode = _fatal is null && _exportClicks == 0 ? 0 : 1;
        Close();
    }

    private void Ping()
    {
        while (_pinging)
        {
            long sent = Stopwatch.GetTimestamp();
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () => _metrics.UiQueueLatency(Stopwatch.GetElapsedTime(sent).TotalMilliseconds));
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Espacio por la cola de entrada real (SendInput) si la ventana está al frente; si no (Windows no deja que un proceso
    /// lanzado en segundo plano se ponga al frente), se envía WM_KEYDOWN/WM_KEYUP a la ventana de entrada de WinUI, para
    /// no teclear nunca en otra aplicación.
    /// </summary>
    private unsafe void SendSpace()
    {
        _inputSentAt = Stopwatch.GetTimestamp();
        if (GetForegroundWindow() == _hwnd)
        {
            _inputMode = "SendInput";
            INPUT* inputs = stackalloc INPUT[2];
            inputs[0] = default;
            inputs[0].type = 1; // INPUT_KEYBOARD
            inputs[0].Anonymous.ki.wVk = 0x20; // VK_SPACE
            inputs[1] = inputs[0];
            inputs[1].Anonymous.ki.dwFlags = 0x0002; // KEYEVENTF_KEYUP
            if (SendInput(2, inputs, sizeof(INPUT)) != 2)
            {
                _metrics.Note("SendInput no pudo inyectar la tecla");
            }

            return;
        }

        HWND target = GetFocus();
        if (target == HWND.NULL)
        {
            target = FindInputSite(_hwnd);
        }

        if (target == HWND.NULL)
        {
            _inputSentAt = 0;
            _metrics.Note("no se encontró la ventana de entrada de WinUI para enviar la tecla");
            return;
        }

        _inputMode = "PostMessage a la ventana de entrada de WinUI (la ventana no estaba al frente)";
        _ = PostMessageW(target, 0x0100, 0x20, 0x00390001); // WM_KEYDOWN, VK_SPACE, código de exploración 0x39
        _ = PostMessageW(target, 0x0101, 0x20, unchecked((nint)0xC0390001)); // WM_KEYUP
    }

    private static unsafe HWND FindInputSite(HWND parent)
    {
        char* name = stackalloc char[128];
        for (HWND child = FindWindowExW(parent, HWND.NULL, null, null); child != HWND.NULL; child = FindWindowExW(parent, child, null, null))
        {
            int length = GetClassNameW(child, name, 128);
            if (new string(name, 0, length).Contains("InputSite", StringComparison.Ordinal))
            {
                return child;
            }

            HWND nested = FindInputSite(child);
            if (nested != HWND.NULL)
            {
                return nested;
            }
        }

        return HWND.NULL;
    }

    private void WriteReport()
    {
        var extra = new List<string>
        {
            "dispositivo: " + _engine.DeviceDescription,
            "archivos: " + string.Join(" | ", _options.Files),
            string.Create(CultureInfo.InvariantCulture, $"escala_composicion: {VideoPanel.CompositionScaleX:0.##}"),
            _options.UiHz <= 0 ? "interfaz: refresco en cada cuadro de XAML" : $"interfaz: refresco a {_options.UiHz} Hz",
        };
        if (_engine.FinalStats is TimelineStats stats)
        {
            extra.Add($"sesion: mostrados={stats.FramesShown} perdidos={stats.FramesDropped} tarde={stats.LateRefreshes} errores={stats.DecodeErrors} seeks={stats.Seeks} audio={stats.Audio.Status} underruns={stats.Audio.Underruns}");
        }

        if (_fatal is not null)
        {
            extra.Add("error: " + _fatal);
        }

        string report = _metrics.Report(extra);
        string path = _options.LogPath ?? Path.Combine(AppContext.BaseDirectory, "medidas.txt");
        File.WriteAllText(path, report, new System.Text.UTF8Encoding(false));
    }
}
