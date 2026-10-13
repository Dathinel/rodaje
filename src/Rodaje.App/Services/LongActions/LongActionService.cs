using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Format;

namespace Rodaje.App.Services.LongActions;

/// <summary>Una acción larga en marcha, tal como se muestra en su botón y en la barra de avisos.</summary>
/// <param name="Progress">De 0 a 1, o nulo si no se sabe cuánto falta (barra indeterminada).</param>
public sealed record LongActionStatus(string ActionId, string Title, string Message, double? Progress, bool CanCancel)
{
    /// <summary>«42 %», o vacío si el avance no se conoce.</summary>
    public string ProgressText => Progress is { } p ? string.Create(SpanishNumbers.Format, $"{Math.Floor(p * 100):0} %") : "";
}

/// <summary>
/// Las acciones largas (color automático, estabilizar, quitar silencios, subtítulos, texto a voz…), sin WinUI. Cada una
/// se marca en marcha desde el clic: su botón se ve ocupado y no se puede lanzar dos veces, la barra de avisos muestra el
/// avance (con porcentaje si se conoce) y «Cancelar» si la acción lo admite. Al terminar queda el aviso de la acción; si no
/// dejó ninguno pero cambió el proyecto, uno con «Deshacer».
/// </summary>
public sealed partial class LongActionService
{
    private readonly EditorSession _session;
    private readonly Lock _lock = new();
    private readonly List<LongActionScope> _running = [];
    private readonly Action<Exception> _reportFailure;

    /// <param name="reportFailure">
    /// Quién lleva al informe la excepción de una acción que falla; por omisión, <see cref="UiTask.Report"/> sin aviso
    /// propio (el aviso lo da <see cref="RunAsync"/>). Las pruebas pasan el suyo.
    /// </param>
    public LongActionService(EditorSession session, Action<Exception>? reportFailure = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _reportFailure = reportFailure ?? (static ex => UiTask.Report(ex, notify: false));
    }

    /// <summary>Cambió algo de lo que está en marcha. Puede llegar desde cualquier hilo.</summary>
    public event EventHandler? Changed;

    /// <summary>Lo que está en marcha, en el orden en que empezó.</summary>
    public IReadOnlyList<LongActionStatus> Running
    {
        get
        {
            lock (_lock)
            {
                return [.. _running.Select(s => s.Status)];
            }
        }
    }

    /// <summary>La última que empezó y sigue en marcha: la que muestra la barra de avisos.</summary>
    public LongActionStatus? Current
    {
        get
        {
            lock (_lock)
            {
                return _running.Count == 0 ? null : _running[^1].Status;
            }
        }
    }

    public bool IsRunning(string actionId)
    {
        lock (_lock)
        {
            return _running.Exists(s => s.ActionId == actionId);
        }
    }

    /// <summary>Marca la acción en marcha; nulo si ya lo estaba (el segundo clic no hace nada).</summary>
    public LongActionScope? TryBegin(string actionId, string title, string message = "", bool canCancel = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(actionId);
        ArgumentNullException.ThrowIfNull(title);
        LongActionScope scope;
        lock (_lock)
        {
            if (_running.Exists(s => s.ActionId == actionId))
            {
                return null;
            }

            scope = new LongActionScope(this, actionId, title, message ?? "", canCancel);
            _running.Add(scope);
        }

        RaiseChanged();
        return scope;
    }

    /// <summary>Pide cancelar la acción si lo admite; devuelve si se pidió.</summary>
    public bool Cancel(string actionId)
    {
        LongActionScope? scope;
        lock (_lock)
        {
            scope = _running.Find(s => s.ActionId == actionId);
        }

        return scope?.RequestCancel() == true;
    }

    /// <summary>
    /// Corre <paramref name="work"/> marcada en marcha. Devuelve <c>false</c> sin hacer nada si ya estaba en marcha. Un fallo
    /// queda como aviso de error con el texto fijo de <see cref="UnexpectedErrorNotice"/> (el mensaje de la excepción, técnico
    /// y a menudo en inglés, va solo al informe) y una cancelación como «Cancelado»; nada se
    /// escapa.
    /// </summary>
    public async Task<bool> RunAsync(string actionId, string title, Func<LongActionScope, Task> work, bool canCancel = false, string message = "")
    {
        ArgumentNullException.ThrowIfNull(work);
        if (TryBegin(actionId, title, message, canCancel) is not { } scope)
        {
            return false;
        }

        int notices = 0;
        void Count(object? sender, EditorNotification e) => notices++;
        int position = _session.History.Count;
        string? undo = _session.UndoDescription;
        _session.Notified += Count;
        try
        {
            await work(scope).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (scope.Token.IsCancellationRequested)
        {
            _session.Notify(NotificationSeverity.Informational, title, "Cancelado.");
        }
#pragma warning disable CA1031 // Una acción que falla no debe cerrar la app: se avisa.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _reportFailure(ex);
            _session.Notify(NotificationSeverity.Error, title, UnexpectedErrorNotice.Describe(null));
        }
        finally
        {
            _session.Notified -= Count;
            scope.Dispose();
        }

        if (notices == 0 && _session.CanUndo && (_session.History.Count != position || _session.UndoDescription != undo))
        {
            _session.Notify(NotificationSeverity.Success, title, $"Listo: {_session.UndoDescription}.",
                new NotificationAction("Deshacer", () => _session.Undo()));
        }

        return true;
    }

    /// <summary>
    /// Espera <paramref name="work"/>, a la que ya se le pidió cancelar (por ejemplo, al cerrar su diálogo): un módulo puede
    /// tardar hasta 10 s en confirmarlo, y mientras tanto la barra de avisos dice «Cancelando…» con la barra en movimiento.
    /// Usa <paramref name="scope"/> si la acción ya tiene uno; si no, abre uno propio con <paramref name="title"/>.
    /// </summary>
    public async Task ShowCancellingAsync(string title, Task work, LongActionScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(work);
        if (work.IsCompleted)
        {
            await work.ConfigureAwait(true);
            return;
        }

        LongActionScope? own = scope is null ? TryBegin("cancelar:" + title, title) : null;
        (scope ?? own)?.MarkCancelling();
        try
        {
            await work.ConfigureAwait(true);
        }
        finally
        {
            own?.Dispose();
        }
    }

    internal void End(LongActionScope scope)
    {
        bool removed;
        lock (_lock)
        {
            removed = _running.Remove(scope);
        }

        if (removed)
        {
            RaiseChanged();
        }
    }

    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Una acción en marcha: su avance, su mensaje y su cancelación. <see cref="Dispose"/> la da por terminada.</summary>
public sealed partial class LongActionScope : IDisposable
{
    // Menos de medio punto no se ve en la barra: no vale la pena despertar a la interfaz.
    private const double ProgressStep = 0.005;

    private readonly LongActionService _owner;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Lock _lock = new();
    private LongActionStatus _status;
    private bool _disposed;
    private bool _cancelling;

    internal LongActionScope(LongActionService owner, string actionId, string title, string message, bool canCancel)
    {
        _owner = owner;
        ActionId = actionId;
        Token = _cancel.Token;
        _status = new LongActionStatus(actionId, title, message, null, canCancel);
    }

    public string ActionId { get; }

    public CancellationToken Token { get; }

    public LongActionStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>Para pasar a lo que ya informa con <see cref="IProgress{T}"/>; llama a <see cref="Report"/> en el mismo hilo.</summary>
    public IProgress<double> Progress => new SyncProgress(this);

    /// <summary>El avance (de 0 a 1; nulo para «no se sabe») y, si cambia, el mensaje.</summary>
    public void Report(double? progress, string? message = null)
    {
        double? value = progress is { } p && double.IsFinite(p) ? Math.Clamp(p, 0, 1) : null;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_cancelling)
            {
                return;
            }

            LongActionStatus old = _status;
            bool sameMessage = message is null || message == old.Message;
            bool sameProgress = (value, old.Progress) switch
            {
                (null, null) => true,
                ({ } a, { } b) => Math.Abs(a - b) < ProgressStep && !(a >= 1 && b < 1),
                _ => false,
            };
            if (sameMessage && sameProgress)
            {
                return;
            }

            _status = old with { Progress = value, Message = message ?? old.Message };
        }

        _owner.RaiseChanged();
    }

    /// <summary>Si «Cancelar» se ofrece desde ahora (por ejemplo, solo mientras analiza).</summary>
    public void SetCancellable(bool canCancel)
    {
        lock (_lock)
        {
            if (_disposed || _cancelling || _status.CanCancel == canCancel)
            {
                return;
            }

            _status = _status with { CanCancel = canCancel };
        }

        _owner.RaiseChanged();
    }

    internal bool RequestCancel()
    {
        lock (_lock)
        {
            if (_disposed || !_status.CanCancel)
            {
                return false;
            }
        }

        _cancel.Cancel();
        MarkCancelling();
        return true;
    }

    /// <summary>
    /// Desde ahora muestra «Cancelando…» con la barra en movimiento y sin botón, hasta que termine: lo que corre puede
    /// tardar en confirmar la cancelación (un módulo de IA, hasta 10 s). Los avances que lleguen después no lo tapan.
    /// </summary>
    public void MarkCancelling()
    {
        lock (_lock)
        {
            if (_disposed || _cancelling)
            {
                return;
            }

            _cancelling = true;
            _status = _status with { Message = Modules.ModuleActivity.Cancelling, Progress = null, CanCancel = false };
        }

        _owner.RaiseChanged();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _owner.End(this);
        _cancel.Dispose();
    }

    private sealed partial class SyncProgress(LongActionScope scope) : IProgress<double>
    {
        public void Report(double value) => scope.Report(value);
    }
}
