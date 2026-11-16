using Rodaje.Core.Timing;
using Rodaje.Engine.RenderCache;
using Rodaje.Export.Running;

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

/// <summary>Quien calcula un trozo (en la app, un <c>RenderCacheJob</c>); devuelve si publicó el fragmento.</summary>
public interface IRenderCacheWorker
{
    Task<bool> RunAsync(RenderCacheWork work, ExportPauseToken pause, CancellationToken cancellationToken);
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
    private long _frameCount;
    private RenderCacheMode _mode = RenderCacheMode.Smart;
    private (RenderCacheWork Work, ExportPauseToken Pause, CancellationTokenSource Cancel, Task Task)? _current;

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

    /// <summary>Pasar a <see cref="RenderCacheMode.Off"/> corta el trabajo en curso y vacía la cola.</summary>
    public RenderCacheMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            if (value == RenderCacheMode.Off)
            {
                Cancel();
            }
        }
    }

    public RenderCacheState State { get; private set; }

    public int Completed { get; private set; }

    public int Discarded { get; private set; }

    /// <summary>Trozos que fallaron (no se vuelven a encolar).</summary>
    public int Failed { get; private set; }

    /// <summary>Trozos que esperan, el siguiente primero.</summary>
    public IReadOnlyCollection<RenderCacheWork> Pending => _queue;

    /// <summary>El trabajo en curso, si hay.</summary>
    public RenderCacheWork? Current => _current?.Work;

    /// <summary>
    /// Los tramos de una revisión del plan; en <see cref="RenderCacheMode.Smart"/> rehace la cola. El trozo en curso se
    /// adopta si <paramref name="adopt"/> dice que sigue vigente (mismas huellas en los mismos cuadros): cuenta para la
    /// revisión nueva y sus cuadros no se vuelven a encolar.
    /// </summary>
    /// <param name="frameCount">Cuadros de la secuencia; sin él, el final del último tramo (un trozo corto pedido después se
    /// ensancharía hacia atrás hasta ese final).</param>
    public void SetPlan(long revision, IReadOnlyList<RenderCostSpan> spans, long cursorFrame, Rational rate, Func<RenderCacheWork, bool>? adopt = null,
        long? frameCount = null)
    {
        ArgumentNullException.ThrowIfNull(spans);
        _revision = revision;
        _rate = rate;
        _frameCount = frameCount ?? (spans.Count == 0 ? 0 : spans.Max(s => s.End));
        (long Start, long End) skip = (0, 0);
        if (_current is { } running && running.Work.Revision != revision && adopt?.Invoke(running.Work) == true)
        {
            _current = running with { Work = running.Work with { Revision = revision } };
            skip = (running.Work.FirstFrame, running.Work.FirstFrame + running.Work.Count);
        }

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
                if (span.End <= skip.Start || span.Start >= skip.End)
                {
                    Enqueue(span.Start, span.End);
                    continue;
                }

                if (span.Start < skip.Start)
                {
                    Enqueue(span.Start, skip.Start);
                }

                if (span.End > skip.End)
                {
                    Enqueue(skip.End, span.End);
                }
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
            if (rested && Mode != RenderCacheMode.Off && running.Pause.IsPauseRequested)
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
            var pause = new ExportPauseToken();
            var cancel = new CancellationTokenSource();
            _current = (work, pause, cancel, _worker.RunAsync(work, pause, cancel.Token));
        }

        UpdateState();
    }

    private void Finish((RenderCacheWork Work, ExportPauseToken Pause, CancellationTokenSource Cancel, Task Task) done)
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
        else if (!done.Cancel.IsCancellationRequested && done.Work.Revision == _revision && done.Task.IsCanceled)
        {
            // Una pausa larga corta el trozo: vuelve delante para cuando haya reposo. Un fallo no, o se repetiría sin fin.
            RenderCacheWork[] rest = [.. _queue];
            _queue.Clear();
            _queue.Enqueue(done.Work);
            foreach (RenderCacheWork work in rest)
            {
                _queue.Enqueue(work);
            }
        }
        else if (done.Task.IsFaulted)
        {
            Failed++;
        }

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

    /// <summary>
    /// Trozos de hasta 2 s, todos de al menos <see cref="CacheCoverage.ShortestSpan"/>: uno más corto se calcularía y no
    /// se vería nunca. Un tramo corto se ensancha dentro de la secuencia y los trozos se reparten por igual.
    /// </summary>
    private void Enqueue(long start, long end)
    {
        long shortest = CacheCoverage.ShortestSpan(_rate);
        if (end - start < shortest)
        {
            long limit = _frameCount > 0 ? _frameCount : start + shortest;
            end = Math.Min(start + shortest, limit);
            start = Math.Max(0, end - shortest);
        }

        int chunk = (int)Math.Ceiling(2 * _rate.ToDouble());
        long length = end - start;
        long pieces = (length + chunk - 1) / chunk;
        long first = start;
        for (long i = 0; i < pieces; i++)
        {
            int count = (int)((length / pieces) + (i < length % pieces ? 1 : 0));
            _queue.Enqueue(new RenderCacheWork(first, count, _revision));
            first += count;
        }
    }

    private static long Distance(RenderCostSpan span, long cursor) =>
        cursor < span.Start ? span.Start - cursor : cursor >= span.End ? cursor - span.End + 1 : 0;

    private void UpdateState() =>
        State = _current is { } current ? current.Pause.IsPauseRequested ? RenderCacheState.Paused : RenderCacheState.Working
            : _queue.Count > 0 && Mode != RenderCacheMode.Off ? RenderCacheState.Waiting
            : RenderCacheState.Idle;
}
