using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
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
internal sealed class TimelineScheduler : IDisposable
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
            var visible = plan.Layers.Select(l => l.TrackId).ToHashSet();
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
                LayerState layer = Layer(layerPlan);
                VideoClipPlan? clip = layerPlan.ClipAt(time);
                if (clip is null)
                {
                    layer.Release(released);
                    layer.ActiveClip = null;
                    layer.AwaitingCut = false;
                    layer.Failure = null;
                    continue;
                }

                MediaState media = Media(clip);
                if (media.Failure is not null)
                {
                    // Sin su medio la capa queda vacía: mostrar el último cuadro del clip anterior sería mentir.
                    layer.Release(released);
                    layer.ActiveClip = clip;
                    layer.AwaitingCut = false;
                    layer.Failure = media.Failure;
                    continue;
                }

                layer.Failure = null;
                long due = clip.FrameAt(time);
                Present(layer, clip, media, due, running, released);
                bool exact = layer.IsShowing(media, due);
                bool missing = media.Unavailable.Contains(due);
                if (missing && !exact && layer.CurrentClip is { } stale && stale.ClipId != clip.ClipId)
                {
                    layer.Release(released);
                }

                settled &= exact || missing;
                if (running && !exact && !missing && !_seekPending)
                {
                    layer.Late++;
                    if (layer.AwaitingCut)
                    {
                        layer.LateAtCuts++;
                    }
                }

                if (exact)
                {
                    layer.AwaitingCut = false;
                }

                if (layer.Current is { } frame && layer.CurrentClip is { } shownClip && frame.TryRetain())
                {
                    LayerTransform transform = TransformPlan.Evaluate(shownClip.Transform, shownClip.ToSource(time));
                    Look look = layer.CurrentLook;
                    layers.Add(new LayerFrame(layerPlan.TrackIndex, shownClip.ClipId, frame, transform, look.SampleAspectRatio, look.Rotation)
                    {
                        DisplayWidth = look.DisplayWidth,
                        DisplayHeight = look.DisplayHeight,
                        IsProxy = look.IsProxy,
                    });
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

    // ---- Presentación ----

    private void Present(LayerState layer, VideoClipPlan clip, MediaState media, long due, bool running, List<DecodedFrame> released)
    {
        VideoClipPlan? active = layer.ActiveClip;
        if (active is null || !active.SameMapping(clip))
        {
            // Mismo clip con otros tiempos (una edición mientras suena) no es un corte: espera su cuadro exacto.
            bool edited = active?.ClipId == clip.ClipId;
            bool natural = running && !_seekPending && !layer.Resync && !edited;
            if (natural)
            {
                layer.Cuts++;
                if (_cache.Contains(media.Id, due))
                {
                    layer.PreRollHits++;
                }
                else
                {
                    layer.PreRollMisses++;
                    layer.AwaitingCut = true;
                }
            }

            layer.ActiveClip = clip;
            layer.LowerBound = natural ? clip.FirstFrame : due;
            layer.CountDrops = false;
        }
        else if (layer.Resync)
        {
            layer.LowerBound = due;
            layer.CountDrops = false;
        }

        layer.Resync = false;
        if (layer.CurrentClip?.ClipId == clip.ClipId && layer.CurrentMedia == media)
        {
            // Tras una edición el plan es otro objeto: la transformación del cuadro en pantalla sale del nuevo.
            layer.CurrentClip = clip;
        }

        // Si el medio cambió de archivo (llegó su proxy), el cuadro en pantalla se reemplaza aunque sea el debido.
        bool outdated = layer.CurrentMedia == media && layer.CurrentVariant != media.Variant;
        if (layer.IsShowing(media, due) && !outdated)
        {
            return;
        }

        if (due < layer.LowerBound)
        {
            // Volver atrás no es perder cuadros; reemplazar el mismo cuadro por el del archivo nuevo, tampoco.
            layer.CountDrops &= outdated && layer.Current is { } shown && shown.Index <= due;
            layer.LowerBound = due;
        }

        if (!_cache.TryGetLatest(media.Id, layer.LowerBound, due, out DecodedFrame? frame, out int slot))
        {
            return;
        }

        Variant? variant = _decoders.TryGetValue(slot, out DecoderState? source) ? source.Variant : null;
        if (outdated && variant != media.Variant && layer.Current?.Index == frame.Index)
        {
            // El mismo cuadro del archivo anterior: se sigue mostrando el que ya está hasta que llegue el nuevo.
            released.Add(frame);
            return;
        }

        if (layer.CountDrops && running && layer.CurrentMedia == media && layer.Current is { } previous)
        {
            layer.Dropped += clip.ShownBetween(previous.Index, frame.Index, _plan.FrameRate);
        }

        bool replacement = layer.CurrentMedia == media && layer.Current?.Index == frame.Index;
        layer.Release(released);
        layer.Current = frame;
        layer.CurrentClip = clip;
        layer.CurrentMedia = media;
        layer.CurrentLook = source?.Look ?? Look.Square;
        layer.CurrentVariant = variant;
        layer.Shown += replacement ? 0 : 1;
        layer.LowerBound = frame.Index + 1;
        layer.CountDrops = true;
    }

    private LayerState Layer(VideoLayerPlan plan)
    {
        if (!_layers.TryGetValue(plan.TrackId, out LayerState? layer))
        {
            layer = new LayerState(plan.TrackId) { Resync = true };
            _layers.Add(plan.TrackId, layer);
        }

        return layer;
    }

    private Transition SeekLocked(Time position)
    {
        Time last = _plan.Duration > Time.Zero ? _plan.Duration - Time.FromTicks(1) : Time.Zero;
        Time clamped = position < Time.Zero ? Time.Zero : position > last ? last : position;
        _clock.Seek(clamped);
        var stale = new List<CancellationTokenSource>();
        foreach (DecoderState decoder in _decoders.Values)
        {
            stale.Add(decoder.Epoch);
            decoder.Epoch = new CancellationTokenSource();
        }

        foreach (MediaState media in _media.Values)
        {
            media.Unavailable.Clear();
            media.Requested.Clear();
        }

        foreach (LayerState layer in _layers.Values)
        {
            layer.Resync = true;
            layer.AwaitingCut = false;
        }

        _seekPending = true;
        _endSignaled = false;
        _seeks++;
        return new Transition(stale, UpdateDemand(clamped.ToFrames(_plan.FrameRate), force: true));
    }

    private long CurrentSequenceFrame() => _plan.FrameStart(_clock.Position).ToFrames(_plan.FrameRate);

    /// <summary>
    /// Cuánto antes del corte empieza el pre-roll: lo pedido, o más si un decodificador nuevo tarda en entregar su primer
    /// cuadro (varias capas 4K se reparten el decodificador de la GPU), hasta 2 s.
    /// </summary>
    private Time PreRollLead => _preRoll <= Time.Zero || _startupSeconds <= 0
        ? _preRoll
        : Time.Max(_preRoll, Time.FromSeconds(Math.Min(MaxPreRollSeconds, StartupMargin * _startupSeconds)));

    // ---- Demanda ----

    /// <summary>Recalcula las ventanas, las reparte entre decodificadores y abre los que falten; devuelve el trabajo a encolar.</summary>
    private List<(IDecodeExecutor, Action)> UpdateDemand(long sequenceFrame, bool force)
    {
        var posts = new List<(IDecodeExecutor, Action)>();
        if (!force && sequenceFrame == _demandFrame)
        {
            return posts;
        }

        _demandDirty = false;
        _demandFrame = sequenceFrame;
        Time time = Time.FromFrames(sequenceFrame, _plan.FrameRate);
        _demandTime = time;
        foreach (MediaState media in _media.Values)
        {
            media.Windows.Clear();
            media.Layers = 0;
        }

        foreach (VideoLayerPlan layer in _plan.Layers)
        {
            if (layer.ClipAt(time) is { } clip)
            {
                long due = clip.FrameAt(time);
                long end = Math.Min(due + _prefetch, clip.LastFrame + 1);
                MediaState media = Media(clip);
                media.Layers++;
                media.Windows.Add(new Window(due, due + 1, Due: true, clip));
                if (end > due + 1)
                {
                    media.Windows.Add(new Window(due + 1, end, Due: false, clip));
                }
            }

            if (layer.NextAfter(time) is { } next && next.Start - time <= PreRollLead)
            {
                long first = next.FirstFrame;
                Media(next).Windows.Add(new Window(first, Math.Min(first + _preRollFrames, next.LastFrame + 1), Due: false, next));
            }
        }

        long now = _time.GetTimestamp();
        _warmUntil = null;
        foreach (VideoLayerPlan layer in _plan.HiddenLayers)
        {
            // Una pista recién ocultada sigue decodificando su tramo, detrás de todo lo visible, por si vuelve a verse.
            if (!_layers.TryGetValue(layer.TrackId, out LayerState? state) || state.HiddenSince is not { } since
                || _time.GetElapsedTime(since, now) >= _idleTimeout || layer.ClipAt(time) is not { } clip)
            {
                continue;
            }

            long due = clip.FrameAt(time);
            Media(clip).Windows.Add(new Window(due, Math.Min(due + _prefetch, clip.LastFrame + 1), Due: false, clip, Warm: true));
            long until = since + (long)(_idleTimeout.TotalSeconds * _time.TimestampFrequency);
            _warmUntil = _warmUntil is { } earlier ? Math.Min(earlier, until) : until;
        }

        var requests = new List<OpenRequest>();
        foreach (DecoderState decoder in _decoders.Values)
        {
            decoder.Assigned.Clear();
        }

        foreach (MediaState media in _media.Values)
        {
            if (media.Windows.Count == 0)
            {
                media.IdleSince ??= now;
                continue;
            }

            media.IdleSince = null;
            media.Requested.RemoveWhere(i => !media.InWindows(i));
            if (media.Failure is not null)
            {
                continue;
            }

            if (media.ResolvePending && !media.Resolving && _resolver is not null)
            {
                media.Resolving = true;
                int layers = media.Layers;
                posts.Add((_service, () => Reresolve(media, layers)));
            }

            Assign(media, requests);
        }

        foreach (DecoderState decoder in _decoders.Values)
        {
            if (!decoder.Closed && decoder.Assigned.Count == 0)
            {
                decoder.IdleSince ??= now;
            }
            else
            {
                decoder.IdleSince = null;
            }
        }

        // Lo más urgente primero; a igual urgencia, los medios sin ningún decodificador: los que ya tienen uno pueden
        // compartirlo si no queda lugar.
        requests.Sort((a, b) => a.Cluster.Urgency != b.Cluster.Urgency
            ? a.Cluster.Urgency.CompareTo(b.Cluster.Urgency)
            : a.Media.Decoders.Count.CompareTo(b.Media.Decoders.Count));
        foreach (OpenRequest request in requests)
        {
            Serve(request, posts);
        }

        foreach (DecoderState decoder in _decoders.Values)
        {
            if (!decoder.Closed && decoder.Decoder is not null && decoder.Assigned.Count > 0 && RequestPump(decoder))
            {
                posts.Add((decoder.Executor!, decoder.Pump));
            }
        }

        return posts;
    }

    /// <summary>Reparte los tramos del medio entre sus decodificadores, el más cercano primero; los que sobran piden uno nuevo.</summary>
    private void Assign(MediaState media, List<OpenRequest> requests)
    {
        List<Cluster> clusters = Clusters(media);
        var candidates = media.Decoders.Where(d => !d.Retiring).ToList();
        var pairs = new List<(Cluster Cluster, DecoderState Decoder, long Cost)>();
        foreach (Cluster cluster in clusters)
        {
            foreach (DecoderState decoder in candidates)
            {
                pairs.Add((cluster, decoder, Cost(decoder, cluster)));
            }
        }

        pairs.Sort((a, b) => a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost) : a.Cluster.Urgency.CompareTo(b.Cluster.Urgency));
        var served = new HashSet<Cluster>();
        var busy = new HashSet<DecoderState>();
        foreach ((Cluster cluster, DecoderState decoder, _) in pairs)
        {
            if (!served.Contains(cluster) && !busy.Contains(decoder))
            {
                decoder.Assigned.AddRange(cluster.Windows);
                cluster.Decoder = decoder;
                served.Add(cluster);
                busy.Add(decoder);
            }
        }

        foreach (Cluster cluster in clusters)
        {
            if (cluster.Decoder is null)
            {
                requests.Add(new OpenRequest(media, cluster));
            }

            // Mientras el decodificador nuevo (otro proxy u original) no tiene listo el cuadro que sigue, el que se retira
            // sigue con los debidos: no hay cortes, y el nuevo adelanta los siguientes sin competir por ellos.
            if ((cluster.Decoder is not { } replacement || !_cache.ContainsFrom(media.Id, cluster.Start + 1, replacement.Id))
                && media.Decoders.Where(d => d.Retiring && d.Decoder is not null).MinBy(d => Cost(d, cluster)) is { } bridge)
            {
                bridge.Assigned.AddRange(cluster.Windows.Where(w => w.Due));
            }
        }
    }

    /// <summary>Abre un decodificador para un tramo sin dueño, dentro del presupuesto; si no se puede, lo suma al más cercano.</summary>
    private void Serve(OpenRequest request, List<(IDecodeExecutor, Action)> posts)
    {
        MediaState media = request.Media;
        Cluster cluster = request.Cluster;
        int live = media.Decoders.Count(d => !d.Retiring);
        long estimate = Estimate(media);
        bool canOpen = live < _maxDecodersPerMedia && MakeRoom(estimate, posts);
        DecoderState? nearest = media.Decoders.Where(d => !d.Retiring).MinBy(d => Cost(d, cluster));
        if (!canOpen && nearest is not null && (cluster.Visible || Cost(nearest, cluster) < SeekCost || RestIsCached(nearest)))
        {
            // Compartir un decodificador que tendría que ir y volver con seeks solo vale para lo que se ve ya, o cuando
            // lo que le queda por mostrar de su clip ya cabe en su caché: si no, el pre-roll le quitaría a la capa visible
            // más de lo que le ahorra al corte.
            nearest.Assigned.AddRange(cluster.Windows);
            nearest.IdleSince = null;
            return;
        }

        if (!canOpen && !cluster.Visible)
        {
            return;
        }

        if (!canOpen)
        {
            _vramOverruns++;
        }

        var decoder = new DecoderState(_nextId++, media)
        {
            Overrun = !canOpen,
            OpenedAt = Stopwatch.GetTimestamp(),
            Opening = true,
            Variant = media.Variant,
            Position = cluster.Start - 1,
            Reserved = estimate,
        };
        decoder.Pump = () => Pump(decoder);
        decoder.Executor = _backend.CreateExecutor();
        decoder.Assigned.AddRange(cluster.Windows);
        _cache.SetSourceLimit(decoder.Id, _maxFramesPerSource);
        media.Decoders.Add(decoder);
        _decoders.Add(decoder.Id, decoder);
        TrackPeak();
        posts.Add((decoder.Executor, () => Open(decoder)));
    }

    /// <summary>Si cabe <paramref name="bytes"/> más en el presupuesto, cerrando primero los decodificadores sin demanda.</summary>
    private bool MakeRoom(long bytes, List<(IDecodeExecutor, Action)> posts)
    {
        long committed = Planned();
        if (committed + bytes <= _vramBudget)
        {
            return true;
        }

        var idle = _decoders.Values
            .Where(d => !d.Closed && d.Assigned.Count == 0 && !d.Opening && d.InFlight is null)
            .OrderByDescending(d => d.Retiring)
            .ThenBy(d => d.IdleSince ?? long.MaxValue)
            .ToList();
        long freed = idle.Sum(d => d.Reserved);
        if (committed - freed + bytes > _vramBudget)
        {
            return false;
        }

        foreach (DecoderState decoder in idle)
        {
            if (committed + bytes <= _vramBudget)
            {
                break;
            }

            committed -= decoder.Reserved;
            Close(decoder, posts);
        }

        return true;
    }

    /// <summary>Lo que le queda a cada clip que sirve el decodificador entra en su caché: puede irse a otro punto sin volver.</summary>
    private static bool RestIsCached(DecoderState decoder) =>
        decoder.Assigned.TrueForAll(w => w.Warm || w.Clip.LastFrame + 1 - w.Start < decoder.CacheLimit);

    private List<Cluster> Clusters(MediaState media)
    {
        var clusters = new List<Cluster>();
        Cluster? current = null;
        foreach (Window window in media.Windows.OrderBy(w => w.Start))
        {
            long urgency = Delay(window, window.Start);
            if (current is not null && window.Start <= current.End + _prefetch)
            {
                current.Add(window, urgency);
            }
            else
            {
                current = new Cluster(window, urgency);
                clusters.Add(current);
            }
        }

        return clusters;
    }

    /// <summary>Cuánto le cuesta al decodificador servir el tramo: 0 si ya está ahí, cuadros por delante si llega sin buscar, o un seek.</summary>
    private static long Cost(DecoderState decoder, Cluster cluster)
    {
        long next = decoder.Position + 1;
        if (next >= cluster.Start && next <= cluster.End)
        {
            return 0;
        }

        if (next < cluster.Start && cluster.Start - next <= ForwardReach)
        {
            return cluster.Start - next;
        }

        return SeekCost + Math.Abs(cluster.Start - next);
    }

    private MediaState Media(VideoClipPlan clip)
    {
        if (!_media.TryGetValue(clip.Source, out MediaState? media))
        {
            media = new MediaState(_nextId++, clip.Source, clip.FrameRate, clip.DisplayWidth, clip.DisplayHeight);
            _media.Add(clip.Source, media);
            _mediaById.Add(media.Id, media);
        }

        return media;
    }

    /// <summary>Más urgente cuanto antes haya que mostrarlo; <see cref="TimelineFrameCache.Unwanted"/> si ninguna ventana lo quiere.</summary>
    private long Urgency(int mediaId, long index)
    {
        long best = TimelineFrameCache.Unwanted;
        if (_mediaById.TryGetValue(mediaId, out MediaState? media))
        {
            foreach (Window window in media.Windows)
            {
                if (index >= window.Start && index < window.End)
                {
                    best = Math.Max(best, long.MaxValue - Delay(window, index));
                }
            }
        }

        return best;
    }

    /// <summary>Cuánto falta, en ticks desde el cuadro de la demanda, para que la capa muestre ese cuadro del origen.</summary>
    private long Delay(Window window, long index)
    {
        if (window.Due)
        {
            return 0;
        }

        VideoClipPlan clip = window.Clip;
        Time shownAt = Time.Max(clip.Start, clip.Start + (Time.FromFrames(index, clip.FrameRate) - clip.SourceIn));
        long delay = Math.Max(0, (shownAt - _demandTime).Ticks);
        return window.Warm ? WarmPenalty.Ticks + delay : delay;
    }

    /// <summary>Cierra los decodificadores sin demanda: ya si se retiran, antes de tiempo si hay muchos abiertos.</summary>
    private void CollectIdle(List<(IDecodeExecutor, Action)> posts)
    {
        long now = _time.GetTimestamp();
        int live = _decoders.Values.Count(d => !d.Closed);
        TimeSpan timeout = live > Math.Max(2, 2 * _plan.Layers.Count) ? _idleTimeout / 4 : _idleTimeout;
        foreach (DecoderState decoder in _decoders.Values.ToList())
        {
            if (!decoder.Closed && decoder.IdleSince is { } since && !decoder.Opening && decoder.InFlight is null
                && (decoder.Retiring || _time.GetElapsedTime(since, now) >= timeout))
            {
                Close(decoder, posts);
            }
        }

        foreach (MediaState media in _media.Values.ToList())
        {
            if (media.Decoders.Count == 0 && media.IdleSince is { } since && _time.GetElapsedTime(since, now) >= _idleTimeout
                && !_layers.Values.Any(l => l.CurrentMedia == media))
            {
                _media.Remove(media.Key);
                _mediaById.Remove(media.Id);
            }
        }
    }

    /// <summary>Saca el decodificador del reparto y de la caché, y encola su cierre en el ejecutor de servicio.</summary>
    private void Close(DecoderState decoder, List<(IDecodeExecutor, Action)> posts)
    {
        decoder.Closed = true;
        decoder.Assigned.Clear();
        decoder.Media.Decoders.Remove(decoder);
        _cache.RemoveSource(decoder.Id);
        _decodersClosed++;
        posts.Add((_service, () => Reap(decoder)));
    }

    // ---- VRAM ----

    /// <summary>VRAM de los pools ya creados, incluidos los de decodificadores que se están cerrando.</summary>
    private long Committed() => _decoders.Values.Where(d => d.PoolKnown).Sum(d => d.Reserved);

    /// <summary>Lo comprometido para decidir aperturas: los pools vivos y lo estimado de los que todavía abren.</summary>
    private long Planned() => _decoders.Values.Where(d => !d.Closed).Sum(d => d.Reserved);

    /// <summary>Uno por capa visible y, si alguna tiene cortes, dos más para el pre-roll.</summary>
    private int ExpectedDecoders()
    {
        int layers = Math.Max(1, _plan.Layers.Count);
        int withCuts = _plan.Layers.Count(l => l.Clips.Count > 1);
        return layers + Math.Min(2, withCuts);
    }

    private bool AnyClosingPool() => _decoders.Values.Any(d => d.Closed && d.PoolKnown && d.Reserved > 0);

    private void TrackPeak() => _vramPeak = Math.Max(_vramPeak, Committed());

    /// <summary>Lo mínimo que ocupará un decodificador nuevo del medio: referencias, superficies retenidas y la caché mínima.</summary>
    private long Estimate(MediaState media)
    {
        Variant? variant = media.Variant;
        if (media.LearnedSurfaceBytes > 0 && media.LearnedPath == variant?.Path)
        {
            return (media.LearnedReferences + HeldSurfaces + OverheadSurfaces + MinCacheFrames) * media.LearnedSurfaceBytes;
        }

        int width = variant?.Width > 0 ? variant.Width : media.DisplayWidth > 0 ? media.DisplayWidth : 1920;
        int height = variant?.Height > 0 ? variant.Height : media.DisplayHeight > 0 ? media.DisplayHeight : 1080;
        long surface = (long)((width + 15) & ~15) * ((height + 15) & ~15) * 3 / 2;
        int references = _decoderOptions.CompactReferencePool ? CompactReferenceEstimate : ReferenceEstimate;
        return (references + HeldSurfaces + OverheadSurfaces + MinCacheFrames) * surface;
    }

    /// <summary>
    /// Texturas extra del pool que el decodificador está por crear: las retenidas fuera de la caché y la caché que quepa
    /// en el presupuesto, repartido entre los decodificadores que se esperan (uno por capa visible y dos de pre-roll si hay cortes).
    /// </summary>
    /// <remarks>
    /// Corre en el hilo del decodificador, al crear el pool. Si no cabe porque otros pools todavía se están liberando,
    /// espera a que terminen (como mucho <see cref="PoolWait"/>): así el presupuesto acota la VRAM de verdad.
    /// </remarks>
    private int SizePool(DecoderState decoder, HardwarePoolRequest request)
    {
        long waitStart = Stopwatch.GetTimestamp();
        while (true)
        {
            lock (_gate)
            {
                if (decoder.Closed || _disposed)
                {
                    return HeldSurfaces + MinCacheFrames;
                }

                long surface = Math.Max(1, request.SurfaceBytes);
                long estimated = decoder.Reserved;
                long others = Planned() - estimated;
                int fixedSurfaces = request.ReferenceSurfaces + HeldSurfaces + OverheadSurfaces;
                int expected = Math.Max(_decoders.Values.Count(d => !d.Closed), ExpectedDecoders());
                long fit = ((_vramBudget - others) / surface) - fixedSurfaces;
                long share = (_vramBudget / expected / surface) - fixedSurfaces;
                long byCache = Math.Max(2, _maxCacheBytes / surface);
                int limit = (int)Math.Clamp(Math.Min(Math.Min(fit, share), byCache), MinCacheFrames, _usefulCacheFrames);
                long pool = (fixedSurfaces + limit) * surface;
                bool wait = Committed() + pool > _vramBudget && AnyClosingPool() && Stopwatch.GetElapsedTime(waitStart) < PoolWait;
                if (!wait)
                {
                    double waited = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
                    if (waited > 1)
                    {
                        _poolWaits++;
                        _poolWaitMs += waited;
                    }

                    return CommitPool(decoder, request, surface, limit, others + estimated <= _vramBudget && others + pool > _vramBudget);
                }
            }

            Thread.Sleep(2);
        }
    }

    private int CommitPool(DecoderState decoder, HardwarePoolRequest request, long surface, int limit, bool crossedBudget)
    {
        _cache.SetSourceLimit(decoder.Id, limit);
        decoder.CacheLimit = limit;
        decoder.Reserved = (request.ReferenceSurfaces + HeldSurfaces + OverheadSurfaces + limit) * surface;
        decoder.PoolKnown = true;
        if (!decoder.Overrun && crossedBudget)
        {
            // La estimación al abrir se quedó corta (el primer decodificador de un medio aún no se conoce).
            decoder.Overrun = true;
            _vramOverruns++;
        }

        MediaState media = decoder.Media;
        media.LearnedPath = decoder.Variant?.Path;
        media.LearnedReferences = request.ReferenceSurfaces;
        media.LearnedSurfaceBytes = surface;
        TrackPeak();
        return HeldSurfaces + limit;
    }

    // ---- Proxies ----

    private void OnProxyStatusChanged(object? sender, ProxyStatusChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            bool ready = e.Status.State == ProxyState.Ready;
            foreach (MediaState media in _media.Values)
            {
                if (media.Variant is { } variant && variant.IsProxy != ready
                    && string.Equals(media.Key.Path, e.Status.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    media.ResolvePending = true;
                    _demandDirty = true;
                }
            }
        }
    }

    /// <summary>En el ejecutor de servicio: vuelve a resolver el medio y, si cambió de archivo, retira sus decodificadores.</summary>
    private void Reresolve(MediaState media, int layers)
    {
        Variant variant = ResolveVariant(media, layers);
        lock (_gate)
        {
            media.Resolving = false;
            media.ResolvePending = false;
            if (_disposed || media.Failure is not null || media.Variant == variant)
            {
                return;
            }

            media.Variant = variant;
            foreach (DecoderState decoder in media.Decoders)
            {
                // Lo que ya adelantó el archivo anterior sigue sirviendo hasta que el nuevo decodifique los mismos cuadros.
                decoder.Retiring |= decoder.Variant is not null && decoder.Variant != variant;
            }

            _demandDirty = true;
        }
    }

    /// <summary>Qué archivo decodificar para la vista previa del medio. Lee el disco la primera vez: nunca bajo el candado.</summary>
    private Variant ResolveVariant(MediaState media, int layers)
    {
        var original = new Variant(media.Key.Path, IsProxy: false, media.DisplayWidth, media.DisplayHeight);
        if (_resolver is null)
        {
            return original;
        }

        try
        {
            MediaSourceResolution resolution = _resolver.Resolve(media.Key.Path, MediaPurpose.Preview);
            if (resolution.Proxy is not { } proxy || !ProxyMatches(proxy.Metadata, media))
            {
                return original;
            }

            if (_proxyUsage == ProxyUsage.Auto && !Recommended(resolution.Status, media, layers))
            {
                return original;
            }

            return new Variant(proxy.ProxyPath, IsProxy: true, proxy.Metadata.Width, proxy.Metadata.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
        {
            return original;
        }
    }

    /// <summary>El cuadro n del proxy es el n del original: misma tasa y los mismos cuadros.</summary>
    private static bool ProxyMatches(ProxyMetadata metadata, MediaState media) =>
        metadata.FrameRate == media.FrameRate && metadata.FrameCount == metadata.SourceFrameCount;

    private bool Recommended(ProxyStatus status, MediaState media, int layers)
    {
        if (status.Recommendation?.IsRecommended == true)
        {
            return true;
        }

        return media.DisplayWidth > 0 && media.DisplayHeight > 0 && _resolver!.Policy.Evaluate(
            new ProxyCandidate(media.DisplayWidth, media.DisplayHeight, "", 8, media.FrameRate, Math.Max(1, layers))).IsRecommended;
    }

    /// <summary>Le cuenta a la política de proxies cómo es el original, lo que puede encolar su proxy; una vez por medio.</summary>
    private void Assess(MediaInfo info, int layers)
    {
        try
        {
            _resolver!.Assess(info, layers: Math.Max(1, layers));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or ObjectDisposedException)
        {
        }
    }

    // ---- Decodificación ----

    private void Open(DecoderState decoder)
    {
        MediaState media = decoder.Media;
        Variant? variant;
        int layers;
        lock (_gate)
        {
            variant = decoder.Variant ?? media.Variant;
            layers = media.Layers;
        }

        if (variant is null)
        {
            Variant resolved = ResolveVariant(media, layers);
            lock (_gate)
            {
                media.Variant ??= resolved;
                variant = media.Variant;
            }
        }

        lock (_gate)
        {
            decoder.Variant = variant;
        }

        VideoDecoderOptions options = _decoderOptions with { HardwarePoolSizer = request => SizePool(decoder, request) };
        IClipVideo? video = null;
        Exception? error = null;
#pragma warning disable CA1031 // Un archivo que no abre deja su capa vacía; se informa y la reproducción sigue.
        try
        {
            video = _backend.OpenVideo(variant.Path, options);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (error is not null && variant.IsProxy)
        {
            // Un proxy dañado no deja la capa vacía: se vuelve al original.
            variant = new Variant(media.Key.Path, IsProxy: false, media.DisplayWidth, media.DisplayHeight);
            lock (_gate)
            {
                media.Variant = variant;
                decoder.Variant = variant;
            }

            try
            {
                video = _backend.OpenVideo(variant.Path, options);
                error = null;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }
#pragma warning restore CA1031

        bool discard;
        bool post = false;
        bool assess = false;
        List<(IDecodeExecutor, Action)> posts = [];
        lock (_gate)
        {
            decoder.Opening = false;
            discard = _disposed || decoder.Closed;
            if (!discard && error is not null)
            {
                media.Failure = error.Message;
                _decodeErrors++;
                Close(decoder, posts);
            }
            else if (!discard && video is not null)
            {
                decoder.Decoder = video;
                decoder.Look = variant.IsProxy
                    ? new Look(new Rational(1, 1), 0, media.DisplayWidth, media.DisplayHeight, IsProxy: true)
                    : new Look(video.Stream.SampleAspectRatio, NormalizeRotation(video.Stream.Rotation), 0, 0, IsProxy: false);
                if (!decoder.PoolKnown)
                {
                    decoder.CacheLimit = PlanSourceLimit(video);
                    _cache.SetSourceLimit(decoder.Id, decoder.CacheLimit);
                }

                assess = !variant.IsProxy && _resolver is not null && !media.Assessed;
                media.Assessed |= assess;
                post = RequestPump(decoder);
            }
        }

        if (discard)
        {
            video?.Dispose();
            return;
        }

        PostAll(posts);
        if (error is not null)
        {
            DecodeFailed?.Invoke(this, new TimelineDecodeFailedEventArgs(media.Key.MediaId, media.Key.Path, -1, error));
        }

        if (assess)
        {
            Assess(video!.Info, layers);
        }

        if (post)
        {
            decoder.Executor!.Post(decoder.Pump);
        }
    }

    /// <summary>Tope de cuadros sin pool de D3D11VA (CPU): lo que pide la opción, sin que uno solo se coma la caché.</summary>
    private int PlanSourceLimit(IClipVideo video)
    {
        VideoStreamInfo stream = video.Stream;
        long frame = Math.Max(1, (long)stream.Width * stream.Height * (stream.BitDepth > 8 ? 2 : 1) * 3 / 2);
        return (int)Math.Clamp(_maxCacheBytes / frame, 2, _maxFramesPerSource);
    }

    private static bool RequestPump(DecoderState decoder)
    {
        if (decoder.PumpPosted || decoder.InFlight is not null)
        {
            return false;
        }

        decoder.PumpPosted = true;
        return true;
    }

    // Se pide primero lo que antes haya que mostrar, sea de la capa que sea. Un cuadro adelantado que la caché ya
    // descartó por falta de lugar se vuelve a pedir solo cuando ahora sí cabría: si no, decodificarlo expulsaría
    // otro y así sin fin. Y mientras no quepa, tampoco se adelantan los que siguen: decodificarlos obligaría a volver
    // atrás con un seek por el que faltó. El cuadro debido siempre se repide.
    private bool TryFindMissing(DecoderState decoder, out long index)
    {
        MediaState media = decoder.Media;
        index = 0;
        long bestDelay = long.MaxValue;
        foreach (Window window in decoder.Assigned)
        {
            for (long i = window.Start; i < window.End; i++)
            {
                long delay = Delay(window, i);
                if (delay >= bestDelay || media.Unavailable.Contains(i) || IsCachedFor(decoder, i) || IsShown(decoder, i)
                    || InFlightElsewhere(decoder, i))
                {
                    continue;
                }

                if (!window.Due && media.Requested.Contains(i) && !_cache.CanAdmit(decoder.Id, long.MaxValue - delay, _urgency))
                {
                    break;
                }

                (index, bestDelay) = (i, delay);
            }
        }

        return bestDelay != long.MaxValue;
    }

    /// <summary>El cuadro está guardado y le sirve a <paramref name="decoder"/>: no salió de un archivo que se está retirando.</summary>
    private bool IsCachedFor(DecoderState decoder, long index) =>
        _cache.TryGetSlot(decoder.Media.Id, index, out int slot)
        && (decoder.Retiring || !_decoders.TryGetValue(slot, out DecoderState? source) || !source.Retiring);

    private static bool InFlightElsewhere(DecoderState decoder, long index)
    {
        foreach (DecoderState other in decoder.Media.Decoders)
        {
            if (other != decoder && other.InFlight?.Index == index)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Alguna capa ya muestra ese cuadro, decodificado del mismo archivo que usa <paramref name="decoder"/>.</summary>
    private bool IsShown(DecoderState decoder, long index)
    {
        foreach (LayerState layer in _layers.Values)
        {
            if (layer.IsShowing(decoder.Media, index) && layer.CurrentVariant == decoder.Variant)
            {
                return true;
            }
        }

        return false;
    }

    private void Pump(DecoderState decoder)
    {
        while (true)
        {
            Job? job = null;
            IClipVideo? video = null;
            List<(IDecodeExecutor, Action)> posts = [];
            lock (_gate)
            {
                decoder.PumpPosted = false;
                if (_disposed || decoder.Closed || decoder.Decoder is null || decoder.InFlight is not null)
                {
                    return;
                }

                if (_clock.IsRunning)
                {
                    // Sin esperar al próximo refresco: lo que el reloj ya dejó atrás no se decodifica.
                    long now = CurrentSequenceFrame();
                    if (now > _demandFrame)
                    {
                        posts = UpdateDemand(now, force: false);
                        posts.RemoveAll(p => p.Item2 == decoder.Pump);
                        decoder.PumpPosted = false;
                    }
                }

                if (!decoder.Closed && TryFindMissing(decoder, out long index))
                {
                    decoder.Media.Requested.Add(index);
                    job = decoder.InFlight = new Job(index, decoder.Epoch);
                    decoder.Position = index;
                    video = decoder.Decoder;
                }
            }

            PostAll(posts);
            if (job is null || video is null)
            {
                return;
            }

            ValueTask<DecodedFrame?> pending;
            try
            {
                pending = video.GetFrameAsync(job.Index, job.Epoch.Token);
            }
            catch (Exception ex)
            {
                pending = ValueTask.FromException<DecodedFrame?>(ex);
            }

            if (!pending.IsCompleted)
            {
                _ = pending.AsTask().ContinueWith(
                    task =>
                    {
                        if (Complete(decoder, job, new ValueTask<DecodedFrame?>(task)))
                        {
                            decoder.Executor!.Post(decoder.Pump);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            Complete(decoder, job, pending);
        }
    }

    private bool Complete(DecoderState decoder, Job job, ValueTask<DecodedFrame?> pending)
    {
        DecodedFrame? frame = null;
        Exception? error = null;
        try
        {
            frame = pending.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (job.Epoch.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }

        MediaState media = decoder.Media;
        DecodedFrame? discard = null;
        bool post;
        lock (_gate)
        {
            decoder.InFlight = null;
            bool current = job.Epoch == decoder.Epoch;
            if (_disposed || decoder.Closed)
            {
                discard = frame;
            }
            else if (frame is not null && frame.Index != job.Index)
            {
                discard = frame;
                error = new InvalidOperationException($"El decodificador devolvió el cuadro {frame.Index} cuando se pidió el {job.Index}.");
            }
            else if (frame is not null)
            {
                if (!decoder.Retiring && _cache.TryGetSlot(media.Id, frame.Index, out int slot) && slot != decoder.Id
                    && _decoders.TryGetValue(slot, out DecoderState? older) && older.Retiring)
                {
                    // El mismo cuadro del archivo nuevo reemplaza al del que se retira.
                    _cache.Remove(media.Id, frame.Index);
                }

                _cache.Add(media.Id, decoder.Id, frame, _urgency);

                // Un decodificador que reemplaza a otro ya entrega: puede que el que se retira deje de hacer falta.
                _demandDirty |= !decoder.Retiring && media.Decoders.Exists(d => d.Retiring);
                if (decoder.Delivered++ == 0)
                {
                    // Lo que tarda un decodificador nuevo en entregar, promediado: de eso depende cuánto antes empezar el pre-roll.
                    double startup = Stopwatch.GetElapsedTime(decoder.OpenedAt).TotalSeconds;
                    _startupSeconds = _startupSeconds <= 0 ? startup : (0.7 * _startupSeconds) + (0.3 * startup);
                    if (!decoder.PoolKnown)
                    {
                        // Sin pool de D3D11VA (decodificación en la CPU): no ocupa VRAM de decodificador.
                        decoder.Reserved = 0;
                        decoder.PoolKnown = true;
                    }


                }
            }

            if (!_disposed && !decoder.Closed && current && (frame is null || error is not null))
            {
                media.Unavailable.Add(job.Index);
            }

            if (!_disposed && error is not null)
            {
                _decodeErrors++;
            }

            post = !_disposed && !decoder.Closed && RequestPump(decoder);
        }

        discard?.Dispose();
        if (error is not null)
        {
            DecodeFailed?.Invoke(this, new TimelineDecodeFailedEventArgs(media.Key.MediaId, media.Key.Path, job.Index, error));
        }

        return post;
    }

    /// <summary>
    /// Suelta cuadros en el ejecutor de servicio: el último cuadro de un decodificador cerrado arrastra la destrucción de su
    /// pool de texturas, que no debe caer en el hilo de la ventana.
    /// </summary>
    private void Release(List<DecodedFrame> frames, List<(IDecodeExecutor, Action)> posts)
    {
        if (frames.Count > 0)
        {
            posts.Add((_service, () => frames.ForEach(f => f.Dispose())));
        }
    }

    private static void PostAll(List<(IDecodeExecutor Executor, Action Work)> posts)
    {
        foreach ((IDecodeExecutor executor, Action work) in posts)
        {
            executor.Post(work);
        }
    }

    /// <summary>
    /// En el ejecutor de servicio: libera el decodificador en su propio hilo, después de lo que ya tenía en cola, espera a
    /// que ese hilo termine y recién entonces deja de contar su VRAM. El hilo de la ventana nunca espera esto.
    /// </summary>
    private void Reap(DecoderState decoder)
    {
        if (decoder.Executor is { } executor)
        {
            executor.Post(() =>
            {
                IClipVideo? video;
                lock (_gate)
                {
                    video = decoder.Decoder;
                    decoder.Decoder = null;
                }

                video?.Dispose();
            });
            (executor as IDisposable)?.Dispose();
        }

        lock (_gate)
        {
            decoder.Reserved = 0;
            _decoders.Remove(decoder.Id);
        }
    }

    private sealed record Job(long Index, CancellationTokenSource Epoch);

    /// <summary>Lo que un seek deja para después de soltar el candado: cancelar lo viejo y encolar lo nuevo.</summary>
    private readonly record struct Transition(List<CancellationTokenSource>? Stale, List<(IDecodeExecutor, Action)>? Posts)
    {
        public void Finish()
        {
            Stale?.ForEach(c => c.Cancel());
            if (Posts is not null)
            {
                PostAll(Posts);
            }
        }
    }

    /// <summary>Cuadros <c>[Start, End)</c> del origen que necesita un clip; <see cref="Due"/> es el que toca mostrar ya.</summary>
    /// <remarks><see cref="Warm"/>: de una pista oculta; va detrás de todo lo visible y es lo primero que la caché descarta.</remarks>
    private readonly record struct Window(long Start, long End, bool Due, VideoClipPlan Clip, bool Warm = false);

    /// <summary>Archivo que decodifica la vista previa de un medio, con el tamaño que se espera de sus cuadros.</summary>
    private sealed record Variant(string Path, bool IsProxy, int Width, int Height);

    /// <summary>Cómo dibujar los cuadros de un decodificador: forma del píxel, giro y, para un proxy, el tamaño del original.</summary>
    private readonly record struct Look(Rational SampleAspectRatio, int Rotation, int DisplayWidth, int DisplayHeight, bool IsProxy)
    {
        public static Look Square => new(new Rational(1, 1), 0, 0, 0, IsProxy: false);
    }

    private sealed record OpenRequest(MediaState Media, Cluster Cluster);

    /// <summary>Ventanas de un medio lo bastante juntas para que un solo decodificador las sirva avanzando.</summary>
    private sealed class Cluster
    {
        public Cluster(Window first, long urgency)
        {
            Start = first.Start;
            End = first.End;
            Urgency = urgency;
            Visible = !first.Warm && first.Due;
            Windows.Add(first);
        }

        public long Start { get; }

        public long End { get; private set; }

        /// <summary>Retraso de lo más urgente del tramo: 0 si hay un cuadro debido.</summary>
        public long Urgency { get; private set; }

        /// <summary>Tiene el cuadro debido de una capa visible.</summary>
        public bool Visible { get; private set; }

        public List<Window> Windows { get; } = [];

        public DecoderState? Decoder { get; set; }

        public void Add(Window window, long urgency)
        {
            Windows.Add(window);
            End = Math.Max(End, window.End);
            Urgency = Math.Min(Urgency, urgency);
            Visible |= window.Due && !window.Warm;
        }
    }

    private sealed class MediaState(int id, SourceKey key, Rational frameRate, int displayWidth, int displayHeight)
    {
        public int Id { get; } = id;

        public SourceKey Key { get; } = key;

        public Rational FrameRate { get; } = frameRate;

        public int DisplayWidth { get; } = displayWidth;

        public int DisplayHeight { get; } = displayHeight;

        public List<DecoderState> Decoders { get; } = [];

        public string? Failure { get; set; }

        public List<Window> Windows { get; } = [];

        /// <summary>Capas visibles que muestran el medio ahora.</summary>
        public int Layers { get; set; }

        public HashSet<long> Requested { get; } = [];

        public HashSet<long> Unavailable { get; } = [];

        public long? IdleSince { get; set; }

        /// <summary>Archivo elegido (proxy u original); nulo hasta resolverlo por primera vez.</summary>
        public Variant? Variant { get; set; }

        public bool ResolvePending { get; set; }

        public bool Resolving { get; set; }

        public bool Assessed { get; set; }

        public string? LearnedPath { get; set; }

        public int LearnedReferences { get; set; }

        public long LearnedSurfaceBytes { get; set; }

        public bool InWindows(long index) => Windows.Exists(w => index >= w.Start && index < w.End);
    }

    private sealed class DecoderState(int id, MediaState media)
    {
        public int Id { get; } = id;

        public MediaState Media { get; } = media;

        public Action Pump { get; set; } = () => { };

        public IClipVideo? Decoder { get; set; }

        public IDecodeExecutor? Executor { get; set; }

        public Variant? Variant { get; set; }

        public Look Look { get; set; } = Look.Square;

        public bool Opening { get; set; }

        public bool Closed { get; set; }

        /// <summary>Su archivo ya no es el elegido para el medio: sirve hasta que el nuevo entregue y se cierra.</summary>
        public bool Retiring { get; set; }

        public List<Window> Assigned { get; } = [];

        /// <summary>Último cuadro pedido: de dónde sigue sin buscar keyframe.</summary>
        public long Position { get; set; }

        public long Delivered { get; set; }

        /// <summary>Cuadros que puede guardar en la caché.</summary>
        public int CacheLimit { get; set; } = MinCacheFrames;

        public long OpenedAt { get; init; }

        /// <summary>VRAM de su pool: estimada al abrir, exacta desde que se crea.</summary>
        public long Reserved { get; set; }

        public bool PoolKnown { get; set; }

        /// <summary>Su pool no cabía en el presupuesto y se abrió igual porque una capa visible no tenía otro.</summary>
        public bool Overrun { get; set; }

        // Sin temporizadores un CTS no retiene recursos; el reemplazado no se libera porque la
        // decodificación en curso todavía puede consultar su token.
        public CancellationTokenSource Epoch { get; set; } = new();

        public Job? InFlight { get; set; }

        public bool PumpPosted { get; set; }

        public long? IdleSince { get; set; }
    }

    private sealed class LayerState(string trackId)
    {
        public string TrackId { get; } = trackId;

        public DecodedFrame? Current { get; set; }

        public VideoClipPlan? CurrentClip { get; set; }

        public MediaState? CurrentMedia { get; set; }

        public Look CurrentLook { get; set; } = Look.Square;

        /// <summary>Archivo del que salió el cuadro en pantalla.</summary>
        public Variant? CurrentVariant { get; set; }

        /// <summary>Clip que cubría el instante en el último refresco.</summary>
        public VideoClipPlan? ActiveClip { get; set; }

        public long LowerBound { get; set; }

        public bool CountDrops { get; set; }

        /// <summary>Tras un seek o al aparecer la capa: el próximo cuadro debe ser el exacto y no cuenta como corte.</summary>
        public bool Resync { get; set; }

        /// <summary>El corte llegó sin su primer cuadro listo; los refrescos hasta tenerlo cuentan como tarde en el corte.</summary>
        public bool AwaitingCut { get; set; }

        /// <summary>Desde cuándo está oculta la pista; nulo mientras se ve.</summary>
        public long? HiddenSince { get; set; }

        /// <summary>Motivo por el que el medio del clip activo no abre; la capa queda vacía.</summary>
        public string? Failure { get; set; }

        public long Shown { get; set; }

        public long Dropped { get; set; }

        public long Late { get; set; }

        public long Cuts { get; set; }

        public long PreRollHits { get; set; }

        public long PreRollMisses { get; set; }

        public long LateAtCuts { get; set; }

        public bool IsShowing(MediaState media, long index) => CurrentMedia == media && Current?.Index == index;

        public void Release(List<DecodedFrame> released)
        {
            if (Current is not null)
            {
                released.Add(Current);
            }

            Current = null;
            CurrentClip = null;
            CurrentMedia = null;
            CurrentLook = Look.Square;
            CurrentVariant = null;
        }

    
    public void ResetStats() => (Shown, Dropped, Late, Cuts, PreRollHits, PreRollMisses, LateAtCuts) = (0, 0, 0, 0, 0, 0, 0);

        public LayerStats Snapshot(int trackIndex) =>
            new(trackIndex, TrackId, Shown, Dropped, Late, Cuts, PreRollHits, PreRollMisses, LateAtCuts,
                Current is not null && CurrentLook.IsProxy, Failure);
    }
}
