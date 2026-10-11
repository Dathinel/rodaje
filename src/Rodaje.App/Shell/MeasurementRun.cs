using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Diagnostics;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using TerraFX.Interop.Windows;
using Windows.Graphics;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App.Shell;

/// <summary>
/// Recorrido de medida de <c>--medir N</c> (docs/archivo/sondeo-winui.md): espera el primer cuadro, reproduce, deja 1,5 s de
/// calentamiento, mide N s con un "ping" a la cola de la interfaz cada 50 ms; luego inyecta Espacio con el foco en
/// EXPORTAR (el atajo no debe pulsarlo) y redimensiona la ventana. Al cerrar se escribe el informe. Con un escenario
/// («efectos», «velocidad») arma antes el proyecto (<see cref="MeasurementScenarios"/>) y, después de medir, repite 30
/// veces su edición reproduciendo (un deslizador de color, o la velocidad de una capa) para ver cuánto tarda en verse.
/// </summary>
internal sealed class MeasurementRun(MainWindow window, PlayerEngine engine, Metrics metrics)
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private volatile bool _pinging;
    private long _inputSentAt;
    private int _toggles;
    private int _exports;
    private string _inputMode = "ninguno";

    public async Task RunAsync(double seconds, string? scenario = null)
    {
        window.Router.Invoked += OnInvoked;
        Func<int, SequenceEditCommand>? liveEdit = null;
        if (!MeasurementScenarios.IsKnown(scenario))
        {
            metrics.Note($"escenario desconocido: {scenario}");
            Program.ExitCode = 1;
            window.Close();
            return;
        }

        if (scenario is not null)
        {
            try
            {
                liveEdit = MeasurementScenarios.Build(scenario, window.Session);
            }
            catch (InvalidOperationException ex)
            {
                metrics.Note(ex.Message);
            }

            if (liveEdit is null)
            {
                metrics.Note($"no se pudo armar el escenario {scenario}");
                Program.ExitCode = 1;
                window.Close();
                return;
            }

            Sequence sequence = window.Session.Sequence;
            int layers = sequence.VideoTracks.Count(t => t.Clips.Count > 0);
            int transitions = sequence.AllTracks.Sum(t => t.Transitions.Count);
            int effects = sequence.AllTracks.SelectMany(t => t.Clips).Sum(c => c.Effects.Count);
            string speeds = string.Join(", ", sequence.VideoTracks.SelectMany(t => t.Clips).Select(c => c.Speed is { } s ? s.Rate.ToString() + "x" : "1x"));
            metrics.Note($"escenario {scenario}: {sequence.Width}x{sequence.Height}, {layers} capas de video, {transitions} transiciones, {effects} efectos, velocidades {speeds}");
            window.Session.Seek(Time.Zero);
        }

        var deadline = Stopwatch.StartNew();
        while (!metrics.HasMilestone("primer_cuadro") && engine.FailureMessage is null && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (!window.Session.IsPlaying && window.Session.Sequence.Duration.Ticks > 0)
            {
                window.Session.Play();
            }

            await Task.Delay(50);
        }

        if (!metrics.HasMilestone("primer_cuadro"))
        {
            metrics.Note("no llegó el primer cuadro en 30 s" + (engine.FailureMessage is { } f ? ": " + f : ""));
            Program.ExitCode = 1;
            window.Close();
            return;
        }

        await Task.Delay(1500);
        metrics.ResetSteadyState();
        long droppedBefore = engine.Snapshot.FramesDropped;
        PlayerEngine.EffectTotals effectsBefore = engine.Effects;
        TimeSpan cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        _pinging = true;
        var pinger = new Thread(Ping) { IsBackground = true, Name = "Rodaje ping" };
        pinger.Start();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        metrics.FreezeSteadyState();
        _pinging = false;
        metrics.Note($"cuadros perdidos al medir: {engine.Snapshot.FramesDropped - droppedBefore}; la calidad Auto bajó {engine.PreviewDowngrades} vez/veces");
        PlayerEngine.EffectTotals drawnEffects = engine.Effects;
        long composed = Math.Max(1, drawnEffects.Frames - effectsBefore.Frames);
        metrics.Note(string.Create(CultureInfo.InvariantCulture,
            $"efectos por cuadro al medir: {(drawnEffects.Applied - effectsBefore.Applied) / (double)composed:0.0} dibujados, {(drawnEffects.Skipped - effectsBefore.Skipped) / (double)composed:0.0} saltados, {(drawnEffects.Missing - effectsBefore.Missing) / (double)composed:0.0} sin su contenido ({composed} cuadros)"));
        using (Process self = Process.GetCurrentProcess())
        {
            double cpu = (self.TotalProcessorTime - cpuStart).TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100;
            metrics.Note(string.Create(CultureInfo.InvariantCulture,
                $"CPU del proceso en reproducción: {cpu:0.0} % de un núcleo; memoria privada {self.PrivateMemorySize64 / 1048576.0:0} MB, conjunto de trabajo {self.WorkingSet64 / 1048576.0:0} MB"));
        }

        _ = SetForegroundWindow((HWND)window.Hwnd);
        window.ExportButton.Focus(FocusState.Keyboard);
        await Task.Delay(300);
        int before = _toggles;
        for (int i = 0; i < 6; i++)
        {
            SendSpace();
            await Task.Delay(400);
        }

        metrics.Note($"Espacio inyectado 6 veces con el foco en EXPORTAR: {_toggles - before} cambios de reproducción, {_exports} clics en EXPORTAR (debe ser 0); vía: {_inputMode}");

        if (liveEdit is not null)
        {
            await EditWhilePlayingAsync(liveEdit);
        }

        // Ediciones mientras reproduce (como las del inspector): cuánto tarda cada copia del proyecto en verse.
        for (int i = 0; i < 20; i++)
        {
            engine.Reload(window.Session.Project.Clone());
            await Task.Delay(113);
        }

        SizeInt32 original = window.AppWindow.Size;
        foreach ((int w, int h) in new[] { (1100, 700), (1700, 1000), (1300, 820) })
        {
            window.AppWindow.Resize(new SizeInt32(w, h));
            await Task.Delay(500);
        }

        window.AppWindow.Resize(original);
        await Task.Delay(500);
        var panel = window.VideoPanel;
        panel.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        panel.RenderTransform = new ScaleTransform { ScaleX = 1.5, ScaleY = 1.5 };
        await Task.Delay(500);
        metrics.Note(string.Create(CultureInfo.InvariantCulture,
            $"con escala 1,5: CompositionScale {panel.CompositionScaleX:0.##}, panel {panel.ActualWidth:0}x{panel.ActualHeight:0} DIP, cadena {engine.SwapChainWidth}x{engine.SwapChainHeight} px (debe ser 1,5 veces el panel)"));
        panel.RenderTransform = null;
        await Task.Delay(300);
        window.Router.Invoked -= OnInvoked;
        Program.ExitCode = engine.FailureMessage is null && _exports == 0 ? 0 : 1;
        window.Close();
    }

    /// <summary>
    /// Repite la edición del escenario como lo haría la persona (un cambio cada 100 ms, fundidos en un paso) mientras
    /// reproduce; cada cambio se mide desde antes de ejecutar el comando hasta que se presenta.
    /// </summary>
    private async Task EditWhilePlayingAsync(Func<int, SequenceEditCommand> edit)
    {
        if (!window.Session.IsPlaying)
        {
            window.Session.Play();
            await Task.Delay(500);
        }

        for (int i = 0; i < 30; i++)
        {
            engine.MarkEditStart(Stopwatch.GetTimestamp());
            window.Session.Execute(edit(i));
            await Task.Delay(100);
        }

        window.Session.BreakMerge();
    }

    /// <summary>Escribe las cifras al cerrar, si la ventana se abrió con <c>--medir</c>.</summary>
    public static void WriteReportIfMeasuring(AppOptions options, PlayerEngine engine, Metrics metrics, double compositionScale)
    {
        if (options.MeasureSeconds is null)
        {
            return;
        }

        var extra = new List<string>
        {
            "dispositivo: " + engine.DeviceDescription,
            "archivos: " + string.Join(" | ", options.Files),
            string.Create(CultureInfo.InvariantCulture, $"escala_composicion: {compositionScale:0.##}"),
            options.UiHz <= 0 ? "interfaz: refresco en cada cuadro de XAML" : $"interfaz: refresco a {options.UiHz} Hz",
            "escenario: " + (options.Scenario ?? "un clip"),
        };
        if (engine.FinalStats is TimelineStats stats)
        {
            extra.Add($"sesion: mostrados={stats.FramesShown} perdidos={stats.FramesDropped} tarde={stats.LateRefreshes} errores={stats.DecodeErrors} seeks={stats.Seeks} audio={stats.Audio.Status} underruns={stats.Audio.Underruns}");
        }

        if (engine.FailureMessage is { } failure)
        {
            extra.Add("error: " + failure);
        }

        string path = options.LogPath ?? Path.Combine(AppContext.BaseDirectory, "medidas.txt");
        File.WriteAllText(path, metrics.Report(extra), new System.Text.UTF8Encoding(false));
    }

    private void OnInvoked(object? sender, string actionId)
    {
        if (actionId == ActionIds.TogglePlay)
        {
            _toggles++;
            if (_inputSentAt != 0)
            {
                metrics.InputToHandler(Stopwatch.GetElapsedTime(_inputSentAt, window.Keyboard.LastKeyDownTimestamp).TotalMilliseconds);
                _inputSentAt = 0;
            }
        }
        else if (actionId == ActionIds.Export)
        {
            _exports++;
        }
    }

    private void Ping()
    {
        while (_pinging)
        {
            long sent = Stopwatch.GetTimestamp();
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () => metrics.UiQueueLatency(Stopwatch.GetElapsedTime(sent).TotalMilliseconds));
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Espacio por la cola de entrada real (SendInput) si la ventana está al frente; si no, WM_KEYDOWN/WM_KEYUP a la
    /// ventana de entrada de WinUI, para no teclear nunca en otra aplicación.
    /// </summary>
    private unsafe void SendSpace()
    {
        var hwnd = (HWND)window.Hwnd;
        _inputSentAt = Stopwatch.GetTimestamp();
        if (GetForegroundWindow() == hwnd)
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
                metrics.Note("SendInput no pudo inyectar la tecla");
            }

            return;
        }

        HWND target = GetFocus();
        if (target == HWND.NULL)
        {
            target = FindInputSite(hwnd);
        }

        if (target == HWND.NULL)
        {
            _inputSentAt = 0;
            metrics.Note("no se encontró la ventana de entrada de WinUI para enviar la tecla");
            return;
        }

        _inputMode = "PostMessage a la ventana de entrada de WinUI (la ventana no estaba al frente)";
        _ = PostMessageW(target, 0x0100, 0x20, 0x00390001); // WM_KEYDOWN, VK_SPACE
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
}
