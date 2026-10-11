using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>Decide qué cuadro de cada capa mostrar en cada refresco y mantiene la decodificación por delante, con pre-roll en los cortes.</summary>
/// <remarks>
/// <para>
/// Hilos: <see cref="Tick"/>, el transporte y <see cref="Reload"/> llegan del hilo de la ventana y nunca esperan a un
/// decodificador. Cada decodificador trabaja en su propio <see cref="IDecodeExecutor"/>, de a un pedido, y el resultado
/// se guarda en el hilo que lo terminó. Cerrar decodificadores y resolver proxies ocurre en otro ejecutor de servicio.
/// Todo el estado vive bajo un único candado que nunca se retiene mientras se decodifica, se abre o se cierra un archivo.
/// </para>
/// <para>
/// Demanda: en cada cuadro de la secuencia cada capa pide a su medio el cuadro debido y los siguientes
/// (<c>PrefetchFrames</c>), y el primer tramo del clip que sigue si el corte está a menos de <c>PreRoll</c>. Las
/// ventanas de un medio que caen cerca se agrupan en tramos; cada tramo va al decodificador de ese medio que quedó más
/// cerca (sin buscar keyframe, si se puede) y, si no hay uno libre, se abre otro, hasta <c>MaxDecodersPerMedia</c>.
/// </para>
/// <para>
/// VRAM: cada decodificador reserva su pool completo de D3D11VA (referencias, caché y cuadros retenidos) contra un
/// presupuesto. El tamaño de la caché de cada uno sale de ese presupuesto al crear el pool. Para abrir uno nuevo se
/// cierran primero los que nadie usa; el pre-roll y las pistas ocultas no pasan del presupuesto, una capa visible sin
/// decodificador sí (y se cuenta). Un decodificador sin demanda se cierra tras <c>DecoderIdleTimeout</c>, o antes si hay
/// muchos abiertos.
/// </para>
/// </remarks>
internal sealed partial class TimelineScheduler : IDisposable
{
    /// <summary>Superficies retenidas fuera de la caché: las de las capas en pantalla, la composición en vuelo y las del decodificador.</summary>
    internal const int HeldSurfaces = 5;

    /// <summary>Caché mínima de un decodificador cuando falta VRAM.</summary>
    internal const int MinCacheFrames = 4;

    /// <summary>VRAM propia de cada decodificador fuera del pool (búferes de NVDEC), en superficies: medida en 4K, unos 34 MB.</summary>
    internal const int OverheadSurfaces = 3;

    /// <summary>Más que cualquier pre-roll posible: lo de las pistas ocultas nunca adelanta a lo visible.</summary>
    private static readonly Time WarmPenalty = Time.FromSeconds(60);

    private const long SeekCost = 1L << 40;
    private const int ForwardReach = 64;
    private const int ReferenceEstimate = 17;
    private const int CompactReferenceEstimate = 8;
    private static readonly TimeSpan PoolWait = TimeSpan.FromMilliseconds(300);
    private const double StartupMargin = 2.5;
    private const double MaxPreRollSeconds = 2;

    private readonly Lock _gate = new();
    private readonly IAudioClock _clock;
    private readonly IClipBackend _backend;
    private readonly VideoDecoderOptions _decoderOptions;
    private readonly TimelineFrameCache _cache;
    private readonly TimeProvider _time;
    private readonly int _prefetch;
    private readonly int _preRollFrames;
    private readonly Time _preRoll;
    private readonly TimeSpan _idleTimeout;
    private readonly int _maxFramesPerSource;

    /// <summary>Más caché que lo adelantado, el pre-roll y dos de margen no se usa: lo demás ya se mostró.</summary>
    private readonly int _usefulCacheFrames;
    private readonly long _maxCacheBytes;
    private readonly int _maxDecodersPerMedia;
    private readonly long _vramBudget;
    private readonly ProxyUsage _proxyUsage;
    private readonly MediaSourceResolver? _resolver;
    private readonly IEffectContent? _content;
    private readonly IDecodeExecutor _service;
    private readonly Dictionary<SourceKey, MediaState> _media = [];
    private readonly Dictionary<int, MediaState> _mediaById = [];
    private readonly Dictionary<int, DecoderState> _decoders = [];
    private readonly Dictionary<string, LayerState> _layers = [];
    private readonly Func<int, long, long> _urgency;

    private TimelinePlan _plan;
    private int _nextId;
    private long _demandFrame = long.MinValue;
    private Time _demandTime;
    private bool _demandDirty;
    private long? _warmUntil;
    private bool _seekPending;
    private bool _endSignaled;
    private bool _disposed;
    private long _seeks;
    private long _decodeErrors;
    private int _decodersClosed;
    private long _vramPeak;
    private long _vramOverruns;
    private double _startupSeconds;
    private long _poolWaits;
    private double _poolWaitMs;

    public TimelineScheduler(TimelinePlan plan, IAudioClock clock, IClipBackend backend, VideoDecoderOptions decoderOptions,
        TimelineSessionOptions options, long vramBudget)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(decoderOptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vramBudget);
        _plan = plan;
        _clock = clock;
        _backend = backend;
        _decoderOptions = decoderOptions;
        _time = options.TimeProvider;
        _prefetch = options.PrefetchFrames;
        _preRollFrames = options.PreRollFrames;
        _preRoll = Time.FromSeconds(options.PreRoll.TotalSeconds);
        _idleTimeout = options.DecoderIdleTimeout;
        _maxFramesPerSource = options.MaxFramesPerSource;
        _usefulCacheFrames = Math.Min(options.MaxFramesPerSource, options.PrefetchFrames + options.PreRollFrames + 2);
        _maxCacheBytes = options.MaxCacheBytes;
        _maxDecodersPerMedia = options.MaxDecodersPerMedia;
        _vramBudget = vramBudget;
        _proxyUsage = options.UseProxies;
        _resolver = options.UseProxies == ProxyUsage.Never ? null : options.ProxyResolver;
        _content = options.Content;
        _cache = new TimelineFrameCache(options.MaxCacheBytes);
        _urgency = Urgency;
        _service = backend.CreateExecutor();
        if (_resolver is not null)
        {
            _resolver.StatusChanged += OnProxyStatusChanged;
        }

        List<(IDecodeExecutor, Action)> posts;
        lock (_gate)
        {
            _seekPending = true;
            posts = UpdateDemand(CurrentSequenceFrame(), force: true);
        }

        PostAll(posts);
    }

    /// <summary>La reproducción llegó al final de la secuencia y el reloj quedó en pausa. Llega desde <see cref="Tick"/>.</summary>
    public event EventHandler? EndReached;

    /// <summary>Un archivo no se pudo abrir o un cuadro no se pudo decodificar. Puede llegar desde un hilo de decodificación.</summary>
    public event EventHandler<TimelineDecodeFailedEventArgs>? DecodeFailed;

    public TimelinePlan Plan
    {
        get
        {
            lock (_gate)
            {
                return _plan;
            }
        }
    }

    public Time Position => _clock.Position;

    public bool IsPlaying => _clock.IsRunning;

    public bool IsSeekPending
    {
        get
        {
            lock (_gate)
            {
                return _seekPending;
            }
        }
    }

    public void Play()
    {
        Transition transition = default;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_plan.Duration <= Time.Zero)
            {
                return;
            }

            if (_clock.Position >= _plan.Duration)
            {
                transition = SeekLocked(Time.Zero);
            }

            _clock.Start();
        }

        transition.Finish();
    }

    public void Pause()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _clock.Pause();
        }
    }

    /// <summary>Lleva el reloj a <paramref name="position"/>, acotada a la secuencia, y descarta lo pendiente.</summary>
    public void Seek(Time position)
    {
        Transition transition;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            transition = SeekLocked(position);
        }

        transition.Finish();
    }

    /// <summary>Cambia la secuencia que se reproduce sin mover el reloj ni detenerlo.</summary>
    /// <remarks>
    /// Los cuadros guardados siguen valiendo para los clips que no cambiaron; las capas cuyo clip cambió esperan su
    /// cuadro nuevo. Con proxies, cada medio vuelve a mirar si su proxy está listo.
    /// </remarks>
    public void Reload(TimelinePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var released = new List<DecodedFrame>();
        List<(IDecodeExecutor, Action)> posts;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _plan = plan;
            var visible = plan.Lanes.Select(l => l.TrackId).ToHashSet();
            long now = _time.GetTimestamp();
            foreach (LayerState layer in _layers.Values)
            {
                if (visible.Contains(layer.TrackId))
                {
                    layer.HiddenSince = null;
                    continue;
                }

                // Al volver a verse empieza de cero en la posición de ese momento: lo que pasó oculta no es pérdida ni corte.
                layer.Release(released);
                layer.ActiveClip = null;
                layer.AwaitingCut = false;
                layer.Resync = true;
                layer.HiddenSince ??= now;
            }

            foreach (MediaState media in _media.Values)
            {
                media.Unavailable.Clear();
                media.Requested.Clear();
                media.ResolvePending |= _resolver is not null;
            }

            posts = UpdateDemand(CurrentSequenceFrame(), force: true);
        }

        Release(released, posts);
        PostAll(posts);
    }

    /// <summary>Elige la composición de este refresco. Nunca espera al decodificador: cada capa muestra lo más nuevo que tenga.</summary>
    public CompositionFrame Tick()
    {
        var released = new List<DecodedFrame>();
        var layers = new List<LayerFrame>();
        bool reachedEnd = false;
        CompositionFrame result;
        List<(IDecodeExecutor, Action)> posts;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Time position = _clock.Position;
            bool running = _clock.IsRunning;
            if (running && position >= _plan.Duration)
            {
                _clock.Pause();
                running = false;
                reachedEnd = !_endSignaled;
                _endSignaled = true;
            }

            Time time = _plan.FrameStart(position);
            bool warmExpired = _warmUntil is { } until && _time.GetTimestamp() >= until;
            posts = UpdateDemand(time.ToFrames(_plan.FrameRate), force: warmExpired || _demandDirty);
            bool settled = true;
            foreach (VideoLayerPlan layerPlan in _plan.Layers)
            {
                LayerState main = Step(layerPlan, time, running, released, ref settled);
                LayerState? incoming = layerPlan.Companion is { } companion ? Step(companion, time, running, released, ref settled) : null;
                if (Compose(layerPlan.TrackIndex, main, incoming, time) is { } layer)
                {
                    layers.Add(layer);
                }
            }

            if (_seekPending && settled)
            {
                _seekPending = false;
            }

            CollectIdle(posts);
            result = new CompositionFrame(time, _plan.Width, _plan.Height, layers);
        }

        Release(released, posts);
        PostAll(posts);
        if (reachedEnd)
        {
            EndReached?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public TimelineStats Stats(TimelineAudioStats audio)
    {
        lock (_gate)
        {
            var layers = _plan.Layers
                .Select(p => _layers.TryGetValue(p.TrackId, out LayerState? s) ? s.Snapshot(p.TrackIndex) : new LayerStats(p.TrackIndex, p.TrackId))
                .ToList();
            IEnumerable<LayerState> all = _layers.Values;
            List<DecoderState> open = [.. _decoders.Values.Where(d => !d.Closed && d.Decoder is not null)];
            return new TimelineStats(
                layers,
                all.Sum(l => l.Shown),
                all.Sum(l => l.Dropped),
                all.Sum(l => l.Late),
                all.Sum(l => l.Cuts),
                all.Sum(l => l.PreRollHits),
                all.Sum(l => l.PreRollMisses),
                all.Sum(l => l.LateAtCuts),
                open.Count,
                _decodersClosed,
                _cache.CostBytes,
                _cache.Count,
                _cache.BudgetBytes,
                _cache.Evictions,
                open.Sum(d => d.Decoder!.Statistics.Seeks),
                _decodeErrors,
                _seeks,
                audio)
            {
                VramBudgetBytes = _vramBudget,
                VramCommittedBytes = Committed(),
                VramPeakBytes = _vramPeak,
                VramOverruns = _vramOverruns,
                FailedMedia = _media.Values.Count(m => m.Failure is not null),
                ProxyDecoders = open.Count(d => d.Variant?.IsProxy == true),
                MissingContent = _plan.MissingContent,
                TransitionPreRollHits = all.Where(l => l.IsCompanion).Sum(l => l.PreRollHits),
                TransitionPreRollMisses = all.Where(l => l.IsCompanion).Sum(l => l.PreRollMisses),
                TransitionLateRefreshes = all.Where(l => l.IsCompanion).Sum(l => l.Late),
            };
        }
    }

    /// <summary>Veces que un decodificador esperó a que se liberaran otros pools para crear el suyo, y cuánto en total.</summary>
    internal (long Count, double Milliseconds) PoolWaits
    {
        get
        {
            lock (_gate)
            {
                return (_poolWaits, _poolWaitMs);
            }
        }
    }

    public void ResetStats()
    {
        lock (_gate)
        {
            foreach (LayerState layer in _layers.Values)
            {
                layer.ResetStats();
            }

            _vramPeak = Committed();
            _vramOverruns = 0;
        }
    }

    /// <summary>Cierra todos los decodificadores y espera a que terminen: después se puede liberar el dispositivo.</summary>
    public void Dispose()
    {
        var released = new List<DecodedFrame>();
        List<DecoderState> decoders;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (LayerState layer in _layers.Values)
            {
                layer.Release(released);
            }

            decoders = [.. _decoders.Values.Where(d => !d.Closed)];
            foreach (DecoderState decoder in decoders)
            {
                decoder.Closed = true;
            }

            _media.Clear();
            _mediaById.Clear();
            _cache.Clear();
        }

        if (_resolver is not null)
        {
            _resolver.StatusChanged -= OnProxyStatusChanged;
        }

        released.ForEach(f => f.Dispose());
        foreach (DecoderState decoder in decoders)
        {
            _service.Post(() => Reap(decoder));
        }

        (_service as IDisposable)?.Dispose();
    }

    /// <summary>Lleva cualquier giro en grados al múltiplo de 90 más cercano, entre 0 y 270.</summary>
    internal static int NormalizeRotation(int degrees)
    {
        int quarter = (int)Math.Round(degrees / 90.0, MidpointRounding.AwayFromZero);
        return ((quarter % 4) + 4) % 4 * 90;
    }
}
