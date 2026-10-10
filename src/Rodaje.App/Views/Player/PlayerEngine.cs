using System.Collections.Concurrent;
using System.Diagnostics;
using Rodaje.App.Diagnostics;
using Rodaje.App.Services;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Hilo de render: dueño del <see cref="GpuDevice"/>, la <see cref="TimelineSession"/>, el <see cref="Compositor"/> y la
/// cadena de intercambio. La interfaz solo encola órdenes y lee <see cref="Snapshot"/>; nunca espera a este hilo.
/// </summary>
/// <remarks>
/// Reproduciendo, el hilo espera el objeto de latencia de la cadena (uno por refresco) y luego hace Tick, compone y
/// presenta. En pausa duerme hasta una orden o 15 ms, y solo pinta si cambió el cuadro (por ejemplo, tras un seek).
/// Los proyectos que llegan con <see cref="Load"/> y <see cref="Reload"/> son copias: este hilo es el único que las lee.
/// </remarks>
internal sealed partial class PlayerEngine : IPlaybackEngine, IDisposable
{
    private static readonly Func<LayerFrame, SourceGeometry> Geometry = l => new SourceGeometry(l.SampleAspectRatio, l.Rotation);
    private static readonly TimeSpan GpuSampleInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromMilliseconds(500);

    private readonly bool _enableAudio;
    private readonly Metrics _metrics;
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly Lock _pendingLock = new();
    private volatile bool _stop;
    private bool _disposed;
    private volatile PlaybackSnapshot _snapshot = PlaybackSnapshot.Empty;
    private SizeRequest? _pendingSize;
    private (Project Project, string SequenceId)? _pendingLoad;
    private Project? _pendingReload;

    private GpuDevice? _gpu;
    private TimelineSession? _session;
    private Compositor? _compositor;
    private Presenter? _presenter;
    private CompositionSwapChain? _swapChain;
    private GpuTimer? _gpuTimer;
    private PreviewProxies? _proxies;
    private ProxyUsage _proxyUsage = ProxyUsage.Auto;
    private Project? _project;
    private string? _sequenceId;
    private bool _gpuPending;
    private long _lastGpuBegin;
    private long _lastStats;
    private long _framesDropped;
    private bool _dirty = true;
    private int _lastSignature;
    private long _awaitingPresentSince;
    private long _resizeSince;

    public PlayerEngine(bool enableAudio, ProxyUsage proxyUsage, Metrics metrics)
    {
        _enableAudio = enableAudio;
        _proxyUsage = proxyUsage;
        _metrics = metrics;
        _thread = new Thread(Run) { Name = "Rodaje render", IsBackground = true, Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>La cadena de intercambio existe: hay que darla al panel desde el hilo de la interfaz. Llega desde el hilo de render.</summary>
    public event Action<nint>? SwapChainCreated;

    /// <summary>Error que deja el reproductor sin imagen, en español. Llega desde el hilo de render.</summary>
    public event Action<string>? Failed;

    /// <summary>Error que no detiene el reproductor (por ejemplo, una secuencia que no se pudo abrir). Llega desde el hilo de render.</summary>
    public event Action<string>? Warning;

    public PlaybackSnapshot Snapshot => _snapshot;

    /// <summary>Nombre de la GPU, cuando ya se creó el dispositivo.</summary>
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
        lock (_pendingLock)
        {
            _pendingSize = new SizeRequest(width, height, scaleX, scaleY, Stopwatch.GetTimestamp());
        }

        _wake.Set();
    }

    public void Load(Project project, string sequenceId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequenceId);
        lock (_pendingLock)
        {
            _pendingLoad = (project, sequenceId);
            _pendingReload = null;
        }

        _wake.Set();
    }

    public void Reload(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        lock (_pendingLock)
        {
            // Solo cuenta el último: si llegan varios entre dos refrescos, los anteriores ya no importan.
            _pendingReload = project;
        }

        _wake.Set();
    }

    public void Play() => Post(new Command(CommandKind.Play));

    public void Pause() => Post(new Command(CommandKind.Pause));

    public void TogglePlayPause() => Post(new Command(CommandKind.TogglePlay, Stopwatch.GetTimestamp()));

    public void Seek(Time position) => Post(new Command(CommandKind.Seek, Position: position));

    public void StepFrames(int frames) => Post(new Command(CommandKind.Step, Frames: frames));

    public void SetProxyUsage(ProxyUsage usage) => Post(new Command(CommandKind.Proxies, Proxies: usage));

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
                ApplyProject();
                ProcessCommands();
                if (_session is null || _swapChain is null)
                {
                    PublishSnapshot();
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
    }

    private void Shutdown()
    {
        CloseSession();
        _compositor?.Dispose();
        _gpuTimer?.Dispose();
        _presenter?.Dispose();
        _swapChain?.Dispose();
        _proxies?.Dispose();
        _gpu?.Dispose();
    }

    private void CloseSession()
    {
        if (_session is null)
        {
            return;
        }

        FinalStats = _session.Stats;
        _session.Dispose();
        _session = null;
    }

    private void ApplyResize()
    {
        SizeRequest? request;
        lock (_pendingLock)
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

    private void ApplyProject()
    {
        (Project Project, string SequenceId)? load;
        Project? reload;
        lock (_pendingLock)
        {
            load = _pendingLoad;
            reload = _pendingReload;
            _pendingLoad = null;
            _pendingReload = null;
        }

        if (load is { } next)
        {
            _project = next.Project;
            _sequenceId = next.SequenceId;
            OpenSession();
        }

        if (reload is not null && _session is not null)
        {
            _project = reload;
            try
            {
                _session.Reload(reload);
            }
            catch (KeyNotFoundException ex)
            {
                Warning?.Invoke(ex.Message);
            }

            EnsureCompositor();
            _dirty = true;
        }
    }

    private void OpenSession()
    {
        CloseSession();
        _framesDropped = 0;
        if (_project is null || _sequenceId is null || _gpu is null)
        {
            return;
        }

        if (_proxyUsage != ProxyUsage.Never)
        {
            _proxies ??= PreviewProxies.Create();
        }

        try
        {
            _session = TimelineSession.Open(_project, _sequenceId, _gpu, new TimelineSessionOptions
            {
                EnableAudio = _enableAudio,
                UseProxies = _proxyUsage,
                ProxyResolver = _proxyUsage == ProxyUsage.Never ? null : _proxies?.Resolver,
            });
        }
#pragma warning disable CA1031 // Una secuencia que no abre deja el reproductor vacío, no el hilo muerto.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Warning?.Invoke("No se pudo abrir la secuencia: " + ex.Message);
            return;
        }

        _session.DecodeFailed += (_, e) => _metrics.Note($"decodificación falló en {Path.GetFileName(e.Path)} cuadro {e.FrameIndex}: {e.Error.Message}");
        _metrics.Milestone("sesion_abierta");
        _lastSignature = 0;
        EnsureCompositor();
        _dirty = true;
    }

    private void ProcessCommands()
    {
        while (_commands.TryDequeue(out Command command))
        {
            if (command.Kind == CommandKind.Proxies)
            {
                if (command.Proxies != _proxyUsage)
                {
                    _proxyUsage = command.Proxies;
                    Time position = _session?.Position ?? Time.Zero;
                    OpenSession();
                    _session?.Seek(position);
                }

                continue;
            }

            if (_session is null)
            {
                continue;
            }

            switch (command.Kind)
            {
                case CommandKind.TogglePlay:
                    _session.TogglePlayPause();
                    long applied = Stopwatch.GetTimestamp();
                    _metrics.HandlerToApplied(Stopwatch.GetElapsedTime(command.PostedAt, applied).TotalMilliseconds);
                    _awaitingPresentSince = applied;
                    break;
                case CommandKind.Play:
                    _session.Play();
                    break;
                case CommandKind.Pause:
                    _session.Pause();
                    break;
                case CommandKind.Seek:
                    _session.Seek(command.Position);
                    break;
                case CommandKind.Step:
                    _session.StepFrame(command.Frames);
                    break;
            }

            _dirty = true;
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
        if (_session is not { } s)
        {
            PlaybackSnapshot empty = PlaybackSnapshot.Empty with { Status = _gpu is null ? "Preparando el reproductor..." : "Sin secuencia" };
            if (!empty.Equals(_snapshot))
            {
                _snapshot = empty;
            }

            return;
        }

        if (Stopwatch.GetElapsedTime(_lastStats) >= StatsInterval)
        {
            _lastStats = Stopwatch.GetTimestamp();
            _framesDropped = s.Stats.FramesDropped;
        }

        string status = s.IsPlaying ? "Reproduciendo" : "En pausa";
        var next = new PlaybackSnapshot(s.Position, s.Duration, s.IsPlaying, s.CurrentFrame, s.FrameCount, status) { FramesDropped = _framesDropped };
        if (!next.Equals(_snapshot))
        {
            _snapshot = next;
        }
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

    private enum CommandKind
    {
        TogglePlay,
        Play,
        Pause,
        Seek,
        Step,
        Proxies,
    }

    private readonly record struct Command(CommandKind Kind, long PostedAt = 0, Time Position = default, int Frames = 0, ProxyUsage Proxies = ProxyUsage.Auto);

    private readonly record struct SizeRequest(int Width, int Height, float ScaleX, float ScaleY, long RequestedAt);
}
