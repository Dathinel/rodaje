using System.Text.Json;
using System.Threading.Channels;
using Dathinel.Sala;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;
using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// La edición compartida (colaboracion.md §3): convierte cada paso local en un parche, lo manda a la sala, valida los de los demás
/// si este equipo hospeda, y aplica en orden los que la sala acepta. Mantiene dos mundos: el <b>confirmado</b> (<c>_mirror</c>: lo
/// que la sala ordenó, igual en todos los equipos) y el <b>vivo</b> (<c>live</c>: lo que se ve y se edita, con los cambios propios
/// aún sin respuesta). Si algo propio no entra, el vivo vuelve al confirmado y se avisa. Si algo ordenado no se puede aplicar
/// (no debería pasar), se avisa de que la copia dejó de coincidir y se sale de la sala en vez de seguir editando sobre un estado
/// roto.
/// </summary>
/// <remarks>
/// Todo lo que toca <c>live</c> pasa por <c>ui</c>, que lo ejecuta de a uno y en orden en el hilo de la interfaz; lo confirmado se
/// protege con su propio candado y se actualiza en el hilo de Sala, dentro del evento, para que el siguiente parche se valide ya
/// sobre él. Cada paso propio se manda tal cual salió (varios sin respuesta a la vez, en orden): no hay cambios sin mandar cuando
/// llega uno ajeno, así que el estado base es siempre el vivo.
/// </remarks>
public sealed partial class CollabEngine : ICollabPayloadHandler, ICollabHistory, IDisposable
{
    private sealed class Pending(Patch patch, SharedState before, SharedState after, string description, bool isUndo, bool isRedo)
    {
        public Patch Patch { get; set; } = patch;

        public SharedState Before { get; set; } = before;

        public SharedState After { get; set; } = after;

        // La sala ya lo ordenó (está en lo confirmado); solo falta que la interfaz lo dé por bueno.
        public bool Confirmed { get; set; }

        public string Description { get; } = description;

        public bool IsUndo { get; } = isUndo;

        public bool IsRedo { get; } = isRedo;

        public IReadOnlySet<string> Keys { get; set; } = new HashSet<string>();

        public TaskCompletionSource<Guid> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Project _live;
    private readonly RoomService _room;
    private readonly CollabPresence _presence;
    private readonly Func<Action, Task> _ui;
    private readonly Action _liveChanged;
    private readonly Action<string, bool> _notice;
    private readonly Func<bool> _isBusy;
    private readonly Queue<Patch> _deferred = new();
    private bool _retryScheduled;
    private readonly CollabHistory _history = new();
    private readonly Lock _m = new();
    private readonly Channel<Pending> _outbox = Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Ordenado> _early = [];
    private readonly List<Pending> _pending = [];

    // El mundo confirmado: bajo _m.
    private Project _mirror = new();
    private ElementVersions _mirrorVersions = new();
    private long _seq;
    private bool _started;

    // El mundo vivo: solo desde ui.
    private ElementVersions _liveVersions = new();
    private SharedState? _baseline;

    /// <param name="live">El proyecto que se ve y se edita; solo se toca desde <paramref name="ui"/>.</param>
    /// <param name="ui">Ejecuta una acción en el hilo de la interfaz, de a una y en orden.</param>
    /// <param name="liveChanged">Se llama (en <paramref name="ui"/>) tras cambiar <paramref name="live"/> por dentro: la vista se rehace.</param>
    /// <param name="notice">Un aviso para la persona (texto, si es un problema).</param>
    /// <param name="isBusy">Hay un gesto abierto (arrastrando un clip): lo ajeno espera a que termine para no pisarlo.</param>
    public CollabEngine(Project live, RoomService room, CollabPresence presence, Func<Action, Task> ui, Action liveChanged, Action<string, bool> notice, Func<bool>? isBusy = null)
    {
        _isBusy = isBusy ?? (() => false);
        _live = live;
        _room = room;
        _presence = presence;
        _ui = ui;
        _liveChanged = liveChanged;
        _notice = notice;
        UiTask.Run(Task.Run(SendLoopAsync));
    }

    /// <summary>Lo confirmado y lo vivo dejaron de coincidir y no hay arreglo seguro: la persona tiene que volver a entrar.</summary>
    public event Action<string>? Desynced;

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    public string? UndoDescription => _history.UndoDescription;

    public string? RedoDescription => _history.RedoDescription;

    public void Dispose()
    {
        _outbox.Writer.TryComplete();
        _stop.Cancel();
        _stop.Dispose();
    }

    // ---------- arranque ----------

    /// <summary>El anfitrión empieza: lo confirmado es el proyecto abierto, sin versiones, en el orden <paramref name="seq"/>.</summary>
    public void StartAsHost(long seq = 0) => Start(_live.Clone(), new ElementVersions(), seq);

    /// <summary>
    /// El invitado empieza con lo que mandó el anfitrión (su copia ya abierta como <c>live</c>, sus versiones y su orden): los
    /// parches ordenados hasta <paramref name="seq"/> ya vienen dentro; los que llegaron mientras tanto se aplican ahora.
    /// </summary>
    public void StartAsGuest(Project received, ElementVersions versions, long seq) => Start(received, versions, seq);

    private void Start(Project mirror, ElementVersions versions, long seq)
    {
        List<Ordenado> early;
        lock (_m)
        {
            _mirror = mirror;
            _mirrorVersions = versions.Clone();
            _seq = seq;
            _liveVersions = versions.Clone();
            _started = true;
            early = [.. _early];
            _early.Clear();
        }

        _ = _ui(() => _baseline = SharedState.Capture(_live));
        foreach (Ordenado o in early)
        {
            Apply(o);
        }
    }

    /// <summary>Lo confirmado listo para un invitado que entra: el proyecto sin rutas con sus versiones, y el orden en que está.</summary>
    public (string Json, long Seq) CaptureForGuest()
    {
        lock (_m)
        {
            return (ProjectWire.ToJson(_mirror, _mirrorVersions), _seq);
        }
    }

    // ---------- lo propio ----------

    /// <summary>Un paso local terminó (fuera de un gesto): sale como parche si cambió algo compartido. Solo desde <c>ui</c>.</summary>
    public void OnLocalChange(string description)
    {
        if (_baseline is null)
        {
            return;
        }

        if (_room.State.MyRole != Rol.Editar)
        {
            string host = _room.State.Members.FirstOrDefault(m => m.IsHost)?.Alias ?? "quien hospeda la sala";
            ConvergeToConfirmed();
            _notice($"Solo puedes mirar en esta sala. Pídele a {host} que te deje editar.", true);
            return;
        }

        SharedState after = SharedState.Capture(_live);
        Patch? patch = PatchDiff.Create(_baseline, after, _liveVersions, Header(description));
        if (patch is null)
        {
            _baseline = after;
            return;
        }

        Enqueue(new Pending(patch, _baseline, after, description, isUndo: false, isRedo: false), after);
    }

    /// <summary>Deshace el último paso propio (solo lo propio, y solo si nadie lo tocó después).</summary>
    public bool Undo() => Replay(redo: false);

    public bool Redo() => Replay(redo: true);

    private bool Replay(bool redo)
    {
        if (_baseline is null)
        {
            return false;
        }

        string text = redo ? "Rehacer" : "Deshacer";
        SharedState current = SharedState.Capture(_live);
        HistoryOutcome outcome = redo
            ? _history.PlanRedo(current, _liveVersions, Header(text))
            : _history.PlanUndo(current, _liveVersions, Header(text));
        if (outcome.Plan is not { } plan)
        {
            if (outcome.Failure == HistoryFailure.ChangedByOthers)
            {
                _notice($"No se puede {(redo ? "rehacer" : "deshacer")}: otra persona cambió ese elemento.", true);
                if (!redo)
                {
                    _history.DiscardUndo();
                }
            }

            return false;
        }

        PatchResult applied = PatchApply.TryApply(_live, _liveVersions, plan.Patch);
        if (!applied.IsAccepted)
        {
            _notice($"No se puede {(redo ? "rehacer" : "deshacer")}: " + applied.Rejection!.Message, true);
            return false;
        }

        SharedState after = SharedState.Capture(_live);
        Pending pending = new(plan.Patch, current, after, plan.Description, isUndo: !redo, isRedo: redo);
        _baseline = after;
        _liveChanged();
        Enqueue(pending, after, alreadyApplied: true);
        return true;
    }

    private void Enqueue(Pending pending, SharedState after, bool alreadyApplied = false)
    {
        if (!alreadyApplied)
        {
            _liveVersions.Advance(pending.Patch);
        }
        else
        {
            // TryApply ya avanzó las versiones del plan.
        }

        pending.Keys = PatchReservations.Required(pending.Patch, _live);
        _baseline = after;
        lock (_m)
        {
            _pending.Add(pending);
        }

        _outbox.Writer.TryWrite(pending);
    }

    private PatchHeader Header(string description)
    {
        long order;
        lock (_m)
        {
            order = _seq;
        }

        return new PatchHeader(Guid.NewGuid().ToString("N"), order, _room.State.MyKey, description);
    }

    /// <summary>Manda los pasos propios de a uno y en orden: el segundo parte de que el primero ya está (sus versiones).</summary>
    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (Pending pending in _outbox.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await SendOneAsync(pending).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendOneAsync(Pending pending)
    {
        bool dropped;
        lock (_m)
        {
            dropped = !_pending.Contains(pending);
        }

        if (dropped)
        {
            // Se deshizo (lo vivo volvió a lo confirmado) antes de salir: no se manda lo que la persona ya no ve.
            pending.Sent.TrySetResult(Guid.Empty);
            _presence.Done(pending.Keys);
            return;
        }

        try
        {
            ReservationBlock? blocked = await _presence.EnsureAsync(pending.Keys, _stop.Token).ConfigureAwait(false);
            if (blocked is not null)
            {
                pending.Sent.TrySetResult(Guid.Empty);
                await _ui(() => Revert(pending, blocked.Describe())).ConfigureAwait(false);
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(PatchWire.ToJson(pending.Patch));
            Guid id = await _room.SendAsync(doc.RootElement, pending.Keys, _stop.Token).ConfigureAwait(false);
            pending.Sent.TrySetResult(id);
        }
        catch (Exception ex) when (ex is CollabException or JsonException or OperationCanceledException or ArgumentException)
        {
            pending.Sent.TrySetResult(Guid.Empty);
            if (ex is not OperationCanceledException)
            {
                string text = ex is CollabException ? ex.Message : CollabMessages.ForUnexpected();
                await _ui(() => Revert(pending, text)).ConfigureAwait(false);
            }
        }
    }

    // ---------- ICollabPayloadHandler ----------

    /// <summary>Solo en el anfitrión: el parche entra si es válido, las reservas son del autor y cabe sobre lo confirmado.</summary>
    public ValueTask<Veredicto> ValidateAsync(Miembro author, JsonElement payload, CancellationToken cancellationToken)
    {
        PatchParseResult parsed = PatchWire.TryParse(payload.GetRawText());
        if (parsed.Patch is not { } patch)
        {
            return ValueTask.FromResult(Veredicto.No("no-valido", parsed.Rejection?.Message ?? "El cambio no es válido."));
        }

        Project candidate;
        ElementVersions versions;
        IReadOnlySet<string> required;
        lock (_m)
        {
            if (!_started)
            {
                return ValueTask.FromResult(Veredicto.No("no-valido", "La sala todavía no está lista."));
            }

            required = PatchReservations.Required(patch, _mirror);
            candidate = _mirror.Clone();
            versions = _mirrorVersions.Clone();
        }

        IReadOnlyDictionary<string, string> owners = _room.Reservations;
        foreach (string key in required)
        {
            if (!owners.TryGetValue(key, out string? owner) || owner != author.Clave)
            {
                return ValueTask.FromResult(Veredicto.No("reserva", $"La reserva «{key}» no es tuya."));
            }
        }

        PatchResult result = PatchApply.TryApply(candidate, versions, patch);
        if (result.IsAccepted)
        {
            return ValueTask.FromResult(Veredicto.Si);
        }

        string reason = result.Rejection!.Reason == PatchRejectionReason.VersionConflict ? "conflicto" : "no-valido";
        return ValueTask.FromResult(Veredicto.No(reason, result.Rejection.Message));
    }

    /// <summary>En todos y en orden: lo ordenado entra en lo confirmado (aquí, en el hilo de Sala) y después en lo vivo (en <c>ui</c>).</summary>
    public void Apply(Ordenado ordered)
    {
        Patch? patch = null;
        string? desync = null;
        lock (_m)
        {
            if (!_started)
            {
                _early.Add(ordered);
                return;
            }

            if (ordered.Seq <= _seq)
            {
                return;
            }

            if (ordered.Seq != _seq + 1)
            {
                desync = "Faltan cambios de la sala entre los que llegaron.";
            }
            else
            {
                PatchParseResult parsed = PatchWire.TryParse(ordered.Datos.GetRawText());
                if (parsed.Patch is null)
                {
                    desync = parsed.Rejection?.Message ?? "Llegó un cambio que no se entiende.";
                }
                else
                {
                    patch = parsed.Patch;
                    PatchResult result = PatchApply.TryApply(_mirror, _mirrorVersions, patch);
                    if (!result.IsAccepted)
                    {
                        desync = result.Rejection!.Message;
                    }
                    else
                    {
                        _seq = ordered.Seq;
                        if (_pending.FirstOrDefault(p => p.Patch.Id == patch.Id) is { } own)
                        {
                            own.Confirmed = true;
                        }
                    }
                }
            }
        }

        if (desync is not null || patch is null)
        {
            Desynced?.Invoke(desync ?? "Llegó un cambio que no se entiende.");
            return;
        }

        Patch ordered1 = patch;
        _ = _ui(() => ApplyToLive(ordered1));
    }

    /// <summary>El anfitrión no aceptó un paso propio: lo vivo vuelve a lo confirmado y se dice por qué.</summary>
    public void Rejected(Rechazo rejection)
    {
        UiTask.Run(Task.Run(async () =>
        {
            Pending? match;
            lock (_m)
            {
                match = _pending.FirstOrDefault(p => p.Sent.Task.IsCompletedSuccessfully && p.Sent.Task.Result == rejection.Id);
            }

            if (match is null)
            {
                // La respuesta puede adelantarse al regreso de EnviarAsync: se espera un momento a que el paso apunte su id.
                foreach (Pending p in SnapshotPending().Where(p => !p.Sent.Task.IsCompleted))
                {
                    if (await Task.WhenAny(p.Sent.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) == p.Sent.Task && p.Sent.Task.Result == rejection.Id)
                    {
                        match = p;
                        break;
                    }
                }
            }

            if (match is not null)
            {
                await _ui(() => Revert(match, CollabMessages.Rejected(rejection.Motivo, rejection.Detalle))).ConfigureAwait(false);
            }
        }));
    }

    private List<Pending> SnapshotPending()
    {
        lock (_m)
        {
            return [.. _pending];
        }
    }

    // ---------- lo vivo (ui) ----------

    private void ApplyToLive(Patch patch)
    {
        Pending? own;
        lock (_m)
        {
            own = _pending.FirstOrDefault(p => p.Patch.Id == patch.Id);
            if (own is not null)
            {
                _pending.Remove(own);
            }
        }

        if (own is not null)
        {
            // Lo propio ya está en lo vivo: solo se confirma.
            _presence.Done(own.Keys);
            if (own.IsUndo)
            {
                _history.CommitUndo();
            }
            else if (own.IsRedo)
            {
                _history.CommitRedo();
            }
            else
            {
                _history.Record(own.Before, own.After, own.Description);
            }

            return;
        }

        if (_deferred.Count > 0 || _isBusy())
        {
            _deferred.Enqueue(patch);
            ScheduleRetry();
            return;
        }

        ApplyRemote(patch);
    }

    private void ScheduleRetry()
    {
        if (_retryScheduled)
        {
            return;
        }

        _retryScheduled = true;
        UiTask.Run(RetryLaterAsync());
    }

    private async Task RetryLaterAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        await _ui(() =>
        {
            _retryScheduled = false;
            if (_isBusy())
            {
                ScheduleRetry();
                return;
            }

            while (_deferred.TryDequeue(out Patch? next))
            {
                ApplyRemote(next);
            }
        }).ConfigureAwait(false);
    }

    private void ApplyRemote(Patch patch)
    {
        PatchResult result = PatchApply.TryApply(_live, _liveVersions, patch);
        if (!result.IsAccepted)
        {
            // Pasa cuando lo ajeno choca con algo propio sin responder: se vuelve a lo confirmado, que ya tiene el cambio ajeno.
            ConvergeToConfirmed();
            _notice("Alguien cambió lo mismo que tú casi a la vez: se quedó su cambio y el tuyo se deshizo.", true);
            return;
        }

        _baseline = SharedState.Capture(_live);
        _liveChanged();
    }

    private void Revert(Pending pending, string text)
    {
        List<Pending> later;
        lock (_m)
        {
            int index = _pending.IndexOf(pending);
            if (index < 0)
            {
                return;
            }

            later = _pending.GetRange(index + 1, _pending.Count - index - 1);
            _pending.RemoveAt(index);
        }

        _presence.Done(pending.Keys);
        if (!ResetToConfirmed())
        {
            return;
        }

        // Solo se pierde lo rechazado: lo propio que venía detrás se vuelve a montar sobre lo confirmado y, si ya no cabe, se avisa.
        List<Pending> lost = [];
        foreach (Pending next in later.Where(p => !p.Confirmed))
        {
            if (!Reproject(next))
            {
                lost.Add(next);
            }
        }

        if (lost.Count > 0)
        {
            lock (_m)
            {
                _pending.RemoveAll(lost.Contains);
            }

            foreach (Pending p in lost)
            {
                _presence.Done(p.Keys);
            }
        }

        _baseline = SharedState.Capture(_live);
        _liveChanged();
        string also = lost.Count == 0 ? "" : $" Tampoco entró lo que hiciste después y dependía de eso ({string.Join(", ", lost.Select(p => p.Description))}).";
        _notice($"Un cambio tuyo no se pudo aplicar: {text} Lo deshicimos para que sigas igual que los demás.{also}", true);
    }

    /// <summary>Vuelve a aplicar un paso propio sin respuesta sobre lo vivo: <c>false</c> si ya no cabe. Solo desde <c>ui</c>.</summary>
    private bool Reproject(Pending next)
    {
        Patch? patch = PatchDiff.Create(next.Before, next.After, _liveVersions, new PatchHeader(next.Patch.Id, next.Patch.BaseOrder, next.Patch.Author, next.Patch.Description));
        if (patch is null)
        {
            return false;
        }

        SharedState before = SharedState.Capture(_live);
        if (!PatchApply.TryApply(_live, _liveVersions, patch).IsAccepted)
        {
            return false;
        }

        next.Patch = patch;
        next.Before = before;
        next.After = SharedState.Capture(_live);
        next.Keys = PatchReservations.Required(patch, _live);
        return true;
    }

    /// <summary>Lo vivo pasa a ser lo confirmado (descartando lo propio sin respuesta). Solo desde <c>ui</c>.</summary>
    private void ConvergeToConfirmed()
    {
        lock (_m)
        {
            _pending.Clear();
        }

        if (ResetToConfirmed())
        {
            _liveChanged();
        }
    }

    /// <summary>Lo vivo vuelve a lo confirmado y conserva la lista de pasos sin respuesta. <c>false</c> si no hay vuelta segura.</summary>
    private bool ResetToConfirmed()
    {
        SharedState target;
        ElementVersions versions;
        lock (_m)
        {
            target = SharedState.Capture(_mirror);
            versions = _mirrorVersions.Clone();
        }

        SharedState current = SharedState.Capture(_live);
        Patch? back = PatchDiff.Create(current, target, _liveVersions, Header("Volver a lo de la sala"));
        if (back is not null)
        {
            PatchResult result = PatchApply.TryApply(_live, _liveVersions, back);
            if (!result.IsAccepted)
            {
                Desynced?.Invoke(result.Rejection!.Message);
                return false;
            }
        }

        _liveVersions = versions;
        _baseline = target;
        return true;
    }
}
