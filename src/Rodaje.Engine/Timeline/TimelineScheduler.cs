using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>Decide qué cuadro de cada capa mostrar en cada refresco y mantiene la decodificación por delante, con pre-roll en los cortes.</summary>
/// <remarks>
/// <para>
/// Hilos: <see cref="Tick"/>, el transporte y <see cref="Reload"/> llegan del hilo de la ventana; cada origen
/// decodifica en su propio <see cref="IDecodeExecutor"/>, de a un pedido, y el resultado se guarda en el hilo
/// que lo terminó. Todo el estado vive bajo un único candado que nunca se retiene mientras se decodifica o se
/// abre un archivo.
/// </para>
/// <para>
/// Demanda: en cada cuadro de la secuencia cada capa pide a su origen el cuadro debido y los siguientes
/// (<c>PrefetchFrames</c>), y el primer tramo del clip que sigue si el corte está a menos de <c>PreRoll</c>.
/// Cada origen atiende primero los cuadros debidos de todas sus capas, luego lo adelantado y al final el
/// pre-roll. Un origen sin demanda durante <c>DecoderIdleTimeout</c> se cierra.
/// </para>
/// </remarks>
internal sealed class TimelineScheduler : IDisposable
{
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
    private readonly long _maxCacheBytes;
    private readonly Dictionary<SourceKey, SourceState> _sources = [];
    private readonly Dictionary<int, SourceState> _sourcesById = [];
    private readonly Dictionary<string, LayerState> _layers = [];
    private readonly Func<int, long, long> _urgency;

    private TimelinePlan _plan;
    private int _nextSourceId;
    private long _demandFrame = long.MinValue;
    private Time _demandTime;
    private bool _seekPending;
    private bool _endSignaled;
    private bool _disposed;
    private long _seeks;
    private long _decodeErrors;
    private int _sourcesClosed;

    public TimelineScheduler(TimelinePlan plan, IAudioClock clock, IClipBackend backend, VideoDecoderOptions decoderOptions, TimelineSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(decoderOptions);
        ArgumentNullException.ThrowIfNull(options);
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
        _maxCacheBytes = options.MaxCacheBytes;
        _cache = new TimelineFrameCache(options.MaxCacheBytes);
        _urgency = Urgency;
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
    /// <remarks>Los cuadros guardados siguen valiendo para los clips que no cambiaron; las capas cuyo clip cambió esperan su cuadro nuevo.</remarks>
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
            foreach (LayerState layer in _layers.Values.Where(l => !visible.Contains(l.TrackId)))
            {
                layer.Release(released);
                layer.ActiveClip = null;
            }

            foreach (SourceState source in _sources.Values)
            {
                source.Unavailable.Clear();
                source.Requested.Clear();
            }

            posts = UpdateDemand(CurrentSequenceFrame(), force: true);
        }

        released.ForEach(f => f.Dispose());
        PostAll(posts);
    }

    /// <summary>Elige la composición de este refresco. Nunca espera al decodificador: cada capa muestra lo más nuevo que tenga.</summary>
    public CompositionFrame Tick()
    {
        var released = new List<DecodedFrame>();
        var layers = new List<LayerFrame>();
        var closing = new List<SourceState>();
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
            posts = UpdateDemand(time.ToFrames(_plan.FrameRate), force: false);
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
                    continue;
                }

                SourceState source = Source(clip);
                long due = clip.FrameAt(time);
                Present(layer, clip, source, due, running, released);
                bool exact = layer.IsShowing(source, due);
                bool missing = source.Unavailable.Contains(due) || source.Failure is not null;
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
                    SourceState shownSource = layer.CurrentSource!;
                    layers.Add(new LayerFrame(layerPlan.TrackIndex, shownClip.ClipId, frame, transform,
                        shownSource.SampleAspectRatio, shownSource.Rotation));
                }
            }

            if (_seekPending && settled)
            {
                _seekPending = false;
            }

            CollectIdleSources(closing);
            result = new CompositionFrame(time, _plan.Width, _plan.Height, layers);
        }

        released.ForEach(f => f.Dispose());
        closing.ForEach(Shutdown);
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
            return new TimelineStats(
                layers,
                all.Sum(l => l.Shown),
                all.Sum(l => l.Dropped),
                all.Sum(l => l.Late),
                all.Sum(l => l.Cuts),
                all.Sum(l => l.PreRollHits),
                all.Sum(l => l.PreRollMisses),
                all.Sum(l => l.LateAtCuts),
                _sources.Values.Count(s => s.Decoder is not null),
                _sourcesClosed,
                _cache.CostBytes,
                _cache.Count,
                _cache.BudgetBytes,
                _cache.Evictions,
                _sources.Values.Sum(s => s.Decoder?.Statistics.Seeks ?? 0),
                _decodeErrors,
                _seeks,
                audio);
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
        }
    }

    public void Dispose()
    {
        var released = new List<DecodedFrame>();
        List<SourceState> sources;
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

            sources = [.. _sources.Values];
            foreach (SourceState source in sources)
            {
                source.Closed = true;
            }

            _sources.Clear();
            _sourcesById.Clear();
            _cache.Clear();
        }

        released.ForEach(f => f.Dispose());
        sources.ForEach(Shutdown);
    }

    // ---- Presentación ----

    private void Present(LayerState layer, VideoClipPlan clip, SourceState source, long due, bool running, List<DecodedFrame> released)
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
                if (_cache.Contains(source.Id, due))
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
        if (layer.CurrentClip?.ClipId == clip.ClipId && layer.CurrentSource == source)
        {
            // Tras una edición el plan es otro objeto: la transformación del cuadro en pantalla sale del nuevo.
            layer.CurrentClip = clip;
        }

        if (layer.IsShowing(source, due))
        {
            return;
        }

        if (due < layer.LowerBound)
        {
            layer.LowerBound = due;
            layer.CountDrops = false;
        }

        if (!_cache.TryGetLatest(source.Id, layer.LowerBound, due, out DecodedFrame? frame))
        {
            return;
        }

        if (layer.CountDrops && running && layer.CurrentSource == source && layer.Current is { } previous)
        {
            layer.Dropped += Math.Max(0, frame.Index - previous.Index - 1);
        }

        layer.Release(released);
        layer.Current = frame;
        layer.CurrentClip = clip;
        layer.CurrentSource = source;
        layer.Shown++;
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
        foreach (SourceState source in _sources.Values)
        {
            stale.Add(source.Epoch);
            source.Epoch = new CancellationTokenSource();
            source.Unavailable.Clear();
            source.Requested.Clear();
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

    // ---- Demanda ----

    /// <summary>Recalcula las ventanas de todos los orígenes para un cuadro de la secuencia; devuelve el trabajo a encolar.</summary>
    private List<(IDecodeExecutor, Action)> UpdateDemand(long sequenceFrame, bool force)
    {
        var posts = new List<(IDecodeExecutor, Action)>();
        if (!force && sequenceFrame == _demandFrame)
        {
            return posts;
        }

        _demandFrame = sequenceFrame;
        Time time = Time.FromFrames(sequenceFrame, _plan.FrameRate);
        _demandTime = time;
        foreach (SourceState source in _sources.Values)
        {
            source.Windows.Clear();
        }

        foreach (VideoLayerPlan layer in _plan.Layers)
        {
            if (layer.ClipAt(time) is { } clip)
            {
                long due = clip.FrameAt(time);
                long end = Math.Min(due + _prefetch, clip.LastFrame + 1);
                SourceState source = Source(clip);
                source.Windows.Add(new Window(due, due + 1, Due: true, clip));
                if (end > due + 1)
                {
                    source.Windows.Add(new Window(due + 1, end, Due: false, clip));
                }
            }

            if (layer.NextAfter(time) is { } next && next.Start - time <= _preRoll)
            {
                long first = next.FirstFrame;
                Source(next).Windows.Add(new Window(first, Math.Min(first + _preRollFrames, next.LastFrame + 1), Due: false, next));
            }
        }

        long now = _time.GetTimestamp();
        foreach (SourceState source in _sources.Values)
        {
            if (source.Windows.Count == 0)
            {
                source.IdleSince ??= now;
                continue;
            }

            source.IdleSince = null;
            source.Requested.RemoveWhere(i => !source.InWindows(i));
            if (source.Decoder is null && !source.Opening && source.Failure is null)
            {
                source.Opening = true;
                source.Executor = _backend.CreateExecutor();
                posts.Add((source.Executor, () => Open(source)));
            }
            else if (source.Decoder is not null && RequestPump(source))
            {
                posts.Add((source.Executor!, source.Pump));
            }
        }

        return posts;
    }

    private SourceState Source(VideoClipPlan clip)
    {
        if (!_sources.TryGetValue(clip.Source, out SourceState? source))
        {
            source = new SourceState(_nextSourceId++, clip.Source);
            source.Pump = () => Pump(source);
            _cache.SetSourceLimit(source.Id, _maxFramesPerSource);
            _sources.Add(clip.Source, source);
            _sourcesById.Add(source.Id, source);
        }

        return source;
    }

    /// <summary>Más urgente cuanto antes haya que mostrarlo; <see cref="TimelineFrameCache.Unwanted"/> si ninguna ventana lo quiere.</summary>
    private long Urgency(int sourceId, long index)
    {
        long best = TimelineFrameCache.Unwanted;
        if (_sourcesById.TryGetValue(sourceId, out SourceState? source))
        {
            foreach (Window window in source.Windows)
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
        return Math.Max(0, (shownAt - _demandTime).Ticks);
    }

    private void CollectIdleSources(List<SourceState> closing)
    {
        long now = _time.GetTimestamp();
        foreach (SourceState source in _sources.Values)
        {
            if (source.IdleSince is { } since && !source.Opening && source.InFlight is null
                && _time.GetElapsedTime(since, now) >= _idleTimeout)
            {
                closing.Add(source);
            }
        }

        foreach (SourceState source in closing)
        {
            source.Closed = true;
            _sources.Remove(source.Key);
            _sourcesById.Remove(source.Id);
            _cache.RemoveSource(source.Id);
            _sourcesClosed++;
        }
    }

    // ---- Decodificación ----

    private void Open(SourceState source)
    {
        IClipVideo? video = null;
        Exception? error = null;
#pragma warning disable CA1031 // Un archivo que no abre deja su capa vacía; se informa y la reproducción sigue.
        try
        {
            video = _backend.OpenVideo(source.Key.Path, _decoderOptions);
        }
        catch (Exception ex)
        {
            error = ex;
        }
#pragma warning restore CA1031

        bool discard;
        bool post = false;
        lock (_gate)
        {
            source.Opening = false;
            discard = _disposed || source.Closed;
            if (!discard && error is not null)
            {
                source.Failure = error.Message;
                _decodeErrors++;
            }
            else if (!discard && video is not null)
            {
                source.Decoder = video;
                source.SampleAspectRatio = video.Stream.SampleAspectRatio;
                source.Rotation = NormalizeRotation(video.Stream.Rotation);
                int limit = PlanSourceLimit(video);
                _cache.SetSourceLimit(source.Id, limit);
                post = RequestPump(source);
            }
        }

        if (discard)
        {
            video?.Dispose();
            return;
        }

        if (error is not null)
        {
            DecodeFailed?.Invoke(this, new TimelineDecodeFailedEventArgs(source.Key.MediaId, source.Key.Path, -1, error));
        }

        if (post)
        {
            source.Executor!.Post(source.Pump);
        }
    }

    /// <summary>Lleva cualquier giro en grados al múltiplo de 90 más cercano, entre 0 y 270.</summary>
    internal static int NormalizeRotation(int degrees)
    {
        int quarter = (int)Math.Round(degrees / 90.0, MidpointRounding.AwayFromZero);
        return ((quarter % 4) + 4) % 4 * 90;
    }

    /// <summary>Tope de cuadros del origen: lo que pide la opción, sin que un solo origen se coma el presupuesto.</summary>
    private int PlanSourceLimit(IClipVideo video)
    {
        VideoStreamInfo stream = video.Stream;
        long frame = Math.Max(1, (long)stream.Width * stream.Height * (stream.BitDepth > 8 ? 2 : 1) * 3 / 2);
        return (int)Math.Clamp(_maxCacheBytes / frame, 2, _maxFramesPerSource);
    }

    private static bool RequestPump(SourceState source)
    {
        if (source.PumpPosted || source.InFlight is not null)
        {
            return false;
        }

        source.PumpPosted = true;
        return true;
    }

    // Se pide primero lo que antes haya que mostrar, sea de la capa que sea. Un cuadro adelantado que la caché ya
    // descartó por falta de lugar se vuelve a pedir solo cuando ahora sí cabría: si no, decodificarlo expulsaría
    // otro y así sin fin. El cuadro debido siempre se repide.
    private bool TryFindMissing(SourceState source, out long index)
    {
        index = 0;
        long bestDelay = long.MaxValue;
        foreach (Window window in source.Windows)
        {
            for (long i = window.Start; i < window.End; i++)
            {
                long delay = Delay(window, i);
                if (delay >= bestDelay || source.Unavailable.Contains(i) || _cache.Contains(source.Id, i) || IsShown(source, i))
                {
                    continue;
                }

                if (!window.Due && source.Requested.Contains(i) && !_cache.CanAdmit(source.Id, long.MaxValue - delay, _urgency))
                {
                    continue;
                }

                (index, bestDelay) = (i, delay);
            }
        }

        return bestDelay != long.MaxValue;
    }

    private bool IsShown(SourceState source, long index)
    {
        foreach (LayerState layer in _layers.Values)
        {
            if (layer.IsShowing(source, index))
            {
                return true;
            }
        }

        return false;
    }

    private void Pump(SourceState source)
    {
        while (true)
        {
            Job? job = null;
            IClipVideo? decoder = null;
            List<(IDecodeExecutor, Action)> posts = [];
            lock (_gate)
            {
                source.PumpPosted = false;
                if (_disposed || source.Closed || source.Decoder is null || source.InFlight is not null)
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
                        posts.RemoveAll(p => p.Item2 == source.Pump);
                        source.PumpPosted = false;
                    }
                }

                if (TryFindMissing(source, out long index))
                {
                    source.Requested.Add(index);
                    job = source.InFlight = new Job(index, source.Epoch);
                    decoder = source.Decoder;
                }
            }

            PostAll(posts);
            if (job is null || decoder is null)
            {
                return;
            }

            ValueTask<DecodedFrame?> pending;
            try
            {
                pending = decoder.GetFrameAsync(job.Index, job.Epoch.Token);
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
                        if (Complete(source, job, new ValueTask<DecodedFrame?>(task)))
                        {
                            source.Executor!.Post(source.Pump);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            Complete(source, job, pending);
        }
    }

    private bool Complete(SourceState source, Job job, ValueTask<DecodedFrame?> pending)
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

        DecodedFrame? discard = null;
        bool post;
        lock (_gate)
        {
            source.InFlight = null;
            bool current = job.Epoch == source.Epoch;
            if (_disposed || source.Closed)
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
                _cache.Add(source.Id, frame, _urgency);
            }

            if (!_disposed && !source.Closed && current && (frame is null || error is not null))
            {
                source.Unavailable.Add(job.Index);
            }

            if (!_disposed && error is not null)
            {
                _decodeErrors++;
            }

            post = !_disposed && !source.Closed && RequestPump(source);
        }

        discard?.Dispose();
        if (error is not null)
        {
            DecodeFailed?.Invoke(this, new TimelineDecodeFailedEventArgs(source.Key.MediaId, source.Key.Path, job.Index, error));
        }

        return post;
    }

    private static void PostAll(List<(IDecodeExecutor Executor, Action Work)> posts)
    {
        foreach ((IDecodeExecutor executor, Action work) in posts)
        {
            executor.Post(work);
        }
    }

    /// <summary>Libera el decodificador en su propio hilo, después de lo que ya tenía en cola, y detiene el hilo.</summary>
    private void Shutdown(SourceState source)
    {
        if (source.Executor is not { } executor)
        {
            return;
        }

        executor.Post(() =>
        {
            IClipVideo? decoder;
            lock (_gate)
            {
                decoder = source.Decoder;
                source.Decoder = null;
            }

            decoder?.Dispose();
        });
        (executor as IDisposable)?.Dispose();
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
    private readonly record struct Window(long Start, long End, bool Due, VideoClipPlan Clip);

    private sealed class SourceState(int id, SourceKey key)
    {
        public int Id { get; } = id;

        public SourceKey Key { get; } = key;

        public Action Pump { get; set; } = () => { };

        public IClipVideo? Decoder { get; set; }

        public Rational SampleAspectRatio { get; set; } = new(1, 1);

        public int Rotation { get; set; }

        public IDecodeExecutor? Executor { get; set; }

        public bool Opening { get; set; }

        public bool Closed { get; set; }

        public string? Failure { get; set; }

        public List<Window> Windows { get; } = [];

        public HashSet<long> Requested { get; } = [];

        public HashSet<long> Unavailable { get; } = [];

        // Sin temporizadores un CTS no retiene recursos; el reemplazado no se libera porque la
        // decodificación en curso todavía puede consultar su token.
        public CancellationTokenSource Epoch { get; set; } = new();

        public Job? InFlight { get; set; }

        public bool PumpPosted { get; set; }

        public long? IdleSince { get; set; }

        public bool InWindows(long index) => Windows.Exists(w => index >= w.Start && index < w.End);
    }

    private sealed class LayerState(string trackId)
    {
        public string TrackId { get; } = trackId;

        public DecodedFrame? Current { get; set; }

        public VideoClipPlan? CurrentClip { get; set; }

        public SourceState? CurrentSource { get; set; }

        /// <summary>Clip que cubría el instante en el último refresco.</summary>
        public VideoClipPlan? ActiveClip { get; set; }

        public long LowerBound { get; set; }

        public bool CountDrops { get; set; }

        /// <summary>Tras un seek o al aparecer la capa: el próximo cuadro debe ser el exacto y no cuenta como corte.</summary>
        public bool Resync { get; set; }

        /// <summary>El corte llegó sin su primer cuadro listo; los refrescos hasta tenerlo cuentan como tarde en el corte.</summary>
        public bool AwaitingCut { get; set; }

        public long Shown { get; set; }

        public long Dropped { get; set; }

        public long Late { get; set; }

        public long Cuts { get; set; }

        public long PreRollHits { get; set; }

        public long PreRollMisses { get; set; }

        public long LateAtCuts { get; set; }

        public bool IsShowing(SourceState source, long index) => CurrentSource == source && Current?.Index == index;

        public void Release(List<DecodedFrame> released)
        {
            if (Current is not null)
            {
                released.Add(Current);
            }

            Current = null;
            CurrentClip = null;
            CurrentSource = null;
        }

        public void ResetStats() => (Shown, Dropped, Late, Cuts, PreRollHits, PreRollMisses, LateAtCuts) = (0, 0, 0, 0, 0, 0, 0);

        public LayerStats Snapshot(int trackIndex) =>
            new(trackIndex, TrackId, Shown, Dropped, Late, Cuts, PreRollHits, PreRollMisses, LateAtCuts);
    }
}
