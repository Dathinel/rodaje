using System.Collections.Concurrent;
using System.Diagnostics;
using Rodaje.App.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;
using Rodaje.Engine.Timeline;
using Rodaje.Media;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App.Player;

/// <summary>Lo que la interfaz lee del reproductor; se reemplaza entero, así que se lee sin candado.</summary>
internal sealed record PlayerSnapshot(Time Position, Time Duration, bool IsPlaying, long Frame, long FrameCount, string Status)
{
    public static readonly PlayerSnapshot Empty = new(Time.Zero, Time.Zero, false, 0, 0, "Preparando el reproductor...");
}

/// <summary>
/// Hilo de render: dueño del <see cref="GpuDevice"/>, la <see cref="TimelineSession"/>, el <see cref="Compositor"/> y la
/// cadena de intercambio. La interfaz solo encola órdenes y lee <see cref="Snapshot"/>; nunca espera a este hilo.
/// </summary>
/// <remarks>
/// Reproduciendo, el hilo espera el objeto de latencia de la cadena (uno por refresco) y luego hace Tick, compone y
/// presenta. En pausa duerme hasta una orden o 15 ms, y solo pinta si cambió el cuadro (por ejemplo, tras un seek).
/// </remarks>
internal sealed partial class PlayerEngine : IDisposable
{
    private static readonly Func<LayerFrame, SourceGeometry> Geometry = l => new SourceGeometry(l.SampleAspectRatio, l.Rotation);
    private static readonly TimeSpan GpuSampleInterval = TimeSpan.FromMilliseconds(250);

    private readonly IReadOnlyList<string> _files;
    private readonly bool _enableAudio;
    private readonly Metrics _metrics;
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private bool _disposed;
    private volatile PlayerSnapshot _snapshot = PlayerSnapshot.Empty;
    private SizeRequest? _pendingSize;
    private readonly Lock _sizeLock = new();

    private GpuDevice? _gpu;
    private TimelineSession? _session;
    private Compositor? _compositor;
    private Presenter? _presenter;
    private CompositionSwapChain? _swapChain;
    private GpuTimer? _gpuTimer;
    private bool _gpuPending;
    private long _lastGpuBegin;
    private bool _dirty = true;
    private int _lastSignature;
    private long _awaitingPresentSince;
    private long _resizeSince;

    public PlayerEngine(IReadOnlyList<string> files, bool enableAudio, Metrics metrics)
    {
        _files = files;
        _enableAudio = enableAudio;
        _metrics = metrics;
        _thread = new Thread(Run) { Name = "Rodaje render", IsBackground = true, Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>La cadena de intercambio existe: hay que darla al panel desde el hilo de la interfaz. Llega desde el hilo de render.</summary>
    public event Action<nint>? SwapChainCreated;

    /// <summary>Error que deja el reproductor sin imagen, en español. Llega desde el hilo de render.</summary>
    public event Action<string>? Failed;

    public PlayerSnapshot Snapshot => _snapshot;

    public string DeviceDescription { get; private set; } = "";

    public TimelineStats? FinalStats { get; private set; }

    /// <summary>El error que detuvo el motor, por si llegó antes de que alguien escuchara <see cref="Failed"/>.</summary>
    public string? FailureMessage { get; private set; }

    public int SwapChainWidth => _swapChain?.Width ?? 0;

    public int SwapChainHeight => _swapChain?.Height ?? 0;

    public void Start() => _thread.Start();

    /// <summary>Tamaño del panel en píxeles físicos y su escala de composición. Desde cualquier hilo.</summary>
    public void RequestResize(int width, int height, float scaleX, float scaleY)
    {
        lock (_sizeLock)
        {
            _pendingSize = new SizeRequest(width, height, scaleX, scaleY, Stopwatch.GetTimestamp());
        }

        _wake.Set();
    }

    /// <param name="handledAt">Marca de <see cref="Stopwatch"/> de cuando la interfaz recibió la tecla.</param>
    public void TogglePlayPause(long handledAt) => Post(new Command(CommandKind.TogglePlay, handledAt));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            _metrics.Note("el hilo de render no terminó en 3 s");
        }

        _wake.Dispose();
    }

    private void Post(Command command)
    {
        _commands.Enqueue(command);
        _wake.Set();
    }

    private void Run()
    {
        try
        {
            Initialize();
            while (!_stop)
            {
                ApplyResize();
                ProcessCommands();
                if (_session is null || _swapChain is null)
                {
                    _wake.WaitOne(15);
                    continue;
                }

                bool active = _session.IsPlaying || _session.IsSeekPending || _dirty;
                if (active)
                {
                    // Uno por refresco: la cadena tiene latencia máxima 1, así que esto marca el ritmo del vsync.
                    _ = WaitForSingleObjectEx((HANDLE)_swapChain.FrameWaitHandle, 100, true);
                    RenderFrame();
                }
                else
                {
                    _wake.WaitOne(15);
                    using CompositionFrame frame = _session.Tick();
                    if (Signature(frame) != _lastSignature)
                    {
                        _dirty = true;
                    }
                }

                PublishSnapshot();
            }
        }
#pragma warning disable CA1031 // El error se muestra en la interfaz en vez de tumbar el proceso desde un hilo de fondo.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _snapshot = _snapshot with { IsPlaying = false, Status = "Error: " + ex.Message };
            FailureMessage = $"{ex.Message} ({ex.GetType().Name})";
            Failed?.Invoke(FailureMessage);
        }
        finally
        {
            Shutdown();
        }
    }

    private void Initialize()
    {
        if (!FfmpegLibrary.TryInitialize(out string? ffmpegFailure))
        {
            throw new InvalidOperationException(ffmpegFailure);
        }

        _gpu = GpuDevice.TryCreate(out string? gpuFailure) ?? throw new InvalidOperationException("Hace falta una GPU con Direct3D 11. " + gpuFailure);
        DeviceDescription = _gpu.ToString();
        _presenter = new Presenter(_gpu);
        _gpuTimer = new GpuTimer(_gpu);
        _metrics.Milestone("gpu_lista");

        if (_files.Count > 0)
        {
            DemoProject project = DemoProject.Open(_files);
            _session = TimelineSession.Open(project.Project, project.SequenceId, _gpu, new TimelineSessionOptions
            {
                ProjectDirectory = project.Directory,
                EnableAudio = _enableAudio,
            });
            _session.DecodeFailed += (_, e) => _metrics.Note($"decodificación falló en {Path.GetFileName(e.Path)} cuadro {e.FrameIndex}: {e.Error.Message}");
            _metrics.Milestone("sesion_abierta");
            _session.Play();
        }
        else
        {
            _snapshot = _snapshot with { Status = "Sin video: pasa uno en la línea de comandos." };
        }
    }

    private void Shutdown()
    {
        if (_session is not null)
        {
            FinalStats = _session.Stats;
            _session.Dispose();
            _session = null;
        }

        _compositor?.Dispose();
        _gpuTimer?.Dispose();
        _presenter?.Dispose();
        _swapChain?.Dispose();
        _gpu?.Dispose();
    }

    private void ApplyResize()
    {
        SizeRequest? request;
        lock (_sizeLock)
        {
            request = _pendingSize;
            _pendingSize = null;
        }

        if (request is not { } size || _gpu is null)
        {
            return;
        }

        if (_swapChain is null)
        {
            _swapChain = new CompositionSwapChain(_gpu, size.Width, size.Height, size.ScaleX, size.ScaleY);
            _metrics.Milestone("cadena_creada");
            SwapChainCreated?.Invoke(_swapChain.NativeSwapChain);
        }
        else
        {
            _swapChain.Resize(size.Width, size.Height, size.ScaleX, size.ScaleY);
            _resizeSince = size.RequestedAt;
        }

        EnsureCompositor();
        _dirty = true;
    }

    private void ProcessCommands()
    {
        while (_commands.TryDequeue(out Command command))
        {
            if (_session is null)
            {
                continue;
            }

            switch (command.Kind)
            {
                case CommandKind.TogglePlay:
                    _session.TogglePlayPause();
                    long applied = Stopwatch.GetTimestamp();
                    _metrics.HandlerToApplied(Stopwatch.GetElapsedTime(command.HandledAt, applied).TotalMilliseconds);
                    _awaitingPresentSince = applied;
                    _dirty = true;
                    break;
            }
        }
    }

    /// <summary>Compone a tamaño de secuencia, o a la mitad si el panel no llega ni a la mitad (igual que el visor).</summary>
    private void EnsureCompositor()
    {
        if (_session is null || _swapChain is null || _gpu is null)
        {
            return;
        }

        int width = _session.Width;
        int height = _session.Height;
        if (_swapChain.Width * 2 <= width && _swapChain.Height * 2 <= height)
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
    }

    private void RenderFrame()
    {
        long start = Stopwatch.GetTimestamp();
        CollectGpuSample();
        using CompositionFrame frame = _session!.Tick();
        int drawn = 0;
        if (_compositor is not null)
        {
            bool measure = !_gpuPending && Stopwatch.GetElapsedTime(_lastGpuBegin) >= GpuSampleInterval;
            if (measure)
            {
                _lastGpuBegin = Stopwatch.GetTimestamp();
                _gpuTimer!.Begin();
            }

            drawn = _compositor.Render(frame, Geometry).LayersDrawn;
            if (measure)
            {
                _gpuTimer!.End();
                _gpuPending = true;
            }
        }

        _lastSignature = Signature(frame);
        _swapChain!.Render(_presenter!, _compositor);
        long presented = Stopwatch.GetTimestamp();
        _metrics.Presented(_session.IsPlaying && !_session.IsSeekPending, Stopwatch.GetElapsedTime(start, presented).TotalMilliseconds);
        if (drawn > 0)
        {
            _metrics.Milestone("primer_cuadro");
        }

        if (_awaitingPresentSince != 0)
        {
            _metrics.AppliedToPresent(Stopwatch.GetElapsedTime(_awaitingPresentSince, presented).TotalMilliseconds);
            _awaitingPresentSince = 0;
        }

        if (_resizeSince != 0)
        {
            _metrics.ResizeToPresent(Stopwatch.GetElapsedTime(_resizeSince, presented).TotalMilliseconds);
            _resizeSince = 0;
        }

        _dirty = false;
    }

    private void CollectGpuSample()
    {
        if (!_gpuPending)
        {
            return;
        }

        _gpuPending = false;
        if (_gpuTimer!.WaitMilliseconds(TimeSpan.FromMilliseconds(2)) is { } ms)
        {
            _metrics.GpuCompose(ms);
        }
    }

    private void PublishSnapshot()
    {
        TimelineSession s = _session!;
        string status = s.IsPlaying ? "Reproduciendo" : "En pausa";
        _snapshot = new PlayerSnapshot(s.Position, s.Duration, s.IsPlaying, s.CurrentFrame, s.FrameCount, status);
    }

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

    /// <summary>Texto corto de tiempo para la interfaz.</summary>
    public static string FormatTime(Time time) => Timecode.Format(time);

    private enum CommandKind
    {
        TogglePlay,
    }

    private readonly record struct Command(CommandKind Kind, long HandledAt);

    private readonly record struct SizeRequest(int Width, int Height, float ScaleX, float ScaleY, long RequestedAt);
}
