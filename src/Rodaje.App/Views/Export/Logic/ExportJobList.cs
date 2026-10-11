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

    public ExportRequest Request { get; }

    public bool ContinueWithoutMissing { get; }

    public IExportJob? Job { get; internal set; }

    public string OutputPath => Request.OutputPath;

    public string Title => Path.GetFileName(Request.OutputPath);

    public ExportProgress Progress => Job?.Progress ?? new ExportProgress(ExportJobState.Queued, 0, 0, 0, TimeSpan.Zero, null, 0);

    public ExportException? Failure => Job?.Failure;

    public BgraImage? Thumbnail => Job?.Thumbnail;

    public bool IsFinished => Progress.State.IsFinished();
}

/// <summary>
/// La cola de exportaciones: una a la vez (cada una con su copia del proyecto), las demás esperan. Los avisos de los
/// trabajos llegan desde sus hilos y se pasan al de la interfaz; <see cref="Changed"/> salta siempre ahí.
/// </summary>
public sealed partial class ExportJobList(IExportRunner runner, SynchronizationContext? context) : IDisposable
{
    private readonly List<ExportEntry> _entries = [];
    private int _pendingNotify;

    public IExportRunner Runner { get; } = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <summary>Lo más reciente primero.</summary>
    public IReadOnlyList<ExportEntry> Entries => _entries;

    public int ActiveCount => _entries.Count(e => !e.IsFinished);

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
        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    /// <summary>Repite una exportación terminada con los mismos datos; <paramref name="continueWithoutMissing"/> tras «Exportar sin él».</summary>
    public ExportEntry Retry(ExportEntry entry, bool continueWithoutMissing = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Remove(entry);
        return Enqueue(entry.Request, continueWithoutMissing || entry.ContinueWithoutMissing);
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

    /// <summary>Cancela lo que quede (al cerrar la app).</summary>
    public void Dispose()
    {
        foreach (ExportEntry entry in _entries)
        {
            entry.Job?.Cancel();
        }
    }

    private void StartNext()
    {
        if (_entries.Any(e => e.Job is { } job && !job.Progress.State.IsFinished()))
        {
            return;
        }

        // La más antigua que espera: la lista va de la más reciente a la más antigua.
        ExportEntry? next = _entries.LastOrDefault(e => e.Job is null);
        if (next is null)
        {
            return;
        }

        IExportJob started = Runner.Start(next.Request, next.ContinueWithoutMissing);
        next.Job = started;
        started.Changed += (_, _) => OnJobChanged();
        _ = WatchAsync(next, started);
    }

    private async Task WatchAsync(ExportEntry entry, IExportJob job)
    {
        await job.Completion.ConfigureAwait(false);
        Post(() =>
        {
            Finished?.Invoke(this, entry);
            StartNext();
            Changed?.Invoke(this, EventArgs.Empty);
        });
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
