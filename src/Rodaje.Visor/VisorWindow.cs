using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Rodaje.Core.Timing;
using Rodaje.Engine;
using Rodaje.Engine.Presentation;
using Rodaje.Media;
using Rodaje.Media.Gpu;
using Rodaje.Playback;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using static Rodaje.Visor.Win32;

namespace Rodaje.Visor;

/// <summary>Ventana Win32 del visor: bucle de mensajes y de pintado al ritmo de la pantalla.</summary>
internal sealed unsafe class VisorWindow : IDisposable
{
    private const string ClassName = "RodajeVisor";
    private const string AppTitle = "Rodaje visor";
    private const int BarHitHeight = 28;
    private const uint IdleWaitMs = 10;
    private const uint ActiveWaitMs = 100;

    private static VisorWindow? s_current;

    private readonly VisorArguments _args;
    private readonly GpuDevice _gpu;
    private readonly PresentTimer _presentTimer = new();
    private HWND _hwnd;
    private VideoRenderer? _renderer;
    private ClipPlayer? _player;
    private AutomatedRun? _automation;
    private ExceptionDispatchHolder? _fatal;

    private bool _quit;
    private int _exitCode;
    private bool _dirty = true;
    private bool _overlay;
    private bool _fullscreen;
    private WINDOWPLACEMENT _placement;
    private bool _dragging;
    private bool _resumeAfterDrag;
    private double? _dragTarget;
    private long _lastDragSeek;
    private bool _barHover;
    private long? _lastDrawnIndex;
    private long _lastTitleUpdate;

    public VisorWindow(VisorArguments args, GpuDevice gpu)
    {
        _args = args;
        _gpu = gpu;
        s_current = this;
        CreateNativeWindow();
        _renderer = new VideoRenderer(gpu, _hwnd);
        RECT client;
        GetClientRect(_hwnd, &client);
        _renderer.Resize(client.right - client.left, client.bottom - client.top);
        DragAcceptFiles(_hwnd, true);
        UpdateTitle(force: true);
    }

    public ClipPlayer? Player => _player;

    /// <summary>Índice del último cuadro pintado y si era el que pedía el reloj.</summary>
    public FramePresentation LastPresentation { get; private set; }

    public PresentTimer Presents => _presentTimer;

    public string AdapterName => _gpu.AdapterName;

    public void Show() => ShowWindow(_hwnd, SW_SHOWNORMAL);

    /// <summary>Muestra el diálogo de abrir; si se cancela, la ventana queda vacía (Ctrl+O o arrastrar un video).</summary>
    public void PromptForFile()
    {
        if (FileDialog.PickVideo(_hwnd) is { } path)
        {
            OpenFile(path, autoplay: true);
        }
    }

    public bool OpenFile(string path, bool autoplay)
    {
        ClipPlayer next;
        try
        {
            next = ClipPlayer.Open(path, new ClipPlayerOptions
            {
                Device = _gpu,
                AllowHardwareDecoding = !_args.ForceSoftwareDecoding,
                EnableAudio = !_args.DisableAudio,
            });
        }
        catch (Exception ex) when (ex is MediaException or FfmpegNotFoundException or IOException or UnauthorizedAccessException)
        {
            ReportError($"No se pudo abrir el archivo:\n{path}\n\n{ex.Message}");
            return false;
        }

        _renderer?.ReleaseFrameViews();
        _player?.Dispose();
        _player = next;
        _lastDrawnIndex = null;
        LastPresentation = default;
        _dirty = true;
        if (autoplay)
        {
            _player.Play();
        }

        UpdateTitle(force: true);
        return true;
    }

    public void StartAutomation(AutomatedRun automation) => _automation = automation;

    public void RequestExit(int exitCode)
    {
        _exitCode = exitCode;
        _quit = true;
    }

    /// <summary>Bucle principal: mensajes, automatización y un cuadro por refresco mientras haya algo que mostrar.</summary>
    public int Run()
    {
        while (!_quit)
        {
            PumpMessages();
            _fatal?.Rethrow();
            if (_quit)
            {
                break;
            }

            ApplyDragSeek();
            _automation?.Update(this);
            if (_quit)
            {
                break;
            }

            bool active = _player is not null && (_player.IsPlaying || _player.IsSeekPending) || _dirty;
            HANDLE wait = _renderer!.FrameWaitHandle;
            if (active)
            {
                uint result = MsgWaitForMultipleObjectsEx(1, &wait, ActiveWaitMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                if (result == WAIT_OBJECT_0)
                {
                    RenderFrame();
                }
            }
            else
            {
                _ = MsgWaitForMultipleObjectsEx(0, null, IdleWaitMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                // En pausa no se pinta, pero un cuadro pedido puede haber llegado.
                if (_player is not null && _player.Tick().Frame?.Index != _lastDrawnIndex)
                {
                    _dirty = true;
                }

                UpdateTitle(force: false);
            }
        }

        return _exitCode;
    }

    public void Dispose()
    {
        _player?.Dispose();
        _player = null;
        _renderer?.Dispose();
        _renderer = null;
        if (_hwnd != HWND.NULL)
        {
            DestroyWindow(_hwnd);
            _hwnd = HWND.NULL;
        }

        s_current = null;
    }

    private static int LowWord(nint value) => (short)(value & 0xFFFF);

    private static int HighWord(nint value) => (short)((value >> 16) & 0xFFFF);

    private static bool IsKeyDown(int key) => GetKeyState(key) < 0;

    [UnmanagedCallersOnly]
    private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        VisorWindow? window = s_current;
        if (window is null)
        {
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

#pragma warning disable CA1031 // Una excepción no puede cruzar a código nativo: se guarda y el bucle la relanza.
        try
        {
            return window.HandleMessage(hwnd, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            window._fatal = new ExceptionDispatchHolder(ex);
            return 0;
        }
#pragma warning restore CA1031
    }

    private void CreateNativeWindow()
    {
        HINSTANCE instance = GetModuleHandleW(null);
        fixed (char* className = ClassName)
        fixed (char* title = AppTitle)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS,
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hCursor = LoadCursorW(HINSTANCE.NULL, IDC_ARROW),
                lpszClassName = className,
            };
            if (RegisterClassExW(&windowClass) == 0)
            {
                throw new InvalidOperationException($"No se pudo registrar la clase de ventana (error {Marshal.GetLastSystemError()}).");
            }

            uint dpi = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393) ? GetDpiForSystem() : 96;
            var rect = new RECT { right = MulDiv(1280, (int)dpi, 96), bottom = MulDiv(720, (int)dpi, 96) };
            AdjustWindowRectEx(&rect, WS_OVERLAPPEDWINDOW, false, 0);
            _hwnd = CreateWindowExW(0, className, title, WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
                rect.right - rect.left, rect.bottom - rect.top, HWND.NULL, HMENU.NULL, instance, null);
            if (_hwnd == HWND.NULL)
            {
                throw new InvalidOperationException($"No se pudo crear la ventana (error {Marshal.GetLastSystemError()}).");
            }
        }
    }

    private void PumpMessages()
    {
        MSG msg;
        while (PeekMessageW(&msg, HWND.NULL, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT)
            {
                _quit = true;
                return;
            }

            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    private void RenderFrame()
    {
        FramePresentation presentation = _player?.Tick() ?? default;
        LastPresentation = presentation;
        bool shown = _renderer!.Render(new RenderRequest(
            presentation.Frame?.Payload as D3D11TexturePayload,
            _player?.Video.DisplayWidth ?? 0,
            _player?.Video.DisplayHeight ?? 0,
            _player?.Video.Rotation ?? 0,
            Progress(),
            _player is not null,
            _barHover || _dragging));
        _presentTimer.Record(shown && _player is { IsPlaying: true });
        _lastDrawnIndex = presentation.Frame?.Index;
        _dirty = false;
        UpdateTitle(force: false);
    }

    private double Progress()
    {
        if (_player is null || _player.Duration <= Time.Zero)
        {
            return 0;
        }

        return _player.Position.ToSeconds() / _player.Duration.ToSeconds();
    }

    private LRESULT HandleMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case WM_SIZE:
                if (_renderer is not null && wParam != SIZE_MINIMIZED)
                {
                    _renderer.Resize(LowWord(lParam), HighWord(lParam));
                    _dirty = true;
                }

                return 0;
            case WM_PAINT:
                PAINTSTRUCT paint;
                BeginPaint(hwnd, &paint);
                EndPaint(hwnd, &paint);
                _dirty = true;
                return 0;
            case WM_ERASEBKGND:
                return 1;
            case WM_GETMINMAXINFO:
                var info = (MINMAXINFO*)lParam;
                info->ptMinTrackSize.x = 320;
                info->ptMinTrackSize.y = 200;
                return 0;
            case WM_KEYDOWN:
                OnKey((int)wParam.Value);
                return 0;
            case WM_LBUTTONDOWN:
                OnMouseDown(LowWord(lParam), HighWord(lParam));
                return 0;
            case WM_LBUTTONDBLCLK:
                if (!IsOverBar(HighWord(lParam)))
                {
                    ToggleFullscreen();
                }

                return 0;
            case WM_MOUSEMOVE:
                OnMouseMove(LowWord(lParam), HighWord(lParam));
                return 0;
            case WM_LBUTTONUP:
                EndDrag();
                return 0;
            case WM_MOUSELEAVE:
                _barHover = false;
                _dirty = true;
                return 0;
            case WM_CAPTURECHANGED:
                if (_dragging)
                {
                    EndDrag();
                }

                return 0;
            case WM_DROPFILES:
                OnDrop((HDROP)(nint)wParam.Value);
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private void OnKey(int key)
    {
        bool shift = IsKeyDown(VK_SHIFT);
        bool control = IsKeyDown(VK_CONTROL);
        if (key == 'O' && control)
        {
            string? path = FileDialog.PickVideo(_hwnd);
            if (path is not null)
            {
                OpenFile(path, autoplay: true);
            }

            return;
        }

        switch (key)
        {
            case 'F':
                ToggleFullscreen();
                return;
            case VK_ESCAPE:
                if (_fullscreen)
                {
                    ToggleFullscreen();
                }

                return;
            case 'I':
                _overlay = !_overlay;
                UpdateTitle(force: true);
                return;
        }

        if (_player is null)
        {
            return;
        }

        switch (key)
        {
            case VK_SPACE:
                _player.TogglePlayPause();
                break;
            case VK_LEFT:
                if (shift)
                {
                    _player.SeekBy(-Time.FromSeconds(5));
                }
                else
                {
                    _player.StepFrame(-1);
                }

                break;
            case VK_RIGHT:
                if (shift)
                {
                    _player.SeekBy(Time.FromSeconds(5));
                }
                else
                {
                    _player.StepFrame(1);
                }

                break;
            case VK_HOME:
                _player.Seek(Time.Zero);
                break;
            case VK_END:
                _player.SeekToFrame(_player.FrameCount - 1);
                break;
            default:
                return;
        }

        _dirty = true;
        UpdateTitle(force: true);
    }

    private bool IsOverBar(int y) => _renderer is not null && y >= _renderer.Height - BarHitHeight;

    private void OnMouseDown(int x, int y)
    {
        if (_player is null || !IsOverBar(y))
        {
            return;
        }

        _dragging = true;
        _resumeAfterDrag = _player.IsPlaying;
        _player.Pause();
        SetCapture(_hwnd);
        _dragTarget = Fraction(x);
        _lastDragSeek = 0;
        _dirty = true;
    }

    private void OnMouseMove(int x, int y)
    {
        if (_dragging)
        {
            _dragTarget = Fraction(x);
            return;
        }

        bool hover = _player is not null && IsOverBar(y);
        if (hover != _barHover)
        {
            _barHover = hover;
            _dirty = true;
            if (hover)
            {
                var track = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE, hwndTrack = _hwnd };
                TrackMouseEvent(&track);
            }
        }
    }

    private double Fraction(int x) => _renderer is null || _renderer.Width <= 0 ? 0 : Math.Clamp((double)x / _renderer.Width, 0, 1);

    // Mientras se arrastra, un seek nuevo cancela la decodificación del anterior: sin este freno, un arrastre
    // continuo nunca dejaría terminar un cuadro y la imagen no cambiaría.
    private void ApplyDragSeek()
    {
        if (_player is null || _dragTarget is not { } fraction)
        {
            return;
        }

        bool due = !_player.IsSeekPending || Stopwatch.GetElapsedTime(_lastDragSeek).TotalMilliseconds > 120;
        if (!due)
        {
            return;
        }

        _dragTarget = null;
        _lastDragSeek = Stopwatch.GetTimestamp();
        _player.Seek(Time.FromSeconds(fraction * _player.Duration.ToSeconds()));
        _dirty = true;
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseCapture();
        ApplyDragSeek();
        if (_dragTarget is { } fraction && _player is not null)
        {
            _dragTarget = null;
            _player.Seek(Time.FromSeconds(fraction * _player.Duration.ToSeconds()));
        }

        if (_resumeAfterDrag)
        {
            _player?.Play();
        }

        _dirty = true;
    }

    private void OnDrop(HDROP drop)
    {
        try
        {
            uint length = DragQueryFileW(drop, 0, null, 0);
            if (length == 0)
            {
                return;
            }

            char* buffer = stackalloc char[(int)length + 1];
            uint copied = DragQueryFileW(drop, 0, buffer, length + 1);
            if (copied > 0)
            {
                OpenFile(new string(buffer, 0, (int)copied), autoplay: true);
            }
        }
        finally
        {
            DragFinish(drop);
        }
    }

    private void ToggleFullscreen()
    {
        int style = GetWindowLongW(_hwnd, GWL_STYLE);
        if (!_fullscreen)
        {
            var monitor = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            WINDOWPLACEMENT placement = default;
            placement.length = (uint)sizeof(WINDOWPLACEMENT);
            if (!GetWindowPlacement(_hwnd, &placement)
                || !GetMonitorInfoW(MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST), &monitor))
            {
                return;
            }

            _placement = placement;
            _ = SetWindowLongW(_hwnd, GWL_STYLE, style & ~unchecked((int)WS_OVERLAPPEDWINDOW));
            RECT r = monitor.rcMonitor;
            SetWindowPos(_hwnd, HWND.HWND_TOP, r.left, r.top, r.right - r.left, r.bottom - r.top, SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
            _fullscreen = true;
        }
        else
        {
            _ = SetWindowLongW(_hwnd, GWL_STYLE, style | unchecked((int)WS_OVERLAPPEDWINDOW));
            WINDOWPLACEMENT placement = _placement;
            SetWindowPlacement(_hwnd, &placement);
            SetWindowPos(_hwnd, HWND.NULL, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
            _fullscreen = false;
        }

        _dirty = true;
    }

    private void UpdateTitle(bool force)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(_lastTitleUpdate, now).TotalMilliseconds < 250)
        {
            return;
        }

        _lastTitleUpdate = now;
        string title = BuildTitle();
        fixed (char* text = title)
        {
            SetWindowTextW(_hwnd, text);
        }
    }

    private string BuildTitle()
    {
        if (_player is null)
        {
            return $"{AppTitle}: arrastra un video aquí o pulsa Ctrl+O";
        }

        string name = Path.GetFileName(_player.Path);
        string state = _player.IsPlaying ? "" : " (pausa)";
        if (!_overlay)
        {
            return $"{name}{state} - {AppTitle}";
        }

        ClipPlayerStats stats = _player.Stats;
        PlaybackStats p = stats.Playback;
        long frame = LastPresentation.Frame?.Index ?? _player.CurrentFrame;
        return string.Create(CultureInfo.InvariantCulture,
            $"{Timecode.Format(_player.Position)} / {Timecode.Format(_player.Duration)} | cuadro {frame + 1}/{_player.FrameCount}{state} | "
            + $"{stats.DecodeModeText} | perdidos {p.FramesDropped}, tarde {p.LateRefreshes} | "
            + $"A/V máx {p.MaxAvOffset.ToSeconds() * 1000:0.0} ms | audio: {stats.AudioStatus} | {name}");
    }

    private void ReportError(string message)
    {
        if (_args.TestDuration is not null)
        {
            string report = "RESULTADO: FALLÓ. " + message + Environment.NewLine;
            Console.Out.Write(report);
            if (_args.LogPath is not null)
            {
                File.WriteAllText(_args.LogPath, report, new System.Text.UTF8Encoding(false));
            }

            return;
        }

        fixed (char* text = message)
        fixed (char* caption = AppTitle)
        {
            _ = MessageBoxW(_hwnd, text, caption, MB_OK | MB_ICONWARNING);
        }
    }

    private sealed class ExceptionDispatchHolder(Exception exception)
    {
        public void Rethrow() => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
    }
}

/// <summary>Intervalos entre presentaciones mientras se reproduce: la medida objetiva más cercana a "se ve fluido".</summary>
internal sealed class PresentTimer
{
    private readonly List<double> _intervalsMs = new(4096);
    private long _last;

    public void Record(bool playing)
    {
        long now = Stopwatch.GetTimestamp();
        if (playing && _last != 0)
        {
            _intervalsMs.Add(Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds);
        }

        _last = playing ? now : 0;
    }

    public void Reset()
    {
        _intervalsMs.Clear();
        _last = 0;
    }

    /// <summary>Mediana, máximo y cuántos intervalos pasaron de 1,5 veces la mediana (un refresco perdido).</summary>
    public (int Count, double MedianMs, double MaxMs, int Hitches) Summarize()
    {
        if (_intervalsMs.Count == 0)
        {
            return (0, 0, 0, 0);
        }

        double[] sorted = [.. _intervalsMs];
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];
        int hitches = sorted.Count(i => i > median * 1.5);
        return (sorted.Length, median, sorted[^1], hitches);
    }
}
