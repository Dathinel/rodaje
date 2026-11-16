using Rodaje.Core.Timing;

namespace Rodaje.App.Services.RenderCache;

/// <summary>Qué calcula el render en caché por su cuenta (Preferencias › Rendimiento).</summary>
public enum RenderCacheMode
{
    /// <summary>Los tramos rojos y después los amarillos, en reposo.</summary>
    Smart,

    /// <summary>Solo lo pedido con «Calcular de entrada a salida».</summary>
    Manual,

    /// <summary>Nada.</summary>
    Off,
}

/// <summary>Lo que hace ahora el servicio del render en caché.</summary>
public enum RenderCacheState
{
    Idle,

    /// <summary>Hay trabajo, esperando 3 s de reposo.</summary>
    Waiting,

    Working,

    /// <summary>El trabajo en curso está en pausa porque se reproduce o se arrastra el cursor.</summary>
    Paused,

    /// <summary>El tope del caché o el disco libre no dejan calcular más.</summary>
    Blocked,
}

/// <summary>Un trozo a calcular: cuadros seguidos de la secuencia con las recetas de una revisión del plan.</summary>
public sealed record RenderCacheWork(long FirstFrame, int Count, long Revision);

/// <summary>Pausa cooperativa del trabajo en curso: quien calcula mira <see cref="WaitWhilePaused"/> entre cuadros.</summary>
public sealed class RenderCachePause : IDisposable
{
    private readonly ManualResetEventSlim _running = new(initialState: true);

    public bool IsPaused => !_running.IsSet;

    public void Pause() => _running.Reset();

    public void Resume() => _running.Set();

    public void WaitWhilePaused(CancellationToken cancellationToken) => _running.Wait(cancellationToken);

    public void Dispose() => _running.Dispose();
}

/// <summary>Quien calcula un trozo (en la app, un <c>RenderCacheJob</c>); devuelve si publicó el fragmento.</summary>
public interface IRenderCacheWorker
{
    Task<bool> RunAsync(RenderCacheWork work, RenderCachePause pause, CancellationToken cancellationToken);
}

/// <summary>
/// Decide qué tramos calcular y cuándo (D-060): tras 3 s sin editar, reproducir ni arrastrar, uno a la vez, primero lo
/// rojo y luego lo amarillo más cerca del cursor, en trozos de 2 s; pausa al reproducir y descarta lo de una revisión
/// vieja. Sin hilos propios: quien lo usa llama a <see cref="Tick"/> cada cierto tiempo.
/// </summary>
public sealed class RenderCacheService
{
    public const long MinimumFreeBytes = 5L * 1024 * 1024 * 1024;

    private static readonly TimeSpan Rest = TimeSpan.FromSeconds(3);

    private readonly IRenderCacheWorker _worker;
    private readonly TimeProvider _time;
    private readonly Func<long> _freeBytes;
    private readonly Func<long> _usedBytes;
    private readonly long _capBytes;
    private readonly Queue<RenderCacheWork> _queue = new();
    private long _lastActivity;
    private bool _playing;
    private bool _scrubbing;
    private long _revision;
    private Rational _rate = Rational.Fps30;
    private (RenderCacheWork Work, RenderCachePause Pause, CancellationTokenSource Cancel, Task Task)? _current;

    public RenderCacheService(IRenderCacheWorker worker, TimeProvider time, Func<long> freeBytes, Func<long> usedBytes, long capBytes)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(freeBytes);
        ArgumentNullException.ThrowIfNull(usedBytes);
        _worker = worker;
        _time = time;
        _freeBytes = freeBytes;
        _usedBytes = usedBytes;
        _capBytes = capBytes;
        _lastActivity = time.GetTimestamp();
    }

    public RenderCacheMode Mode { get; set; } = RenderCacheMode.Smart;

    public RenderCacheState State { get; private set; }

    public int Completed { get; private set; }

    public int Discarded { get; private set; }

    /// <summary>Trozos que esperan, el siguiente primero.</summary>
    public IReadOnlyCollection<RenderCacheWork> Pending => _queue;

    /// <summary>El trabajo en curso, si hay.</summary>
    public RenderCacheWork? Current => _current?.Work;

    /// <summary>Los tramos de una revisión del plan; en <see cref="RenderCacheMode.Smart"/> rehace la cola.</summary>
    public void SetPlan(long revision, IReadOnlyList<RenderCostSpan> spans, long cursorFrame, Rational rate)
    {
        ArgumentNullException.ThrowIfNull(spans);
        _revision = revision;
        _rate = rate;
        if (Mode != RenderCacheMode.Smart)
        {
            if (Mode == RenderCacheMode.Off)
            {
                _queue.Clear();
            }

            return;
        }

        _queue.Clear();
        foreach (RenderCostClass wanted in (RenderCostClass[])[RenderCostClass.Heavy, RenderCostClass.Tight])
        {
            foreach (RenderCostSpan span in spans.Where(s => s.Class == wanted && s.End > s.Start).OrderBy(s => Distance(s, cursorFrame)))
            {
                Enqueue(span.Start, span.End);
            }
        }

        UpdateState();
    }

    /// <summary>«Calcular de entrada a salida» (o la selección): va delante de lo demás.</summary>
    public void Request(long first, long end)
    {
        if (Mode == RenderCacheMode.Off || end <= first)
        {
            return;
        }

        RenderCacheWork[] rest = [.. _queue];
        _queue.Clear();
        Enqueue(first, end);
        foreach (RenderCacheWork work in rest)
        {
            _queue.Enqueue(work);
        }

        UpdateState();
    }

    public void NotifyEdited() => _lastActivity = _time.GetTimestamp();

    public void NotifyPlaying(bool playing) => Interrupt(ref _playing, playing);

    public void NotifyScrubbing(bool scrubbing) => Interrupt(ref _scrubbing, scrubbing);

    /// <summary>Cancela el trabajo en curso y vacía la cola.</summary>
    public void Cancel()
    {
        _queue.Clear();
        _current?.Cancel.Cancel();
        UpdateState();
    }

    /// <summary>Recoge el trabajo terminado y, tras el reposo, reanuda el que estaba en pausa o arranca el siguiente.</summary>
    public void Tick()
    {
        if (_current is { } current && current.Task.IsCompleted)
        {
            Finish(current);
            _current = null;
        }

        bool rested = !_playing && !_scrubbing && _time.GetElapsedTime(_lastActivity) >= Rest;
        if (_current is { } running)
        {
            if (rested && running.Pause.IsPaused)
            {
                running.Pause.Resume();
            }
        }
        else if (rested && Mode != RenderCacheMode.Off && _queue.Count > 0)
        {
            if (_usedBytes() >= _capBytes || _freeBytes() < MinimumFreeBytes)
            {
                State = RenderCacheState.Blocked;
                return;
            }

            RenderCacheWork work = _queue.Dequeue();
            var pause = new RenderCachePause();
            var cancel = new CancellationTokenSource();
            _current = (work, pause, cancel, _worker.RunAsync(work, pause, cancel.Token));
        }

        UpdateState();
    }

    private void Finish((RenderCacheWork Work, RenderCachePause Pause, CancellationTokenSource Cancel, Task Task) done)
    {
        bool published = done.Task is Task<bool> { IsCompletedSuccessfully: true } result && result.Result;
        if (published && done.Work.Revision == _revision)
        {
            Completed++;
        }
        else if (published)
        {
            Discarded++;
        }

        done.Pause.Dispose();
        done.Cancel.Dispose();
    }

    private void Interrupt(ref bool flag, bool value)
    {
        flag = value;
        _lastActivity = _time.GetTimestamp();
        if (value)
        {
            _current?.Pause.Pause();
        }

        UpdateState();
    }

    private void Enqueue(long start, long end)
    {
        int chunk = (int)Math.Ceiling(2 * _rate.ToDouble());
        for (long first = start; first < end; first += chunk)
        {
            _queue.Enqueue(new RenderCacheWork(first, (int)Math.Min(chunk, end - first), _revision));
        }
    }

    private static long Distance(RenderCostSpan span, long cursor) =>
        cursor < span.Start ? span.Start - cursor : cursor >= span.End ? cursor - span.End + 1 : 0;

    private void UpdateState() =>
        State = _current is { } current ? current.Pause.IsPaused ? RenderCacheState.Paused : RenderCacheState.Working
            : _queue.Count > 0 && Mode != RenderCacheMode.Off ? RenderCacheState.Waiting
            : RenderCacheState.Idle;
}
