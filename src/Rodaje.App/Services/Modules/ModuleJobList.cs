using System.Diagnostics;
using Rodaje.App.Services.Operations;

namespace Rodaje.App.Services.Modules;

public enum ModuleJobState
{
    /// <summary>Esperando memoria de video o que el módulo abra.</summary>
    Waiting,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Un trabajo de módulo tal como lo ve la lista de Trabajos: etapa, avance, tiempo restante y cancelar. Se actualiza desde
/// cualquier hilo; la lista avisa en el de la interfaz.
/// </summary>
public sealed class ModuleJob
{
    private readonly CancellationTokenSource _cancel;
    private readonly ModuleJobList _owner;
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _runningSince;
    private volatile bool _cancelling;

    internal ModuleJob(ModuleJobList owner, string title, string moduleName, CancellationToken external)
    {
        _owner = owner;
        Title = title;
        ModuleName = moduleName;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(external);
        Token = _cancel.Token;
        Operation.SetCancellable(true);
        // Desde la fila o desde quien lanzó el trabajo: el módulo puede tardar en confirmar (hasta CancelGrace, 10 s).
        _ = Token.Register(OnCancelRequested);
    }

    public string Title { get; }

    public string ModuleName { get; }

    /// <summary>El avance con fases, tiempo restante suavizado y «último avance» que dibuja la vista común.</summary>
    public OperationProgress Operation { get; } = new();

    public ModuleJobState State { get; private set; } = ModuleJobState.Waiting;

    public double Progress { get; private set; }

    public string? Stage { get; private set; }

    /// <summary>El mensaje para la persona si falló (en lenguaje simple, sin trazas).</summary>
    public string? Error { get; private set; }

    public bool IsFinished => State is ModuleJobState.Completed or ModuleJobState.Failed or ModuleJobState.Cancelled;

    /// <summary>Se pidió cancelar y el módulo todavía no lo confirmó.</summary>
    public bool IsCancelling => _cancelling && !IsFinished;

    /// <summary>
    /// Barra sin porcentaje (en movimiento) mientras no hay avance que mostrar: esperando, iniciando el módulo (hasta 30 s
    /// en frío) o cancelando.
    /// </summary>
    public bool IsIndeterminate => !IsFinished && (State == ModuleJobState.Waiting || _cancelling);

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    /// <summary>Estimado a partir del ritmo desde que empezó a correr; <c>null</c> mientras no hay suficiente avance.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            double p = Progress;
            if (State != ModuleJobState.Running || p < 0.02 || p >= 1 || _runningSince == 0)
            {
                return null;
            }

            double seconds = Stopwatch.GetElapsedTime(_runningSince).TotalSeconds;
            return TimeSpan.FromSeconds(seconds * (1 - p) / p);
        }
    }

    public CancellationToken Token { get; }

    /// <summary>Texto corto de estado para la fila.</summary>
    public string StatusText => IsCancelling ? ModuleActivity.Cancelling : State switch
    {
        ModuleJobState.Waiting => Stage ?? "En espera",
        ModuleJobState.Running => $"{Stage ?? "Procesando"} · {Progress * 100:0} %" + (Remaining is { } r ? $" · quedan {FormatRemaining(r)}" : ""),
        ModuleJobState.Completed => "Terminado",
        ModuleJobState.Cancelled => "Cancelado",
        _ => Error ?? "Falló",
    };

    public void Cancel()
    {
        if (!IsFinished)
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void OnCancelRequested()
    {
        if (!IsFinished)
        {
            _cancelling = true;
            Operation.MarkCancelling();
            _owner.Notify();
        }
    }

    internal void Report(double fraction, string? stage)
    {
        if (IsFinished)
        {
            return;
        }

        Progress = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : Progress;
        Stage = stage ?? Stage;
        Operation.Report(Progress, stage);
        _owner.Notify();
    }

    internal void SetWaiting(string stage)
    {
        Stage = stage;
        Operation.Report(null, stage);
        _owner.Notify();
    }

    internal void SetRunning()
    {
        if (State == ModuleJobState.Waiting)
        {
            State = ModuleJobState.Running;
            Operation.Report(null, Stage ?? "Procesando");
            _runningSince = Stopwatch.GetTimestamp();
            _owner.Notify();
        }
    }

    internal void Finish(ModuleJobState state, string? error = null)
    {
        if (IsFinished)
        {
            return;
        }

        State = state;
        Error = error;
        if (state == ModuleJobState.Completed)
        {
            Progress = 1;
        }

        Operation.Complete();

        _cancel.Dispose();
        _owner.Finished(this);
    }

    private static string FormatRemaining(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Ceiling(t.TotalSeconds):0} s" : $"{(int)t.TotalMinutes} min {t.Seconds:00} s";
}

/// <summary>Los trabajos de módulos (lo más reciente primero). <see cref="Changed"/> salta en el contexto de la interfaz.</summary>
public sealed class ModuleJobList(SynchronizationContext? context)
{
    private readonly object _lock = new();
    private readonly List<ModuleJob> _jobs = [];
    private int _pendingNotify;

    public IReadOnlyList<ModuleJob> Jobs
    {
        get
        {
            lock (_lock)
            {
                return [.. _jobs];
            }
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_lock)
            {
                return _jobs.Count(j => !j.IsFinished);
            }
        }
    }

    public event EventHandler? Changed;

    /// <summary>Un trabajo terminó, falló o se canceló.</summary>
    public event EventHandler<ModuleJob>? JobFinished;

    [ThreadStatic]
    private static Action<ModuleJob>? t_capture;

    public ModuleJob Add(string title, string moduleName, CancellationToken cancellationToken = default)
    {
        var job = new ModuleJob(this, title, moduleName, cancellationToken);
        lock (_lock)
        {
            _jobs.Insert(0, job);
        }

        Notify();
        if (t_capture is { } capture)
        {
            t_capture = null;
            capture(job);
        }

        return job;
    }

    /// <summary>
    /// Entrega el siguiente trabajo que se añada <b>desde este hilo</b> mientras dure el <c>using</c>. Sirve a un diálogo para
    /// llegar al avance del trabajo de módulo que lanza (<c>ModuleManager.RunAsync</c> lo añade antes de su primer
    /// <c>await</c>): <c>using (jobs.CaptureNext(j => job = j)) { task = servicio.HacerAsync(…); }</c>.
    /// </summary>
    public static IDisposable CaptureNext(Action<ModuleJob> onJob)
    {
        ArgumentNullException.ThrowIfNull(onJob);
        t_capture = onJob;
        return new Release();
    }

    private sealed partial class Release : IDisposable
    {
        public void Dispose() => t_capture = null;
    }

    public bool ClearFinished()
    {
        lock (_lock)
        {
            if (_jobs.RemoveAll(j => j.IsFinished) == 0)
            {
                return false;
            }
        }

        Notify();
        return true;
    }

    internal void Finished(ModuleJob job)
    {
        Post(() => JobFinished?.Invoke(this, job));
        Notify();
    }

    // Los avances llegan muy seguidos: se junta en un solo aviso pendiente.
    internal void Notify()
    {
        if (Interlocked.Exchange(ref _pendingNotify, 1) == 1)
        {
            return;
        }

        Post(() =>
        {
            Volatile.Write(ref _pendingNotify, 0);
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Post(Action action)
    {
        if (context is null)
        {
            action();
        }
        else
        {
            context.Post(_ => action(), null);
        }
    }
}
