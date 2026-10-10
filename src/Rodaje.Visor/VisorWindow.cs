using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;
using Rodaje.Engine.Timeline;
using Rodaje.Media;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using static Rodaje.Visor.Win32;

namespace Rodaje.Visor;

[Flags]
internal enum KeyModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>Capa tal como se compuso en el último refresco.</summary>
internal readonly record struct ShownLayer(int TrackIndex, string ClipId, long FrameIndex, LayerTransform Transform);

/// <summary>Lo que se compuso en el último refresco pintado.</summary>
/// <param name="SequenceFrame">Cuadro de la secuencia según el reloj al componer.</param>
internal sealed record ShownFrame(long SequenceFrame, IReadOnlyList<ShownLayer> Layers, CompositeResult Result)
{
    public static ShownFrame Empty { get; } = new(-1, [], default);

    public ShownLayer? Layer(int trackIndex)
    {
        foreach (ShownLayer layer in Layers)
        {
            if (layer.TrackIndex == trackIndex)
            {
                return layer;
            }
        }

        return null;
    }
}

/// <summary>Ventana Win32 del visor: bucle de mensajes y de pintado al ritmo de la pantalla.</summary>
internal sealed unsafe class VisorWindow : IDisposable
{
    private const string ClassName = "RodajeVisor";
    private const string AppTitle = "Rodaje visor";
    private const int BarHitHeight = 28;
    private const uint IdleWaitMs = 10;
    private const uint ActiveWaitMs = 100;
    private const int MaxSelectableTracks = 9;

    private static readonly Func<LayerFrame, SourceGeometry> Geometry = l => new SourceGeometry(l.SampleAspectRatio, l.Rotation);

    private static VisorWindow? s_current;

    private readonly VisorArguments _args;
    private readonly GpuDevice _gpu;
    private readonly PresentTimer _presentTimer = new();
    private readonly GpuSampler _gpuSampler;
    private HWND _hwnd;
    private VideoRenderer? _renderer;
    private VisorProject? _project;
    private TimelineSession? _session;
    private Compositor? _compositor;
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
    private int _lastSignature;
    private long _lastTitleUpdate;
    private string? _status;
    private long _statusUntil;
    private int _decodeFailures;
    private volatile string? _lastDecodeFailure;

    public VisorWindow(VisorArguments args, GpuDevice gpu)
    {
        _args = args;
        _gpu = gpu;
        s_current = this;
        _gpuSampler = new GpuSampler(gpu);
        CreateNativeWindow();
        _renderer = new VideoRenderer(gpu, _hwnd);
        RECT client;
        GetClientRect(_hwnd, &client);
        _renderer.Resize(client.right - client.left, client.bottom - client.top);
        DragAcceptFiles(_hwnd, true);
        UpdateTitle(force: true);
    }

    public TimelineSession? Session => _session;

    public VisorProject? Project => _project;

    /// <summary>Pista de video (índice desde 0) que reciben las ediciones de capa.</summary>
    public int SelectedTrack { get; private set; }

    public ShownFrame LastFrame { get; private set; } = ShownFrame.Empty;

    public PresentTimer Presents => _presentTimer;

    public GpuSampler GpuTimes => _gpuSampler;

    public string AdapterName => _gpu.AdapterName;

    public int DecodeFailures => Volatile.Read(ref _decodeFailures);

    public string? LastDecodeFailure => _lastDecodeFailure;

    public (int Width, int Height) CompositeSize => _compositor is { } c ? (c.Width, c.Height) : (0, 0);

    public void Show() => ShowWindow(_hwnd, SW_SHOWNORMAL);

    /// <summary>Muestra el diálogo de abrir; si se cancela, la ventana queda vacía (Ctrl+O o arrastrar videos).</summary>
    public void PromptForFiles()
    {
        IReadOnlyList<string> paths = FileDialog.PickFiles(_hwnd);
        if (paths.Count > 0)
        {
            Open(paths, autoplay: true);
        }
    }

    /// <summary>Abre un proyecto <c>.rodaje</c> o arma la demostración con los videos, y la reproduce.</summary>
    public bool Open(IReadOnlyList<string> paths, bool autoplay)
    {
        VisorProject project;
        TimelineSession session;
        try
        {
            project = VisorProject.Open(paths);
            session = TimelineSession.Open(project.Project, project.SequenceId, _gpu, new TimelineSessionOptions
            {
                ProjectDirectory = project.Directory,
                AllowHardwareDecoding = !_args.ForceSoftwareDecoding,
                EnableAudio = !_args.DisableAudio,
            });
        }
        catch (Exception ex) when (ex is MediaException or FfmpegNotFoundException or IOException or UnauthorizedAccessException
            or ProjectFormatException or EditException or ArgumentException or KeyNotFoundException)
        {
            string list = string.Join("\n", paths);
            ReportError($"No se pudo abrir:\n{list}\n\n{ex.Message}");
            return false;
        }

        CloseSession();
        _project = project;
        _session = session;
        _session.DecodeFailed += OnDecodeFailed;
        SelectedTrack = Math.Max(0, Math.Min(project.Sequence.VideoTracks.Count, MaxSelectableTracks) - 1);
        EnsureCompositor();
        LastFrame = ShownFrame.Empty;
        _presentTimer.Reset();
        _dirty = true;
        if (autoplay)
        {
            _session.Play();
        }

        UpdateTitle(force: true);
        if (project.Skipped.Count > 0)
        {
            ShowStatus($"Se omitieron {project.Skipped.Count} archivo(s) que el visor no puede reproducir.", seconds: 10);
            ReportNotice("Estos archivos se dejaron fuera de la demostración:\n\n" + VisorProject.DescribeSkipped(project.Skipped));
        }

        return true;
    }

    public void StartAutomation(AutomatedRun automation) => _automation = automation;

    public void RequestExit(int exitCode)
    {
        _exitCode = exitCode;
        _quit = true;
    }

    /// <summary>Hace lo mismo que la tecla con esos modificadores (también lo usa el recorrido automático).</summary>
    public bool PressKey(int key, KeyModifiers modifiers = KeyModifiers.None)
    {
        bool handled = HandleKey(key, modifiers);
        if (handled)
        {
            _dirty = true;
            UpdateTitle(force: true);
        }

        return handled;
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

            bool active = _session is not null && (_session.IsPlaying || _session.IsSeekPending) || _dirty;
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
                if (_session is not null)
                {
                    using CompositionFrame frame = _session.Tick();
                    if (Signature(frame) != _lastSignature)
                    {
                        _dirty = true;
                    }
                }

                UpdateTitle(force: false);
            }
        }

        return _exitCode;
    }

    public void Dispose()
    {
        CloseSession();
        _gpuSampler.Dispose();
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

    private static KeyModifiers CurrentModifiers() =>
        (IsKeyDown(VK_CONTROL) ? KeyModifiers.Control : 0)
        | (IsKeyDown(VK_SHIFT) ? KeyModifiers.Shift : 0)
        | (IsKeyDown(VK_MENU) ? KeyModifiers.Alt : 0);

    /// <summary>Número de 1 a 9 de la fila de dígitos o del teclado numérico; 0 si es otra tecla.</summary>
    private static int DigitOf(int key) => key switch
    {
        >= '1' and <= '9' => key - '0',
        >= VK_NUMPAD1 and <= VK_NUMPAD9 => key - VK_NUMPAD0,
        _ => 0,
    };

    private static int Signature(CompositionFrame frame)
    {
        var hash = new HashCode();
        hash.Add(frame.Layers.Count);
        foreach (LayerFrame layer in frame.Layers)
        {
            hash.Add(layer.TrackIndex);
            hash.Add(layer.ClipId);
            hash.Add(layer.Frame.Index);
            hash.Add(layer.Transform);
        }

        return hash.ToHashCode();
    }

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

    private void OnDecodeFailed(object? sender, TimelineDecodeFailedEventArgs e)
    {
        Interlocked.Increment(ref _decodeFailures);
        string where = e.FrameIndex < 0 ? "no se pudo abrir" : $"falló el cuadro {e.FrameIndex}";
        _lastDecodeFailure = $"{Path.GetFileName(e.Path)}: {where} ({e.Error.Message})";
    }

    private void CloseSession()
    {
        if (_session is not null)
        {
            _session.DecodeFailed -= OnDecodeFailed;
            _session.Dispose();
            _session = null;
        }

        _compositor?.Dispose();
        _compositor = null;
        _project = null;
    }

    /// <summary>Compone a tamaño de secuencia, o a la mitad si la ventana no llega ni a la mitad de la secuencia.</summary>
    private void EnsureCompositor()
    {
        if (_session is null || _renderer is null)
        {
            return;
        }

        int width = _session.Width;
        int height = _session.Height;
        if (_renderer.Width * 2 <= width && _renderer.Height * 2 <= height)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        if (_compositor is { } current && current.Width == width && current.Height == height)
        {
            return;
        }

        _compositor?.Dispose();
        _compositor = null;
        _compositor = new Compositor(_gpu, width, height);
        _dirty = true;
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
        _gpuSampler.Collect();
        CompositionFrame? frame = _session?.Tick();
        try
        {
            if (frame is not null && _compositor is not null)
            {
                bool measure = _gpuSampler.BeginIfDue();
                CompositeResult result = _compositor.Render(frame, Geometry);
                if (measure)
                {
                    _gpuSampler.End();
                }

                var layers = new ShownLayer[frame.Layers.Count];
                for (int i = 0; i < layers.Length; i++)
                {
                    LayerFrame layer = frame.Layers[i];
                    layers[i] = new ShownLayer(layer.TrackIndex, layer.ClipId, layer.Frame.Index, layer.Transform);
                }

                LastFrame = new ShownFrame(_session!.CurrentFrame, layers, result);
                _lastSignature = Signature(frame);
            }

            bool shown = _renderer!.Render(_session is null ? null : _compositor,
                new RenderRequest(Progress(), _session is not null, _barHover || _dragging));
            _presentTimer.Record(shown && _session is { IsPlaying: true });
        }
        finally
        {
            frame?.Dispose();
        }

        _dirty = false;
        UpdateTitle(force: false);
    }

    private double Progress()
    {
        if (_session is null || _session.Duration <= Time.Zero)
        {
            return 0;
        }

        return _session.Position.ToSeconds() / _session.Duration.ToSeconds();
    }

    private LRESULT HandleMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case WM_SIZE:
                if (_renderer is not null && wParam != SIZE_MINIMIZED)
                {
                    _renderer.Resize(LowWord(lParam), HighWord(lParam));
                    EnsureCompositor();
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
                PressKey((int)wParam.Value, CurrentModifiers());
                return 0;
            case WM_SYSKEYDOWN:
                // Alt+flechas mueve la capa; lo demás (Alt+F4, el menú) sigue su curso normal.
                return PressKey((int)wParam.Value, CurrentModifiers() | KeyModifiers.Alt) ? 0 : DefWindowProcW(hwnd, message, wParam, lParam);
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

    private bool HandleKey(int key, KeyModifiers modifiers)
    {
        bool control = modifiers.HasFlag(KeyModifiers.Control);
        bool shift = modifiers.HasFlag(KeyModifiers.Shift);
        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            return !control && HandleAltKey(key);
        }

        if (control)
        {
            return HandleControlKey(key, shift);
        }

        switch (key)
        {
            case 'F':
                ToggleFullscreen();
                return true;
            case VK_ESCAPE:
                if (_fullscreen)
                {
                    ToggleFullscreen();
                }

                return true;
            case 'I':
                _overlay = !_overlay;
                return true;
        }

        if (_session is null)
        {
            return false;
        }

        if (DigitOf(key) is > 0 and int track)
        {
            ToggleTrack(track - 1);
            return true;
        }

        switch (key)
        {
            case VK_SPACE:
                _session.TogglePlayPause();
                return true;
            case VK_LEFT:
            case VK_RIGHT:
                int sign = key == VK_LEFT ? -1 : 1;
                if (shift)
                {
                    _session.SeekBy(Time.FromSeconds(5 * sign));
                }
                else
                {
                    _session.StepFrame(sign);
                }

                return true;
            case VK_HOME:
                _session.Seek(Time.Zero);
                return true;
            case VK_END:
                _session.SeekToFrame(_session.FrameCount - 1);
                return true;
            case VK_OEM_PLUS:
            case VK_ADD:
                EditSelected((s, c) => LayerEdits.Scale(s, c, LayerEdits.ScaleFactor));
                return true;
            case VK_OEM_MINUS:
            case VK_SUBTRACT:
                EditSelected((s, c) => LayerEdits.Scale(s, c, 1 / LayerEdits.ScaleFactor));
                return true;
            case 'R':
                EditSelected((s, c) => LayerEdits.Rotate(s, c, LayerEdits.RotationStep));
                return true;
            case 'O':
                EditSelected(LayerEdits.CycleOpacity);
                return true;
            default:
                return false;
        }
    }

    private bool HandleControlKey(int key, bool shift)
    {
        switch (key)
        {
            case 'O':
                PromptForFiles();
                return true;
            case 'S':
                Save();
                return true;
            case 'Z' when shift:
            case 'Y':
                Redo();
                return true;
            case 'Z':
                Undo();
                return true;
        }

        if (_session is not null && DigitOf(key) is > 0 and int track)
        {
            Select(track - 1);
            return true;
        }

        return false;
    }

    private bool HandleAltKey(int key)
    {
        (double dx, double dy) = key switch
        {
            VK_LEFT => (-LayerEdits.MoveStep, 0.0),
            VK_RIGHT => (LayerEdits.MoveStep, 0.0),
            VK_UP => (0.0, -LayerEdits.MoveStep),
            VK_DOWN => (0.0, LayerEdits.MoveStep),
            _ => (0.0, 0.0),
        };
        if (dx == 0 && dy == 0 || _session is null)
        {
            return false;
        }

        EditSelected((s, c) => LayerEdits.Move(s, c, dx, dy));
        return true;
    }

    private void Select(int track)
    {
        if (_project is null || track >= _project.Sequence.VideoTracks.Count)
        {
            ShowStatus($"No hay pista de video {track + 1}.");
            return;
        }

        SelectedTrack = track;
        _project.History.BreakMerge();
        ShowStatus($"Capa V{track + 1} seleccionada.");
    }

    private void ToggleTrack(int track)
    {
        if (_project is null || track >= _project.Sequence.VideoTracks.Count)
        {
            ShowStatus($"No hay pista de video {track + 1}.");
            return;
        }

        Track target = _project.Sequence.VideoTracks[track];
        Execute(Edits.SetTrackState(_project.SequenceId, target.Id, hidden: !target.Hidden));
    }

    /// <summary>Clip de la capa seleccionada bajo el cursor de reproducción, o el primero de la pista.</summary>
    public string? SelectedClipId()
    {
        if (_project is null || _session is null || SelectedTrack >= _project.Sequence.VideoTracks.Count)
        {
            return null;
        }

        Track track = _project.Sequence.VideoTracks[SelectedTrack];
        return (track.ClipAt(_session.Position) ?? track.Clips.FirstOrDefault())?.Id;
    }

    private void EditSelected(Func<string, string, ICommand> edit)
    {
        if (_project is null)
        {
            return;
        }

        if (SelectedClipId() is not { } clipId)
        {
            ShowStatus($"La pista V{SelectedTrack + 1} no tiene clips.");
            return;
        }

        Execute(edit(_project.SequenceId, clipId));
    }

    private void Execute(ICommand command)
    {
        try
        {
            _project!.History.Execute(command);
        }
        catch (EditException ex)
        {
            ShowStatus(ex.Message);
            return;
        }

        _session!.Invalidate();
    }

    private void Undo()
    {
        if (_project is null || _project.History.UndoDescription is not { } what || !_project.History.Undo())
        {
            ShowStatus("No hay nada que deshacer.");
            return;
        }

        _session!.Invalidate();
        ShowStatus($"Deshecho: {what}.");
    }

    private void Redo()
    {
        if (_project is null || _project.History.RedoDescription is not { } what || !_project.History.Redo())
        {
            ShowStatus("No hay nada que rehacer.");
            return;
        }

        _session!.Invalidate();
        ShowStatus($"Rehecho: {what}.");
    }

    private void Save()
    {
        if (_project is null)
        {
            return;
        }

        try
        {
            _project.Save();
            ShowStatus($"Guardado en {_project.SavePath}", seconds: 8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectFormatException)
        {
            ReportError($"No se pudo guardar el proyecto en:\n{_project.SavePath}\n\n{ex.Message}");
        }
    }

    private void ShowStatus(string text, double seconds = 4)
    {
        _status = text;
        _statusUntil = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
    }

    private bool IsOverBar(int y) => _renderer is not null && y >= _renderer.Height - BarHitHeight;

    private void OnMouseDown(int x, int y)
    {
        if (_session is null || !IsOverBar(y))
        {
            return;
        }

        _dragging = true;
        _resumeAfterDrag = _session.IsPlaying;
        _session.Pause();
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

        bool hover = _session is not null && IsOverBar(y);
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
        if (_session is null || _dragTarget is not { } fraction)
        {
            return;
        }

        bool due = !_session.IsSeekPending || Stopwatch.GetElapsedTime(_lastDragSeek).TotalMilliseconds > 120;
        if (!due)
        {
            return;
        }

        _dragTarget = null;
        _lastDragSeek = Stopwatch.GetTimestamp();
        _session.Seek(Time.FromSeconds(fraction * _session.Duration.ToSeconds()));
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
        if (_dragTarget is { } fraction && _session is not null)
        {
            _dragTarget = null;
            _session.Seek(Time.FromSeconds(fraction * _session.Duration.ToSeconds()));
        }

        if (_resumeAfterDrag)
        {
            _session?.Play();
        }

        _dirty = true;
    }

    private void OnDrop(HDROP drop)
    {
        var paths = new List<string>();
        try
        {
            uint count = DragQueryFileW(drop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFileW(drop, i, null, 0);
                if (length == 0)
                {
                    continue;
                }

                char[] buffer = new char[length + 1];
                fixed (char* text = buffer)
                {
                    uint copied = DragQueryFileW(drop, i, text, length + 1);
                    if (copied > 0)
                    {
                        paths.Add(new string(text, 0, (int)copied));
                    }
                }
            }
        }
        finally
        {
            DragFinish(drop);
        }

        if (paths.Count > 0)
        {
            Open(paths, autoplay: true);
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
        string title = BuildTitle(now);
        fixed (char* text = title)
        {
            SetWindowTextW(_hwnd, text);
        }
    }

    private string BuildTitle(long now)
    {
        if (_session is null || _project is null)
        {
            return $"{AppTitle}: arrastra uno o varios videos aquí o pulsa Ctrl+O";
        }

        var title = new StringBuilder();
        if (_status is not null && now < _statusUntil)
        {
            title.Append(_status).Append(" | ");
        }

        if (_lastDecodeFailure is { } failure)
        {
            title.Append("Error: ").Append(failure).Append(" | ");
        }

        string state = _session.IsPlaying ? "" : " (pausa)";
        string name = _project.IsSaved
            ? Path.GetFileName(_project.SavePath)
            : $"{_project.Project.Name} (demo sin guardar; Ctrl+S la guarda como {Path.GetFileName(_project.SavePath)})";
        string changed = _project.History.IsClean ? "" : "*";
        int tracks = _project.Sequence.VideoTracks.Count;
        if (!_overlay)
        {
            title.Append(CultureInfo.InvariantCulture,
                $"{name}{changed}{state} | {tracks} capa{(tracks == 1 ? "" : "s")}, V{SelectedTrack + 1} seleccionada | I: estadísticas - {AppTitle}");
            return title.ToString();
        }

        TimelineStats stats = _session.Stats;
        long frame = LastFrame.SequenceFrame >= 0 ? LastFrame.SequenceFrame : _session.CurrentFrame;
        title.Append(CultureInfo.InvariantCulture,
            $"{Timecode.Format(_session.Position)} / {Timecode.Format(_session.Duration)} | cuadro {frame + 1}/{_session.FrameCount}{state} | ");
        title.Append(CultureInfo.InvariantCulture, $"capas {LastFrame.Layers.Count}/{tracks}:");
        foreach (LayerStats layer in stats.Layers)
        {
            title.Append(CultureInfo.InvariantCulture, $" V{layer.TrackIndex + 1} tarde {layer.LateRefreshes} perd. {layer.FramesDropped};");
        }

        GpuSummary gpu = _gpuSampler.Summarize();
        title.Append(CultureInfo.InvariantCulture,
            $" | GPU {gpu.LastMs:0.00} ms (máx {gpu.MaxMs:0.00}) {CompositeSize.Width}x{CompositeSize.Height}"
            + $" | decodificadores {stats.DecodersOpen} | caché {stats.CacheBytes / (1024.0 * 1024):0} MB"
            + $" | pre-roll {stats.PreRollHitRate * 100:0} % ({stats.PreRollHits}/{stats.PreRollHits + stats.PreRollMisses})"
            + $" | audio: {stats.Audio.Status} | V{SelectedTrack + 1} sel. | {name}{changed}");
        return title.ToString();
    }

    /// <summary>Aviso informativo; en la prueba automática no se muestra (el informe ya lo lista).</summary>
    private void ReportNotice(string message)
    {
        if (_args.TestDuration is not null)
        {
            return;
        }

        fixed (char* text = message)
        fixed (char* caption = AppTitle)
        {
            _ = MessageBoxW(_hwnd, text, caption, MB_OK | MB_ICONINFORMATION);
        }
    }

    private void ReportError(string message)
    {
        if (_args.TestDuration is not null)
        {
            string report = "RESULTADO: FALLÓ. " + message + Environment.NewLine;
            Console.Out.Write(report);
            if (_args.LogPath is not null)
            {
                File.WriteAllText(_args.LogPath, report, new UTF8Encoding(false));
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

internal readonly record struct GpuSummary(int Samples, double LastMs, double MedianMs, double MaxMs);

/// <summary>Mide en la GPU lo que tarda la composición, unas cuatro veces por segundo para no frenar el bucle.</summary>
/// <remarks>La consulta se lee en el refresco siguiente, cuando la GPU ya terminó ese trabajo.</remarks>
internal sealed class GpuSampler(GpuDevice gpu) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(2);

    private readonly GpuTimer _timer = new(gpu);
    private readonly List<double> _samples = [];
    private long _lastBegin;
    private bool _pending;

    public bool BeginIfDue()
    {
        if (_pending || Stopwatch.GetElapsedTime(_lastBegin) < Interval)
        {
            return false;
        }

        _lastBegin = Stopwatch.GetTimestamp();
        _timer.Begin();
        return true;
    }

    public void End()
    {
        _timer.End();
        _pending = true;
    }

    public void Collect()
    {
        if (!_pending)
        {
            return;
        }

        _pending = false;
        if (_timer.WaitMilliseconds(ReadTimeout) is { } ms)
        {
            _samples.Add(ms);
        }
    }

    public void Reset() => _samples.Clear();

    public GpuSummary Summarize()
    {
        if (_samples.Count == 0)
        {
            return default;
        }

        double[] sorted = [.. _samples];
        Array.Sort(sorted);
        return new GpuSummary(sorted.Length, _samples[^1], sorted[sorted.Length / 2], sorted[^1]);
    }

    public void Dispose() => _timer.Dispose();
}
