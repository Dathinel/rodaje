using Rodaje.Core.Timing;

namespace Rodaje.Playback;

/// <summary>Decide qué cuadro de un clip mostrar en cada refresco siguiendo al reloj de audio, y mantiene la decodificación por delante.</summary>
/// <remarks>
/// <para>Hilos: <see cref="Tick"/> se llama desde el hilo de render en cada refresco y nunca espera al
/// decodificador; los controles de transporte pueden llamarse desde cualquier hilo; los pedidos al
/// decodificador salen del <see cref="IDecodeExecutor"/>, de a uno, y cada resultado se guarda en el hilo
/// que lo terminó. Todo el estado se protege con un único candado que nunca se retiene mientras se decodifica.</para>
/// <para>La cola de decodificación es la ventana <c>[cuadro debido, cuadro debido + PrefetchFrames)</c>:
/// se recalcula en cada refresco y antes de cada pedido, así que nunca crece y lo que el reloj ya dejó
/// atrás no se pide.</para>
/// </remarks>
public sealed class PlaybackScheduler : IDisposable
{
    private readonly Lock _gate = new();
    private readonly IAudioClock _clock;
    private readonly IVideoFrameSource _source;
    private readonly FrameCache _cache;
    private readonly IDecodeExecutor _executor;
    private readonly int _prefetch;
    private readonly HashSet<long> _unavailable = [];
    private readonly HashSet<long> _requested = [];
    private readonly Action _pump;

    // Sin temporizadores un CTS no retiene recursos; el reemplazado no se libera porque la
    // decodificación en curso todavía puede consultar su token.
    private CancellationTokenSource _epochCts = new();
    private Job? _inFlight;
    private bool _pumpPosted;
    private long _windowStart;
    private long _windowEnd;

    private DecodedFrame? _current;
    private long _lowerBound;
    private bool _countDrops;
    private long? _seekTarget;
    private bool _endSignaled;
    private bool _disposed;

    private long _shown;
    private long _dropped;
    private long _late;
    private long _hits;
    private long _misses;
    private long _errors;
    private Time _maxOffset;

    public PlaybackScheduler(
        ClipTiming timing,
        IAudioClock clock,
        IVideoFrameSource source,
        FrameCache cache,
        IDecodeExecutor executor,
        PlaybackOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(executor);
        options ??= new PlaybackOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PrefetchFrames, nameof(options));

        Timing = timing;
        _clock = clock;
        _source = source;
        _cache = cache;
        _executor = executor;
        _prefetch = options.PrefetchFrames;
        _pump = Pump;

        long target = timing.FrameAt(clock.Position);
        _lowerBound = target;
        if (MoveWindow(target))
        {
            _executor.Post(_pump);
        }
    }

    /// <summary>El cuadro pedido por un seek ya está decodificado. Puede llegar desde un hilo de fondo.</summary>
    public event EventHandler<FrameReadyEventArgs>? ExactFrameReady;

    /// <summary>La reproducción llegó al final del clip y el reloj quedó en pausa.</summary>
    public event EventHandler? EndReached;

    /// <summary>El decodificador falló con un cuadro; ese cuadro se salta hasta el siguiente seek.</summary>
    public event EventHandler<DecodeFailedEventArgs>? DecodeFailed;

    public ClipTiming Timing { get; }

    public Time Position => _clock.Position;

    public bool IsPlaying => _clock.IsRunning;

    public bool IsSeekPending
    {
        get
        {
            lock (_gate)
            {
                return _seekTarget is not null;
            }
        }
    }

    public PlaybackStats Stats
    {
        get
        {
            lock (_gate)
            {
                return new(_shown, _dropped, _late, _maxOffset, _hits, _misses, _errors);
            }
        }
    }

    public void ResetStats()
    {
        lock (_gate)
        {
            (_shown, _dropped, _late, _hits, _misses, _errors, _maxOffset) = (0, 0, 0, 0, 0, 0, Time.Zero);
        }
    }

    public void Play()
    {
        Transition transition = default;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clock.Position >= Timing.End)
            {
                transition = SeekLocked(Timing.Start);
            }

            _clock.Start();
        }

        Finish(transition);
    }

    public void Pause()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _clock.Pause();
        }
    }

    /// <summary>Lleva el reloj a <paramref name="timelinePosition"/>, acotada al clip, y descarta lo pendiente.</summary>
    public void Seek(Time timelinePosition)
    {
        Transition transition;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            transition = SeekLocked(timelinePosition);
        }

        Finish(transition);
    }

    public void SeekToFrame(long frameIndex) => Seek(Timing.TimelineStartOf(frameIndex));

    /// <summary>Elige el cuadro de este refresco. Nunca bloquea: si el debido no está listo, repite el último.</summary>
    public FramePresentation Tick()
    {
        DecodedFrame? released = null;
        (long From, long To)? passed = null;
        FrameReadyEventArgs? ready = null;
        bool post;
        bool reachedEnd = false;
        FramePresentation result;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Time position = _clock.Position;
            bool running = _clock.IsRunning;
            if (position >= Timing.End && running)
            {
                _clock.Pause();
                running = false;
                reachedEnd = !_endSignaled;
                _endSignaled = true;
            }

            Time sourceTime = Timing.ToSource(Timing.Clamp(position));
            long target = Timing.FrameAt(position);
            post = MoveWindow(target);

            if (_current?.Index != target)
            {
                if (target < _lowerBound)
                {
                    _lowerBound = target;
                    _countDrops = false;
                }

                if (_cache.TryGetLatest(_lowerBound, target, out DecodedFrame? frame))
                {
                    if (frame.Index == target)
                    {
                        _hits++;
                    }
                    else
                    {
                        _misses++;
                    }

                    if (_countDrops && running && _current is not null)
                    {
                        _dropped += Math.Max(0, frame.Index - _current.Index - 1);
                    }

                    released = _current;
                    _current = frame;

                    // Al avanzar reproduciendo, lo que quedó atrás sale de la caché: si no, al tocarse al mostrarse
                    // sería más reciente que lo adelantado aún sin mostrar, y el LRU expulsaría lo adelantado.
                    // Se borra fuera del candado porque la caché libera allí los cuadros que suelta.
                    if (running && released is not null && released.Index < frame.Index)
                    {
                        passed = (released.Index, frame.Index);
                    }

                    _shown++;
                    _lowerBound = frame.Index + 1;
                    _countDrops = true;
                }
                else
                {
                    _misses++;
                }
            }

            bool exact = _current?.Index == target;
            if (running && !exact)
            {
                _late++;
            }

            if (running && _current is not null && !Timing.IsStillImage)
            {
                Time offset = sourceTime - _current.Pts;
                _maxOffset = Time.Max(_maxOffset, offset < Time.Zero ? -offset : offset);
            }

            if (exact && _seekTarget is not null)
            {
                _seekTarget = null;
                ready = new FrameReadyEventArgs(target);
            }

            result = new FramePresentation(_current, target, sourceTime, exact);
        }

        if (passed is { } range)
        {
            _cache.RemoveRange(range.From, range.To);
        }

        released?.Dispose();
        if (post)
        {
            _executor.Post(_pump);
        }

        if (ready is not null)
        {
            ExactFrameReady?.Invoke(this, ready);
        }

        if (reachedEnd)
        {
            EndReached?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public void Dispose()
    {
        DecodedFrame? released;
        CancellationTokenSource epoch;
        bool idle;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            released = _current;
            _current = null;
            epoch = _epochCts;
            idle = _inFlight is null;
        }

        epoch.Cancel();
        if (idle)
        {
            epoch.Dispose();
        }

        released?.Dispose();
    }

    private Transition SeekLocked(Time timelinePosition)
    {
        Time clamped = Timing.Clamp(timelinePosition);
        long target = Timing.FrameAt(clamped);
        _clock.Seek(clamped);

        CancellationTokenSource stale = _epochCts;
        _epochCts = new CancellationTokenSource();
        _unavailable.Clear();
        _requested.Clear();
        _lowerBound = target;
        _countDrops = false;
        _endSignaled = false;
        _windowStart = target;
        _windowEnd = WindowEnd(target);

        FrameReadyEventArgs? ready = null;
        if (_cache.Contains(target))
        {
            _seekTarget = null;
            ready = new FrameReadyEventArgs(target);
        }
        else
        {
            _seekTarget = target;
        }

        return new Transition(stale, RequestPump(), ready);
    }

    private void Finish(Transition transition)
    {
        transition.Stale?.Cancel();
        if (transition.Post)
        {
            _executor.Post(_pump);
        }

        if (transition.Ready is not null)
        {
            ExactFrameReady?.Invoke(this, transition.Ready);
        }
    }

    private long WindowEnd(long target) => Math.Min(target + _prefetch, Timing.LastFrame + 1);

    private bool MoveWindow(long target)
    {
        if (target == _windowStart && WindowEnd(target) == _windowEnd)
        {
            return false;
        }

        SetWindow(target);
        return RequestPump();
    }

    private void SetWindow(long target)
    {
        if (target < _windowStart)
        {
            _requested.Clear();
        }
        else
        {
            _requested.RemoveWhere(i => i < target);
        }

        _windowStart = target;
        _windowEnd = WindowEnd(target);
    }

    private bool RequestPump()
    {
        if (_pumpPosted || _inFlight is not null)
        {
            return false;
        }

        _pumpPosted = true;
        return true;
    }

    // Un cuadro adelantado se pide una sola vez por ventana: si la caché lo expulsa antes de
    // mostrarlo, volver a pedirlo podría expulsar otro y así sin fin. El cuadro debido siempre se repide.
    private bool TryFindMissing(out long index)
    {
        for (long i = _windowStart; i < _windowEnd; i++)
        {
            if (_current?.Index != i && !_unavailable.Contains(i) && !_cache.Contains(i)
                && (i == _windowStart || !_requested.Contains(i)))
            {
                index = i;
                return true;
            }
        }

        index = 0;
        return false;
    }

    private void Pump()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                _pumpPosted = false;
                if (!_disposed && _clock.IsRunning)
                {
                    // Sin esperar al próximo refresco: lo que el reloj ya dejó atrás no se decodifica
                    long now = Timing.FrameAt(_clock.Position);
                    if (now > _windowStart)
                    {
                        SetWindow(now);
                    }
                }

                if (_disposed || _inFlight is not null || !TryFindMissing(out long index))
                {
                    return;
                }

                _requested.Add(index);
                job = _inFlight = new Job(index, _epochCts);
            }

            ValueTask<DecodedFrame?> pending;
            try
            {
                pending = _source.GetFrameAsync(job.Index, job.Epoch.Token);
            }
            catch (Exception ex)
            {
                pending = ValueTask.FromException<DecodedFrame?>(ex);
            }

            if (!pending.IsCompleted)
            {
                // Se completa en el hilo que terminó la decodificación para que el cuadro nunca quede
                // huérfano si el ejecutor ya se detuvo; solo la continuación del bucle vuelve al ejecutor.
                // ExecuteSynchronously se ejecuta en línea aunque el hilo tenga un SynchronizationContext.
                _ = pending.AsTask().ContinueWith(
                    task =>
                    {
                        if (Complete(job, new ValueTask<DecodedFrame?>(task)))
                        {
                            _executor.Post(_pump);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            Complete(job, pending);
        }
    }

    private bool Complete(Job job, ValueTask<DecodedFrame?> pending)
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
        List<DecodedFrame>? evicted = null;
        FrameReadyEventArgs? ready = null;
        DecodeFailedEventArgs? failed = null;
        bool post;
        lock (_gate)
        {
            _inFlight = null;
            bool current = job.Epoch == _epochCts;
            if (_disposed)
            {
                discard = frame;
            }
            else if (frame is not null && frame.Index != job.Index)
            {
                discard = frame;
                error = new InvalidOperationException(
                    $"El decodificador devolvió el cuadro {frame.Index} cuando se pidió el {job.Index}.");
            }
            else if (frame is not null)
            {
                _cache.Add(frame, ref evicted);
                if (_seekTarget == job.Index)
                {
                    _seekTarget = null;
                    ready = new FrameReadyEventArgs(job.Index);
                }
            }

            if (!_disposed && current && (frame is null || error is not null))
            {
                _unavailable.Add(job.Index);
            }

            if (!_disposed && error is not null)
            {
                _errors++;
                failed = new DecodeFailedEventArgs(job.Index, error);
            }

            post = !_disposed && RequestPump();
        }

        discard?.Dispose();
        evicted?.ForEach(f => f.Dispose());
        if (ready is not null)
        {
            ExactFrameReady?.Invoke(this, ready);
        }

        if (failed is not null)
        {
            DecodeFailed?.Invoke(this, failed);
        }

        return post;
    }

    private sealed record Job(long Index, CancellationTokenSource Epoch);

    private readonly record struct Transition(CancellationTokenSource? Stale, bool Post, FrameReadyEventArgs? Ready);
}
