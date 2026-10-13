using Rodaje.Core.Model;
using Rodaje.Export;
using Rodaje.Media.Export;
using Rodaje.Media.Video;

namespace Rodaje.App.Views.Export;

/// <summary>Una exportación de la cola tal como la ve la interfaz (hilo de la interfaz).</summary>
public sealed class ExportEntry
{
    internal ExportEntry(ExportRequest request, bool continueWithoutMissing)
    {
        Request = request;
        ContinueWithoutMissing = continueWithoutMissing;
    }

    /// <summary>
    /// Lo pedido. Al completarse, la copia del proyecto se suelta (<see cref="IsReleased"/>): queda un proyecto vacío con
    /// su identificador y nombre, y siguen los ajustes, el rango y la salida.
    /// </summary>
    public ExportRequest Request { get; private set; }

    public bool ContinueWithoutMissing { get; }

    public IExportJob? Job { get; internal set; }

    /// <summary>Completada y sin la copia del proyecto: solo queda el resumen (avance, miniatura y salida).</summary>
    public bool IsReleased { get; private set; }

    public string OutputPath => Request.OutputPath;

    public string Title => Path.GetFileName(Request.OutputPath);

    public ExportProgress Progress => Job?.Progress ?? new ExportProgress(ExportJobState.Queued, 0, 0, 0, TimeSpan.Zero, null, 0);

    public ExportException? Failure => Job?.Failure;

    public BgraImage? Thumbnail => Job?.Thumbnail;

    public bool IsFinished => Progress.State.IsFinished();

    /// <summary>
    /// Una exportación completada ya no necesita el proyecto: se cambia el trabajo por su resumen y se suelta la copia. Las
    /// fallidas y las canceladas la conservan, porque se pueden repetir aunque luego se abra otro proyecto.
    /// </summary>
    internal void Release()
    {
        if (IsReleased || Job is not { } job || job.Progress.State != ExportJobState.Completed)
        {
            return;
        }

        Project project = Request.Project;
        Request = Request with { Project = new Project { Id = project.Id, Name = project.Name } };
        Job = new FinishedJob(Request, job.Progress, job.Failure, job.Thumbnail);
        job.Dispose();
        IsReleased = true;
    }

    /// <summary>Lo que queda de un trabajo completado: no se mueve más.</summary>
    private sealed partial class FinishedJob(ExportRequest request, ExportProgress progress, ExportException? failure, BgraImage? thumbnail) : IExportJob
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ExportRequest Request { get; } = request;

        public ExportProgress Progress { get; } = progress;

        public ExportException? Failure { get; } = failure;

        public BgraImage? Thumbnail { get; } = thumbnail;

        public Task Completion => Task.CompletedTask;

        public void Cancel()
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// La cola de exportaciones: una a la vez, o dos si sobra equipo (<see cref="ExportConcurrency"/>), cada una con su copia
/// del proyecto (que se suelta al completarse, ver <see cref="ExportEntry.Release"/>); las demás esperan en un orden que se puede cambiar, y la cola se puede pausar. Con <see cref="Store"/> se
/// guarda en disco cada vez que cambia. Los avisos de los trabajos llegan desde sus hilos y se pasan al de la interfaz;
/// <see cref="Changed"/> salta siempre ahí.
/// </summary>
public sealed partial class ExportJobList(IExportRunner runner, SynchronizationContext? context) : IDisposable
{
    private readonly List<ExportEntry> _entries = [];
    private int _pendingNotify;
    private bool _paused;
    private bool _disposed;

    public IExportRunner Runner { get; } = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <summary>Cuántas a la vez; por omisión, una.</summary>
    public ExportConcurrency Concurrency { get; init; } = ExportConcurrency.OneAtATime;

    /// <summary>Dónde se guarda la cola para reanudarla al volver a abrir; nulo, no se guarda.</summary>
    public ExportQueueStore? Store { get; init; }

    /// <summary>Lo más reciente primero.</summary>
    public IReadOnlyList<ExportEntry> Entries => _entries;

    public int ActiveCount => _entries.Count(e => !e.IsFinished);

    /// <summary>Las que esperan, en el orden en que van a empezar.</summary>
    public IReadOnlyList<ExportEntry> Waiting => [.. Enumerable.Reverse(_entries).Where(e => e.Job is null)];

    /// <summary>Pausada: lo que está en marcha termina, pero no empieza nada nuevo.</summary>
    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (_paused == value)
            {
                return;
            }

            _paused = value;
            StartNext();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Lugar en la cola (1 = la siguiente en empezar), o 0 si no espera.</summary>
    public int QueuePosition(ExportEntry entry)
    {
        int index = Waiting.ToList().IndexOf(entry);
        return index + 1;
    }

    /// <summary>Adelanta (<paramref name="earlier"/>) o atrasa una que espera; <c>false</c> si no se pudo.</summary>
    public bool Move(ExportEntry entry, bool earlier)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Job is not null)
        {
            return false;
        }

        // La lista va de la más reciente a la más antigua: empezar antes es ir hacia el final.
        int index = _entries.IndexOf(entry);
        int step = earlier ? 1 : -1;
        int other = index + step;
        while (other >= 0 && other < _entries.Count && _entries[other].Job is not null)
        {
            other += step;
        }

        if (index < 0 || other < 0 || other >= _entries.Count)
        {
            return false;
        }

        (_entries[index], _entries[other]) = (_entries[other], _entries[index]);
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Varias exportaciones de una vez (varias versiones), en ese orden.</summary>
    public IReadOnlyList<ExportEntry> EnqueueRange(IEnumerable<ExportRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var added = new List<ExportEntry>();
        foreach (ExportRequest request in requests)
        {
            var entry = new ExportEntry(request, request.ContinueWithoutMissing);
            _entries.Insert(0, entry);
            added.Add(entry);
        }

        StartNext();
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    /// <summary>Algo cambió en la cola o en el avance de un trabajo.</summary>
    public event EventHandler? Changed;

    /// <summary>Un trabajo llegó a un estado final (terminado, fallido o cancelado).</summary>
    public event EventHandler<ExportEntry>? Finished;

    public ExportEntry Enqueue(ExportRequest request, bool continueWithoutMissing = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entry = new ExportEntry(request, continueWithoutMissing);
        _entries.Insert(0, entry);
        StartNext();
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    /// <summary>
    /// Repite una exportación terminada con los mismos ajustes; <paramref name="continueWithoutMissing"/> tras «Exportar sin él».
    /// Con <paramref name="current"/> (el proyecto abierto) y si es el mismo proyecto con la misma secuencia, exporta su estado
    /// de ahora: lo que se arregló después del fallo (un clip o medio quitado) ya no está en la copia que se repite.
    /// </summary>
    /// <exception cref="InvalidOperationException">Una completada (<see cref="ExportEntry.IsReleased"/>) sin ese proyecto abierto.</exception>
    public ExportEntry Retry(ExportEntry entry, bool continueWithoutMissing = false, Project? current = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ExportRequest request = entry.Request;
        if (current is { Id.Length: > 0 } && current.Id == request.Project.Id && current.Sequences.Exists(s => s.Id == request.SequenceId))
        {
            request = request with { Project = current.Clone() };
        }
        else if (entry.IsReleased)
        {
            throw new InvalidOperationException("La exportación ya se completó y soltó la copia del proyecto; solo se repite con ese proyecto abierto.");
        }

        Remove(entry);
        return Enqueue(request, continueWithoutMissing || entry.ContinueWithoutMissing);
    }

    public void Cancel(ExportEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Job is { } job)
        {
            job.Cancel();
        }
        else if (_entries.Remove(entry))
        {
            Persist();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Quita de la lista una exportación terminada.</summary>
    public void Remove(ExportEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsFinished && _entries.Remove(entry))
        {
            entry.Job?.Dispose();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ClearFinished()
    {
        foreach (ExportEntry entry in _entries.Where(e => e.IsFinished).ToArray())
        {
            _entries.Remove(entry);
            entry.Job?.Dispose();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Al cerrar la app: guarda lo que no terminó (la que estaba a medias vuelve a empezar al reanudar) y lo cancela.
    /// </summary>
    public void Dispose()
    {
        Persist();
        _disposed = true;
        foreach (ExportEntry entry in _entries)
        {
            entry.Job?.Cancel();
        }
    }

    private void StartNext()
    {
        while (!_paused && !_disposed)
        {
            // La más antigua que espera: la lista va de la más reciente a la más antigua.
            ExportEntry? next = _entries.LastOrDefault(e => e.Job is null);
            ExportSettings[] running = [.. _entries.Where(e => e.Job is { } job && !job.Progress.State.IsFinished()).Select(e => e.Request.Settings)];
            if (next is null || !Concurrency.Allows(running, next.Request.Settings))
            {
                return;
            }

            IExportJob started = Runner.Start(next.Request, next.ContinueWithoutMissing);
            next.Job = started;
            started.Changed += (_, _) => OnJobChanged();
            _ = WatchAsync(next, started);
        }
    }

    private async Task WatchAsync(ExportEntry entry, IExportJob job)
    {
        await job.Completion.ConfigureAwait(false);
        Post(() =>
        {
            entry.Release();
            Finished?.Invoke(this, entry);
            StartNext();
            Persist();
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Persist()
    {
        if (Store is null || _disposed)
        {
            return;
        }

        try
        {
            Store.Save(Enumerable.Reverse(_entries).Where(e => !e.IsFinished).Select(e => (e.Request, e.ContinueWithoutMissing)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Llega desde el hilo del trabajo: se junta en un solo aviso pendiente para no inundar la interfaz.
    private void OnJobChanged()
    {
        if (Interlocked.Exchange(ref _pendingNotify, 1) == 0)
        {
            Post(() =>
            {
                Volatile.Write(ref _pendingNotify, 0);
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    private void Post(Action action)
    {
        if (context is null)
        {
            action();
        }
        else
        {
            context.Post(static state => ((Action)state!)(), action);
        }
    }
}
