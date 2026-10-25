using System.Collections.Concurrent;
using System.Diagnostics;
using Rodaje.App.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Settings;
using Rodaje.Audio.Metering;
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
/// presenta. En pausa duerme hasta una orden, un cuadro entregado o un título listo (con un tope de seguridad para lo
/// periódico del planificador), y solo pinta si cambió el cuadro (por ejemplo, tras un seek).
/// Los proyectos que llegan con <see cref="Load"/> y <see cref="Reload"/> son copias: este hilo es el único que las lee.
/// <para>
/// Medidores: en cada vuelta este hilo publica la posición del reloj (la muestra que suena) con su marca de tiempo, y
/// <see cref="TakeAudioLevels"/>, desde el hilo de la interfaz, la extrapola al instante en que dibuja y lee los niveles
/// de ese tramo en los bloques que la mezcla publicó por delante. Ninguno de los dos espera al otro más que una copia.
/// </para>
/// <para>
/// Lanzadera: a ×1 reproduce de verdad; a otras velocidades (×2 a ×8 o hacia atrás) recorre con seeks en pausa, uno
/// por refresco en cuanto el anterior muestra su cuadro, hacia la posición que corresponde a la velocidad pedida.
/// </para>
/// </remarks>
internal sealed partial class PlayerEngine : IPlaybackEngine, IDisposable
{
    private static readonly Func<LayerFrame, SourceGeometry> Geometry = l => new SourceGeometry(l.SampleAspectRatio, l.Rotation);
    private static readonly TimeSpan GpuSampleInterval = TimeSpan.FromMilliseconds(250);

    // Con calidad Auto se mide más seguido al reproducir: el p95 de un segundo necesita algo más que cuatro muestras.
    private static readonly TimeSpan AutoGpuSampleInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RmsWindow = TimeSpan.FromMilliseconds(300);
    private static readonly Func<TimelineSession, Time, Time, Time, AudioLevels> MeasureMix =
        static (session, from, to, rmsFloor) => session.MeasureAudio(from, to, RmsWindow, rmsFloor);

    private readonly bool _enableAudio;
    private readonly Metrics _metrics;
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly AutoResetEvent _wake = new(false);

    /// <summary>
    /// Tope de la espera en pausa: todo lo que cambia la composición despierta al hilo (órdenes, cuadros, títulos); lo
    /// periódico (cerrar decodificadores ociosos, expirar el pre-calentado) espera a lo sumo esto.
    /// </summary>
    private const int IdleWaitMs = 250;
    private readonly Thread _thread;
    private readonly Lock _pendingLock = new();
    private volatile bool _stop;
    private bool _disposed;
    private volatile PlaybackSnapshot _snapshot = PlaybackSnapshot.Empty;
    private SizeRequest? _pendingSize;
    private (Project Project, string SequenceId)? _pendingLoad;
    private Project? _pendingReload;
    private long _pendingReloadAt;

    private GpuDevice? _gpu;
    private TimelineSession? _session;
    private Compositor? _compositor;
    /// <summary>El gris de alrededor del lienzo, igual que <c>ViewerSurroundColor</c> de <c>Palette.xaml</c> (#242424).</summary>
    private static readonly (float R, float G, float B) SurroundColor = (0x24 / 255f, 0x24 / 255f, 0x24 / 255f);

    private Presenter? _presenter;
    private CompositionSwapChain? _swapChain;
    private GpuTimer? _gpuTimer;
    private PreviewProxies? _proxies;
    private ProxyUsage _proxyUsage = ProxyUsage.Auto;
    private IEffectContent? _content;
    private readonly ContentRefresh _contentRefresh;
    private Project? _project;
    private string? _sequenceId;
    private bool _gpuPending;
    private long _lastGpuBegin;
    private long _lastStats;
    private TimelineStats? _latestStats;
    private long _framesDropped;
    private bool _dirty = true;
    private int _lastSignature;
    private long _awaitingPresentSince;
    private long _resizeSince;
    private long _reloadSince;
    private bool _usesProxy;
    private int _shuttleRate;
    private Time _shuttleAnchor;
    private long _shuttleAnchorStamp;
    private double _zoom;
    private readonly PreviewQualityGovernor _quality = new();
    private int _previewDivisor = 1;
    private int _previewDowngrades;
    private long _editMark;
    private EffectTotals _effectTotals;
    private long _editMarkTaken;
    private long _editAppliedAt;
    private double _lastWaitMs;

    private readonly Lock _meterLock = new();
    private TimelineSession? _meterSession;
    private DynamicsMonitor? _dynamicsMonitor;
    private DynamicsMonitor? _dynamicsApplied;
    private TimelineSession? _dynamicsSession;
    private IReadOnlySet<string>? _bypassedInserts;
    private IReadOnlySet<string>? _bypassApplied;
    private TimelineSession? _bypassSession;
    private Time _meterPosition;
    private long _meterStamp;
    private bool _meterPlaying;

    // Solo del hilo de la interfaz (el único lector de niveles).
    private readonly AudioMeterReader _meterReader = new();

    public PlayerEngine(bool enableAudio, ProxyUsage proxyUsage, Metrics metrics)
    {
        _enableAudio = enableAudio;
        _proxyUsage = proxyUsage;
        _metrics = metrics;
        _contentRefresh = new ContentRefresh(() => Wake());
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

    /// <summary>Las cifras de la sesión tomadas en el último medio segundo (para medir); nulo sin sesión.</summary>
    public TimelineStats? LatestStats => Volatile.Read(ref _latestStats);

    /// <summary>El error que detuvo el motor, por si llegó antes de que alguien escuchara <see cref="Failed"/>.</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>Los proxies de la vista previa, cuando el hilo de render ya los creó; para la pestaña Trabajos.</summary>
    public Rodaje.Media.Proxies.MediaSourceResolver? ProxyResolver => Volatile.Read(ref _proxies)?.Resolver;

    /// <summary>VRAM dedicada de la GPU del reproductor; 0 mientras no abrió (la usa el presupuesto de los módulos).</summary>
    public long DedicatedVideoMemory => Volatile.Read(ref _gpu)?.DedicatedVideoMemory ?? 0;

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

        Wake();
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

        Wake();
    }

    public void Reload(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        lock (_pendingLock)
        {
            // Solo cuenta el último: si llegan varios entre dos refrescos, los anteriores ya no importan.
            _pendingReload = project;
            _pendingReloadAt = Stopwatch.GetTimestamp();
        }

        Wake();
    }

    public void Play() => Post(new Command(CommandKind.Play));

    public void Pause() => Post(new Command(CommandKind.Pause));

    public void TogglePlayPause() => Post(new Command(CommandKind.TogglePlay, Stopwatch.GetTimestamp()));

    public void Seek(Time position) => Post(new Command(CommandKind.Seek, Position: position));

    public void StepFrames(int frames) => Post(new Command(CommandKind.Step, Frames: frames));

    public void SetProxyUsage(ProxyUsage usage) => Post(new Command(CommandKind.Proxies, Proxies: usage));

    /// <summary>LUT y transiciones de paquete; si ya hay una secuencia abierta, se vuelve a abrir en la misma posición.</summary>
    public void SetContent(IEffectContent? content) => Post(new Command(CommandKind.Content, Content: content));

    /// <summary>Resolución de la vista previa: Auto, completa, mitad o cuarto.</summary>
    public void SetPreviewQuality(PreviewQuality quality) => Post(new Command(CommandKind.Quality, Quality: quality));

    /// <summary>Visor de 10 bits; se lee al crear cada cadena de intercambio, así que se fija antes del primer tamaño.</summary>
    public TenBitViewer TenBitViewer { get; set; }

    private bool UsesTenBits() => _gpu is not null && ViewerBitDepth.UsesTenBits(TenBitViewer, CompositionSwapChain.MaxBitsPerColor(_gpu));

    /// <summary>Efectos dibujados, saltados y contenido que faltó desde que se abrió el reproductor (para medir).</summary>
    public EffectTotals Effects
    {
        get
        {
            lock (_meterLock)
            {
                return _effectTotals;
            }
        }
    }

    /// <summary>Veces que la calidad Auto bajó la resolución al reproducir (para medir).</summary>
    public int PreviewDowngrades => Volatile.Read(ref _previewDowngrades);

    /// <summary>
    /// Marca el instante en que empieza una edición (antes de ejecutar el comando): el próximo proyecto que llegue con
    /// <see cref="Reload"/> mide desde aquí hasta que se presenta (<c>edicion.deslizador_a_present</c>).
    /// </summary>
    public void MarkEditStart(long timestamp) => Interlocked.Exchange(ref _editMark, timestamp);

    /// <summary>J, K o L: la velocidad siguiente se decide en el hilo de render, con el estado real del transporte.</summary>
    public void ShuttleKey(ShuttleKey key) => Post(new Command(CommandKind.Shuttle, Key: key));

    /// <summary>Escala de la imagen: 0 la ajusta al panel; 0,5 o 1 son píxeles de la secuencia por píxel físico.</summary>
    public void SetZoom(double zoom) => Post(new Command(CommandKind.Zoom, Zoom: zoom));

    /// <summary>
    /// Niveles de lo que sonó desde la llamada anterior: picos de ese tramo y RMS de los últimos 300 ms, leídos en la
    /// posición que suena ahora; en pausa, silencio. Solo desde el hilo de la interfaz; no espera al render ni reserva memoria.
    /// </summary>
    public AudioLevels TakeAudioLevels()
    {
        TimelineSession? session;
        Time position;
        long stamp;
        bool playing;
        lock (_meterLock)
        {
            session = _meterSession;
            position = _meterPosition;
            stamp = _meterStamp;
            playing = _meterPlaying;
        }

        return _meterReader.Take(session is { HasAudioLevels: true } ? session : null, position, playing, Stopwatch.GetElapsedTime(stamp), MeasureMix);
    }

    /// <summary>
    /// El compresor cuyo medidor se lee con <see cref="TakeDynamics"/> y, solo en la escucha, si suena su sidechain o sin
    /// comprimir; nulo, ninguno. Desde el hilo de la interfaz: el de render lo pasa a la sesión en su próxima vuelta.
    /// </summary>
    public void MonitorDynamics(DynamicsMonitor? monitor)
    {
        Volatile.Write(ref _dynamicsMonitor, monitor);
        Wake();
    }

    /// <summary>
    /// Inserciones que se escuchan sin efecto (A/B del mezclador, por su id); nula, ninguna. Solo cambia la vista previa.
    /// Desde el hilo de la interfaz: el de render lo pasa a la sesión en su próxima vuelta.
    /// </summary>
    public void BypassInserts(IReadOnlySet<string>? inserts)
    {
        Volatile.Write(ref _bypassedInserts, inserts);
        Wake();
    }

    /// <summary>
    /// Picos de los últimos 60 ms y RMS de los últimos 300 ms de una tira del mezclador (<see cref="DynamicsMonitor.ForTrack"/>,
    /// <see cref="DynamicsMonitor.ForBus"/>) en la posición que suena; nulo en pausa. Desde el hilo de la interfaz, sin reservas.
    /// </summary>
    public AudioLevels? MeasureStrip(string key)
    {
        if (CurrentHeard() is not { } heard)
        {
            return null;
        }

        return heard.Session.MeasureStrip(key, heard.Now - Time.FromSeconds(0.06), heard.Now, RmsWindow);
    }

    /// <summary>Picos (con el pico verdadero) de los últimos 60 ms de la mezcla que suena; nulo en pausa. No toca la lectura de los medidores.</summary>
    public AudioLevels? MeasureMixNow()
    {
        if (CurrentHeard() is not { } heard)
        {
            return null;
        }

        return heard.Session.MeasureAudio(heard.Now - Time.FromSeconds(0.06), heard.Now, RmsWindow);
    }

    private (TimelineSession Session, Time Now)? CurrentHeard()
    {
        TimelineSession? session;
        Time position;
        long stamp;
        bool playing;
        lock (_meterLock)
        {
            session = _meterSession;
            position = _meterPosition;
            stamp = _meterStamp;
            playing = _meterPlaying;
        }

        if (session is not { HasAudioLevels: true } || !playing)
        {
            return null;
        }

        return (session, position + Time.FromSeconds(Math.Min(0.1, Stopwatch.GetElapsedTime(stamp).TotalSeconds)));
    }

    /// <summary>
    /// Entrada, salida y reducción del compresor que se mira en los últimos 50 ms de lo que suena; nulo en pausa o sin datos.
    /// Solo desde el hilo de la interfaz; no espera al render ni reserva memoria.
    /// </summary>
    public Rodaje.Audio.Processing.DynamicsReading? TakeDynamics()
    {
        TimelineSession? session;
        Time position;
        long stamp;
        bool playing;
        lock (_meterLock)
        {
            session = _meterSession;
            position = _meterPosition;
            stamp = _meterStamp;
            playing = _meterPlaying;
        }

        if (session is not { HasAudioLevels: true } || !playing)
        {
            return null;
        }

        Time now = position + Time.FromSeconds(Math.Min(0.1, Stopwatch.GetElapsedTime(stamp).TotalSeconds));
        return session.MeasureDynamics(now - Time.FromSeconds(0.05), now);
    }

    /// <summary>
    /// Potencia ponderada K de lo que sonó en los últimos 400 ms (momentánea) y 3 s (corta), hasta la última lectura de
    /// <see cref="TakeAudioLevels"/>; nulo en pausa. Desde el mismo hilo que esa lectura.
    /// </summary>
    public (double Momentary, double ShortTerm)? TakeLoudness()
    {
        TimelineSession? session;
        lock (_meterLock)
        {
            session = _meterSession;
        }

        if (session is not { HasAudioLevels: true } || _meterReader.LastRead is not { } to)
        {
            return null;
        }

        Time floor = _meterReader.Floor;
        return (session.MeasureLoudnessPower(to, TimeSpan.FromMilliseconds(400), floor), session.MeasureLoudnessPower(to, TimeSpan.FromSeconds(3), floor));
    }

    /// <summary>
    /// Posición de la línea de tiempo que sonaba en el instante QPC <paramref name="qpc100ns"/> (unidades de 100 ns, como
    /// las marcas de WASAPI), desde la última posición publicada por el reloj del dispositivo. Nula en pausa o mientras el
    /// reloj no pasó de <paramref name="after"/> (la salida todavía no empezó a sonar). Desde cualquier hilo.
    /// </summary>
    public Time? HeardPositionAt(long qpc100ns, Time after)
    {
        Time position;
        long stamp;
        bool playing;
        lock (_meterLock)
        {
            position = _meterPosition;
            stamp = _meterStamp;
            playing = _meterPlaying;
        }

        if (!playing || position <= after)
        {
            return null;
        }

        double seconds = (qpc100ns / 1e7) - (stamp / (double)Stopwatch.Frequency);
        return position + Time.FromSeconds(seconds);
    }

    public void Dispose()
    {
        lock (_pendingLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _stop = true;
        _wake.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            _metrics.Note("el hilo de render no terminó en 3 s");
        }

        lock (_pendingLock)
        {
            _wake.Dispose();
        }
    }

    /// <summary>Despierta al hilo de render; tras liberar el motor no hace nada (un refresco de paquetes puede llegar después de cerrar).</summary>
    private void Wake()
    {
        lock (_pendingLock)
        {
            if (!_disposed)
            {
                _wake.Set();
            }
        }
    }

    private void Post(Command command)
    {
        _commands.Enqueue(command);
        Wake();
    }

    private void Run()
    {
        try
        {
            Initialize();
            while (!_stop)
            {
                ApplyResize();
                ApplyExternal();
                ApplyProject();
                ProcessCommands();
                ApplyContentChanges();
                ApplyTitleChanges();
                RunGpuWork();
                if (_session is { IsPlaying: false } && _quality.Paused())
                {
                    EnsureCompositor();
                    _dirty = true;
                }

                if (_session is null || _swapChain is null)
                {
                    PublishSnapshot();
                    _wake.WaitOne(IdleWaitMs);
                    continue;
                }

                bool active = _session.IsPlaying || _session.IsSeekPending || _dirty || IsScrubbing;
                if (active)
                {
                    // Uno por refresco: la cadena tiene latencia máxima 1, así que esto marca el ritmo del vsync.
                    long waitStart = Stopwatch.GetTimestamp();
                    _ = WaitForSingleObjectEx((HANDLE)_swapChain.FrameWaitHandle, 100, true);
                    _lastWaitMs = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;

                    // Una edición que llegó durante la espera entra en este mismo cuadro y no en el siguiente.
                    ApplyProject();
                    if (_session is null)
                    {
                        continue;
                    }

                    RenderFrame();
                }
                else
                {
                    _wake.WaitOne(IdleWaitMs);
                    using CompositionFrame frame = _session.Tick();
                    if (Signature(frame) != _lastSignature)
                    {
                        _dirty = true;
                    }
                }

                PumpScopes();
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

        _metrics.Milestone("ffmpeg_lista");

        _gpu = GpuDevice.TryCreate(out string? gpuFailure) ?? throw new InvalidOperationException("Hace falta una GPU con Direct3D 11. " + gpuFailure);
        DeviceDescription = _gpu.ToString();
        _presenter = new Presenter(_gpu) { Surround = SurroundColor };
        _gpuTimer = new GpuTimer(_gpu);
        CreateTitles();
        _metrics.Milestone("gpu_lista");
    }

    private void Shutdown()
    {
        _contentRefresh.Dispose();
        CloseSession();
        _compositor?.Dispose();
        _scopes?.Dispose();
        _looks?.Dispose();
        _transitionStills?.Dispose();
        _titles?.Dispose();
        _gpuTimer?.Dispose();
        _presenter?.Dispose();
        _swapChain?.Dispose();
        _external?.Dispose();
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
        lock (_meterLock)
        {
            _meterSession = null;
        }

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
            _swapChain = new CompositionSwapChain(_gpu, size.Width, size.Height, size.ScaleX, size.ScaleY, UsesTenBits());
            _metrics.Milestone("cadena_creada");
            if (_swapChain.TenBits)
            {
                _metrics.Note("visor de 10 bits (R10G10B10A2)");
            }
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
        long reloadAt;
        lock (_pendingLock)
        {
            load = _pendingLoad;
            reload = _pendingReload;
            reloadAt = _pendingReloadAt;
            _pendingLoad = null;
            _pendingReload = null;
        }

        if (load is { } next)
        {
            _project = next.Project;
            _sequenceId = next.SequenceId;

            // Otro proyecto (o secuencia): abre su sesión y suelta las texturas de los títulos del anterior.
            ProjectSwitch.Load(OpenSession, _titles);
        }

        if (reload is not null && _session is not null)
        {
            _project = reload;
            _editMarkTaken = Interlocked.Exchange(ref _editMark, 0);
            long applyStart = Stopwatch.GetTimestamp();
            try
            {
                _session.Reload(reload);
            }
            catch (KeyNotFoundException ex)
            {
                Warning?.Invoke(ex.Message);
            }

            UpdateMissingFonts();
            if (_editMarkTaken != 0)
            {
                _editAppliedAt = Stopwatch.GetTimestamp();
                _metrics.EditParts(
                    Stopwatch.GetElapsedTime(_editMarkTaken, reloadAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(reloadAt, applyStart).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(applyStart, _editAppliedAt).TotalMilliseconds);
            }

            EnsureCompositor();
            _dirty = true;
            _reloadSince = reloadAt;
        }
    }

    private bool IsScrubbing => _shuttleRate is not (0 or 1);

    private void OpenSession()
    {
        CloseSession();
        _framesDropped = 0;
        _usesProxy = false;
        _shuttleRate = 0;
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
                Content = _content,
                Titles = _titles,
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
        _session.FrameDelivered += (_, _) => Wake();
        _metrics.Milestone("sesion_abierta");
        _lastSignature = 0;
        UpdateMissingFonts();
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

            if (command.Kind == CommandKind.Content)
            {
                if (!ReferenceEquals(command.Content, _content))
                {
                    _content = command.Content;
                    _contentRefresh.Attach(_content);
                    if (_compositor is not null)
                    {
                        _compositor.Resources = _content;
                    }

                    if (_session is not null)
                    {
                        Time position = _session.Position;
                        OpenSession();
                        _session?.Seek(position);
                    }
                }

                continue;
            }

            if (command.Kind == CommandKind.Quality)
            {
                _quality.Quality = command.Quality;
                EnsureCompositor();
                _dirty = true;
                continue;
            }

            if (command.Kind == CommandKind.Zoom)
            {
                _zoom = command.Zoom;
                EnsureCompositor();
                _dirty = true;
                continue;
            }

            if (_session is null)
            {
                continue;
            }

            if (command.Kind == CommandKind.Shuttle)
            {
                ApplyShuttle(Shuttle.Next(EffectiveShuttleRate(_session), command.Key));
                _dirty = true;
                continue;
            }

            _shuttleRate = 0;
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

    private int EffectiveShuttleRate(TimelineSession session) => IsScrubbing ? _shuttleRate : session.IsPlaying ? 1 : 0;

    private void ApplyShuttle(int rate)
    {
        TimelineSession session = _session!;
        _shuttleRate = rate;
        if (rate == 1)
        {
            session.Play();
            return;
        }

        session.Pause();
        _shuttleAnchor = session.Position;
        _shuttleAnchorStamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Recorre a la velocidad de la lanzadera: un seek nuevo solo cuando el anterior ya muestra su cuadro.</summary>
    private void AdvanceScrub()
    {
        TimelineSession session = _session!;
        if (!IsScrubbing || session.IsSeekPending)
        {
            return;
        }

        double seconds = Stopwatch.GetElapsedTime(_shuttleAnchorStamp).TotalSeconds * _shuttleRate;
        Time target = _shuttleAnchor + Time.FromSeconds(seconds);
        if (target <= Time.Zero || target >= session.Duration)
        {
            target = target <= Time.Zero ? Time.Zero : session.Duration;
            _shuttleRate = 0;
        }

        if (target != session.Position)
        {
            session.Seek(target);
        }
    }

    /// <summary>
    /// Compone a tamaño de secuencia dividido por la calidad de la vista previa, y al menos a la mitad si el panel no
    /// llega ni a la mitad de la secuencia.
    /// </summary>
    private void EnsureCompositor()
    {
        if (_session is null || _swapChain is null || _gpu is null)
        {
            return;
        }

        int width = _session.Width;
        int height = _session.Height;
        bool halve = _zoom > 0 ? _zoom <= 0.5 : _swapChain.Width * 2 <= width && _swapChain.Height * 2 <= height;
        int divisor = Math.Max(halve ? 2 : 1, _quality.Divisor);
        _previewDivisor = _quality.Divisor;
        width = Math.Max(1, width / divisor);
        height = Math.Max(1, height / divisor);

        if (_compositor is { } current && current.Width == width && current.Height == height)
        {
            return;
        }

        _compositor?.Dispose();
        _compositor = null;
        _compositor = new Compositor(_gpu, width, height) { Resources = _content, Titles = _titles };
    }

    /// <summary>Una LUT terminó de cargar o cambiaron los paquetes: en pausa también hay que volver a componer.</summary>
    private void ApplyContentChanges()
    {
        ContentUpdate update = _contentRefresh.Take();
        if (update == ContentUpdate.None)
        {
            return;
        }

        if (update == ContentUpdate.Rebuild && _session is not null)
        {
            try
            {
                _session.Invalidate();
            }
            catch (KeyNotFoundException ex)
            {
                Warning?.Invoke(ex.Message);
            }
        }

        _dirty = true;
    }

    private void RenderFrame()
    {
        long start = Stopwatch.GetTimestamp();
        CollectGpuSample();
        AdvanceScrub();
        using CompositionFrame frame = _session!.Tick();
        long ticked = Stopwatch.GetTimestamp();
        int drawn = 0;
        if (_compositor is not null)
        {
            TimeSpan interval = _quality.Quality == PreviewQuality.Auto && _session.IsPlaying ? AutoGpuSampleInterval : GpuSampleInterval;
            bool measure = !_gpuPending && Stopwatch.GetElapsedTime(_lastGpuBegin) >= interval;
            if (measure)
            {
                _lastGpuBegin = Stopwatch.GetTimestamp();
                _gpuTimer!.Begin();
            }

            ApplyViewOptions(_compositor);
            CompositeResult result = _compositor.Render(frame, Geometry);
            drawn = result.LayersDrawn;
            PublishTitleBoxes(frame);
            CompleteCapture();
            SubmitScope(_compositor);
            lock (_meterLock)
            {
                _effectTotals = _effectTotals.Add(result);
            }
            if (measure)
            {
                _gpuTimer!.End();
                _gpuPending = true;
            }
        }

        _lastSignature = Signature(frame);
        // La misma cuenta que el contorno y las guías del visor (ViewerGeometry), con la forma de la secuencia y no la de la composición.
        PixelRect placement = ViewerGeometry.Image(_swapChain!.Width, _swapChain.Height, _session.Width, _session.Height, _zoom);
        _swapChain!.Render(_presenter!, _compositor, placement);
        PresentExternal();
        long presented = Stopwatch.GetTimestamp();
        _metrics.Presented(_session.IsPlaying && !_session.IsSeekPending, Stopwatch.GetElapsedTime(start, presented).TotalMilliseconds);
        _metrics.SlowRefresh(_lastWaitMs, Stopwatch.GetElapsedTime(start, ticked).TotalMilliseconds,
            Stopwatch.GetElapsedTime(ticked, presented).TotalMilliseconds);
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

        if (_reloadSince != 0)
        {
            _metrics.ReloadToPresent(Stopwatch.GetElapsedTime(_reloadSince, presented).TotalMilliseconds);
            _reloadSince = 0;
        }

        if (_editMarkTaken != 0)
        {
            _metrics.EditToPresent(Stopwatch.GetElapsedTime(_editMarkTaken, presented).TotalMilliseconds,
                Stopwatch.GetElapsedTime(_editAppliedAt, presented).TotalMilliseconds);
            _editMarkTaken = 0;
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
            if (_session is { IsPlaying: true } && _quality.Sample(ms, Stopwatch.GetElapsedTime(0)))
            {
                Volatile.Write(ref _previewDowngrades, _quality.Downgrades);
                _metrics.Note($"calidad Auto: la vista previa baja a 1/{_quality.Divisor} (p95 de GPU > {PreviewQualityGovernor.BudgetMs} ms)");
                EnsureCompositor();
            }
        }
    }

    private void PublishSnapshot()
    {
        if (_session is not { } s)
        {
            PlaybackSnapshot empty = PlaybackSnapshot.Empty with { Status = _gpu is null ? "Preparando el reproductor…" : "Sin secuencia" };
            if (!empty.Equals(_snapshot))
            {
                _snapshot = empty;
            }

            return;
        }

        if (Stopwatch.GetElapsedTime(_lastStats) >= StatsInterval)
        {
            _lastStats = Stopwatch.GetTimestamp();
            TimelineStats stats = s.Stats;
            Volatile.Write(ref _latestStats, stats);
            _framesDropped = stats.FramesDropped;
            _usesProxy = false;
            foreach (LayerStats layer in stats.Layers)
            {
                _usesProxy |= layer.UsesProxy;
            }
        }

        DynamicsMonitor? monitor = Volatile.Read(ref _dynamicsMonitor);
        IReadOnlySet<string>? bypassed = Volatile.Read(ref _bypassedInserts);
        if (!ReferenceEquals(s, _bypassSession) || !ReferenceEquals(bypassed, _bypassApplied))
        {
            s.BypassInserts(bypassed);
            _bypassSession = s;
            _bypassApplied = bypassed;
        }

        if (!ReferenceEquals(s, _dynamicsSession) || !Equals(monitor, _dynamicsApplied))
        {
            s.MonitorDynamics(monitor);
            _dynamicsSession = s;
            _dynamicsApplied = monitor;
        }

        Time position = s.Position;
        bool playing = s.IsPlaying;
        lock (_meterLock)
        {
            _meterSession = s;
            _meterPosition = position;
            _meterStamp = Stopwatch.GetTimestamp();
            _meterPlaying = playing;
        }

        string status = playing ? "Reproduciendo" : "En pausa";
        var next = new PlaybackSnapshot(position, s.Duration, playing, s.CurrentFrame, s.FrameCount, status)
        {
            FramesDropped = _framesDropped,
            UsesProxy = _usesProxy,
            ShuttleRate = EffectiveShuttleRate(s),
            PreviewDivisor = _previewDivisor,
        };
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
        Shuttle,
        Zoom,
        Quality,
        Content,
    }

    private readonly record struct Command(
        CommandKind Kind,
        long PostedAt = 0,
        Time Position = default,
        int Frames = 0,
        ProxyUsage Proxies = ProxyUsage.Auto,
        ShuttleKey Key = default,
        double Zoom = 0,
        PreviewQuality Quality = PreviewQuality.Auto,
        IEffectContent? Content = null);

    /// <summary>Sumas de <see cref="CompositeResult"/> en los cuadros compuestos.</summary>
    internal readonly record struct EffectTotals(long Frames, long Applied, long Skipped, long Missing)
    {
        public EffectTotals Add(CompositeResult result) =>
            new(Frames + 1, Applied + result.EffectsApplied, Skipped + result.EffectsSkipped, Missing + result.MissingContent);
    }

    private readonly record struct SizeRequest(int Width, int Height, float ScaleX, float ScaleY, long RequestedAt);
}
