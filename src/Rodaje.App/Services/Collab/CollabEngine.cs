using System.Text.Json;
using System.Text.Json.Nodes;
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

        /// <summary>El parche tal como se mandó: con él se comprueba que la confirmación trae exactamente lo enviado.</summary>
        public string Canonical { get; set; } = PatchWire.ToJson(patch);

        public SharedState Before { get; set; } = before;

        public SharedState After { get; set; } = after;

        // La sala ya lo ordenó (está en lo confirmado); solo falta que la interfaz lo dé por bueno.
        public bool Confirmed { get; set; }

        public bool Attempted { get; set; }

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
    private long _generation;   // sube cuando lo confirmado pisa los pendientes: los envíos a la espera se invalidan
    private readonly Queue<string> _seenPatchIds = new();
    private readonly HashSet<string> _seenSet = new(StringComparer.Ordinal);
    private const int MaxSeenPatchIds = 4096;
    private int _sendAttempts;   // cuántas veces se llamó a Sala para mandar (lo miran las pruebas)

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
        EnsureReservations = presence.EnsureAsync;
        _ui = ui;
        _liveChanged = liveChanged;
        _notice = notice;
        _room.ConnectionLost += OnConnectionLost;
        _room.Superseded += HandleSupersededAsync;
        _room.SnapshotNeeded += OnSnapshotNeeded;
        _room.StateChanged += OnRoomChanged;
        _room.DirectReceived += OnRecoveryDirect;
        UiTask.Run(Task.Run(SendLoopAsync));
    }

    /// <summary>Lo confirmado y lo vivo dejaron de coincidir y no hay arreglo seguro: la persona tiene que volver a entrar.</summary>
    public event Action<string>? Desynced;

    /// <summary>Cambios ajenos retenidos mientras hay un gesto abierto (solo para las pruebas).</summary>
    internal int DeferredCount => _deferred.Count;

    /// <summary>Cómo se piden las reservas antes de mandar; las pruebas la cambian para retener un envío a la espera.</summary>
    internal Func<IEnumerable<string>, CancellationToken, Task<ReservationBlock?>> EnsureReservations { get; set; }

    /// <summary>Envíos intentados hacia la sala (solo para las pruebas).</summary>
    internal int SendAttempts => _sendAttempts;

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    public string? UndoDescription => _history.UndoDescription;

    public string? RedoDescription => _history.RedoDescription;

    public void Dispose()
    {
        _room.ConnectionLost -= OnConnectionLost;
        _room.Superseded -= HandleSupersededAsync;
        _room.SnapshotNeeded -= OnSnapshotNeeded;
        _room.StateChanged -= OnRoomChanged;
        _room.DirectReceived -= OnRecoveryDirect;
        lock (_m) { if (_started) Persist(); }
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
            if (_room.RoomFolder.Length > 0)
            {
                _journal = new CollabJournal(_room.RoomFolder);
                _restore = _journal.Load(() => _notice("El registro de la sala está dañado o no se pudo leer y se ignoró. La edición puede continuar; conserva tu copia del proyecto si falta algún cambio.", true));
            }
            _mirror = mirror;
            _mirrorVersions = versions.Clone();
            if (_room.State.IsHost && _restore is { } saved)
            {
                ProjectParseResult restored = ProjectWire.TryParse(saved.Snapshot);
                if (restored.Project is { } confirmed && confirmed.Id == mirror.Id && restored.Versions is { } confirmedVersions)
                {
                    _mirror = confirmed;
                    _mirrorVersions = confirmedVersions;
                }
            }
            _seq = seq;
            _liveVersions = _mirrorVersions.Clone();
            _started = true;
            early = [.. _early];
            _early.Clear();
        }

        _ = _ui(() =>
        {
            if (_room.State.IsHost && _restore is not null) ResetToConfirmed();
            _baseline = SharedState.Capture(_live);
            RestoreJournal();
        });
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
            if (!Persist())
            {
                _notice("No se pudo guardar el registro de la sala. Este cambio sigue en este equipo y no se enviará hasta poder guardarlo. Comprueba el espacio y los permisos de la carpeta del proyecto.", true);
                return;
            }
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
        long generation;
        lock (_m)
        {
            dropped = !_pending.Contains(pending) || pending.Confirmed;
            generation = _generation;
            if (!dropped)
            {
                if (pending.Attempted || _room.State.Phase == RoomPhase.Reconnecting || _superseded || !Persist()) return;
                pending.Attempted = true;
            }
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
            ReservationBlock? blocked = await EnsureReservations(pending.Keys, _stop.Token).ConfigureAwait(false);
            if (blocked is not null)
            {
                pending.Sent.TrySetResult(Guid.Empty);
                await _ui(() => Revert(pending, blocked.Describe(), "Otra persona tiene la pista reservada. Puedes conservar este cambio como uno nuevo en otra pista o descartarlo.")).ConfigureAwait(false);
                return;
            }

            lock (_m)
            {
                // Mientras esperaba la reserva, lo confirmado pudo pisar este cambio (rechazo de otro pendiente):
                // entonces ya no se manda, sea lo que sea que quede en la lista de pendientes.
                dropped = generation != _generation || !_pending.Contains(pending);
            }

            if (dropped)
            {
                pending.Sent.TrySetResult(Guid.Empty);
                _presence.Done(pending.Keys);
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(pending.Canonical);
            Interlocked.Increment(ref _sendAttempts);
            Guid id = await _room.SendAsync(doc.RootElement, pending.Keys, _stop.Token).ConfigureAwait(false);
            pending.Sent.TrySetResult(id);
        }
        catch (Exception ex) when (ex is CollabException or JsonException or OperationCanceledException or ArgumentException)
        {
            lock (_m) pending.Attempted = false;
            if (_room.State.Phase == RoomPhase.Reconnecting || ex is OperationCanceledException) return;
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
        PatchParseResult parsed = PatchWire.TryParse(payload.GetRawText(), cancellationToken: cancellationToken);
        if (parsed.Patch is not { } patch)
        {
            return ValueTask.FromResult(Veredicto.No("no-valido", parsed.Rejection?.Message ?? "El cambio no es válido."));
        }
        if (!Guid.TryParse(patch.Id, out _) || patch.Author != author.Clave)
            return ValueTask.FromResult(Veredicto.No("no-valido", "El identificador o el autor del cambio no son válidos."));

        Project candidate;
        ElementVersions versions;
        IReadOnlySet<string> required;
        lock (_m)
        {
            if (!_started)
            {
                return ValueTask.FromResult(Veredicto.No("no-valido", "La sala todavía no está lista."));
            }
            if (_superseded) return ValueTask.FromResult(Veredicto.No("superada", "Otra copia de la sala quedó al mando. Vuelve a entrar antes de seguir editando."));

            // Un reenvío tras reabrir tiene otro id de transporte. No crea un segundo orden.
            if (_accepted.Contains(patch.Id)) return ValueTask.FromResult(Veredicto.No("ya-confirmado", "Este cambio ya está en la copia confirmada de la sala."));
            if (_branch.Count >= CollabJournal.MaxEntries)
                return ValueTask.FromResult(Veredicto.No("registro-lleno", "El registro de cambios separados está lleno. Vuelve a entrar en la sala actual antes de seguir editando."));

            if (_seenSet.Contains(patch.Id))
            {
                return ValueTask.FromResult(Veredicto.No("duplicado", "Ese cambio ya entró en la sala."));
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
            lock (_m)
            {
                // Un identificador aceptado no vuelve a entrar; la ventana es acotada y la sala es corta.
                _seenSet.Add(patch.Id);
                _seenPatchIds.Enqueue(patch.Id);
                while (_seenPatchIds.Count > MaxSeenPatchIds)
                {
                    _seenSet.Remove(_seenPatchIds.Dequeue());
                }
            }

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
        bool duplicate = false;
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
                    Pending? candidate = _pending.FirstOrDefault(p => p.Patch.Id == patch.Id);
                    if (candidate is not null
                        && (ordered.Autor != _room.State.MyKey
                            || !SamePayload(candidate.Canonical, ordered.Datos)))
                    {
                        // El mismo Patch.Id pero otro autor, otro id de carga u otro contenido no es la confirmación de tu cambio.
                        desync = "La sala confirmó un cambio tuyo con contenido distinto del que mandaste. Tu copia ya no se puede fiar.";
                    }
                    else
                    {
                        duplicate = _accepted.Contains(patch.Id);
                        PatchResult result = duplicate ? PatchResult.Accepted : PatchApply.TryApply(_mirror, _mirrorVersions, patch);
                        if (!result.IsAccepted)
                        {
                            desync = result.Rejection!.Message;
                        }
                        else
                        {
                            _seq = ordered.Seq;
                            if (!duplicate)
                            {
                                RememberAccepted(patch.Id);
                                if (_separation is not null)
                                    _branch.Add(new JournalEntry("branch", patch with { Author = ordered.Autor }, ordered.Seq));
                                if (_superseded && ordered.Autor == _room.State.MyKey)
                                    Unsettled.Add(patch, "El cambio se confirmó mientras otra copia de la sala tomaba el mando. Vuelve a entrar para conservarlo como uno nuevo o descartarlo.");
                            }
                            if (candidate is not null)
                            {
                                candidate.Confirmed = true;
                            }
                            Persist();
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
        if (duplicate)
        {
            _ = _ui(() => ConfirmDuplicate(ordered1));
            return;
        }
        _ = _ui(() => ApplyToLive(ordered1));
    }

    /// <summary>La carga confirmada es exactamente la que se mandó, comparando el JSON por contenido (el transporte puede cambiar su formato).</summary>
    private static bool SamePayload(string canonical, JsonElement received)
    {
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(canonical), JsonNode.Parse(received.GetRawText()));
        }
        catch (JsonException)
        {
            return false;
        }
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
                if (rejection.Motivo == "ya-confirmado")
                {
                    await _ui(() =>
                    {
                        ConfirmDuplicate(match.Patch);
                        if (Remount() is not { } lost) return;
                        lock (_m) Persist();
                        _liveChanged();
                        if (lost.Count > 0)
                            _notice($"La copia actual cambió mientras se confirmaba otro paso y no entró: {string.Join(", ", lost.Select(p => p.Description))}.", true);
                    }).ConfigureAwait(false);
                    return;
                }
                await _ui(() => Revert(match, CollabMessages.Rejected(rejection.Motivo, null),
                    CollabMessages.Rejected(rejection.Motivo, null) + " Puedes conservar este cambio como uno nuevo o descartarlo.")).ConfigureAwait(false);
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
                Persist();
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

            ReconcileLiveOrders();
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
        ReconcileLiveOrders();
        _liveChanged();
    }

    /// <summary>
    /// Las altas y bajas simultáneas no piden reservas: lo vivo puede quedar con la misma lista de elementos en otro orden
    /// del confirmado (el propio por delante y lo ajeno aplicado detrás). Tras cada aplicación ordenada se reconstruye lo
    /// esperado (confirmado + pendientes) y, si solo difiere el orden de las listas, se realinea con el canónico. Si el
    /// desajuste no se puede reparar, la copia no es segura.
    /// </summary>
    private void ReconcileLiveOrders()
    {
        SharedState wanted;
        lock (_m)
        {
            Project target = _mirror.Clone();
            ElementVersions versions = _mirrorVersions.Clone();
            foreach (Pending p in _pending)
            {
                _ = PatchApply.TryApply(target, versions, p.Patch);
            }

            wanted = SharedState.Capture(target);
        }

        SharedState current = SharedState.Capture(_live);
        if (wanted.Hash == current.Hash)
        {
            return;
        }

        Patch? fix = PatchDiff.Create(current, wanted, _liveVersions, Header("Reordenar como la sala"));
        if (fix is null || fix.Ops.Count > 0 || fix.Orders.Count == 0)
        {
            // Nada que hacer o un desajuste de contenido: este último lo resuelve el veredicto de su pendiente.
            return;
        }

        // Solo se realinea el orden: no es un paso propio, así que se aplica sin avanzar las versiones de lo vivo.
        if (PatchApply.TryApply(_live, _liveVersions.Clone(), fix).IsAccepted)
        {
            _baseline = SharedState.Capture(_live);
        }
        else
        {
            Desynced?.Invoke("No se pudo reordenar tu copia como la sala.");
        }
    }

    private void Revert(Pending pending, string text, string? unsettledReason = null)
    {
        lock (_m)
        {
            if (!_pending.Remove(pending))
            {
                return;
            }
        }

        _presence.Done(pending.Keys);
        SaveUnsettled(pending.Patch, unsettledReason ?? "El envío del cambio no se pudo completar. Puedes conservarlo como un cambio nuevo o descartarlo.");
        if (Remount() is not { } lost)
        {
            return;
        }

        lock (_m) Persist();
        _liveChanged();
        string also = lost.Count == 0 ? "" : $" Tampoco entró lo que hiciste después y dependía de eso ({string.Join(", ", lost.Select(p => p.Description))}).";
        _notice($"Un cambio tuyo no se pudo aplicar: {text} Lo deshicimos para que sigas igual que los demás.{also}", true);
    }

    /// <summary>
    /// Lo vivo vuelve a lo confirmado y se montan encima, en orden, todos los pasos propios sin respuesta (también los que
    /// salieron antes del que falló: su confirmación da por hecho que ya están en lo vivo). Los que ya no caben se quitan y
    /// quedan en «Cambios que no entraron». <c>null</c> si no hay vuelta segura. Solo desde <c>ui</c>.
    /// </summary>
    private List<Pending>? Remount()
    {
        if (!ResetToConfirmed())
        {
            return null;
        }

        List<Pending> lost = [];
        foreach (Pending next in SnapshotPending().Where(p => !p.Confirmed))
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
                SaveUnsettled(p.Patch, "Este cambio dependía de otro que no entró. Puedes conservarlo como un cambio nuevo o descartarlo.");
            }
        }

        _baseline = SharedState.Capture(_live);
        return lost;
    }

    /// <summary>Vuelve a aplicar un paso propio sin respuesta sobre lo vivo: <c>false</c> si ya no cabe. Solo desde <c>ui</c>.</summary>
    private bool Reproject(Pending next)
    {
        Patch patch = next.Patch;

        SharedState before = SharedState.Capture(_live);
        if (!PatchApply.TryApply(_live, _liveVersions, patch).IsAccepted)
        {
            return false;
        }

        next.Patch = patch;
        if (!next.Sent.Task.IsCompleted)
        {
            // Lo que aún no salió sale ya con las versiones nuevas; lo que sí salió se comprueba contra lo que se mandó.
            next.Canonical = PatchWire.ToJson(patch);
        }

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
            foreach (Pending pending in _pending.Where(p => !p.Confirmed))
                SaveUnsettled(pending.Patch, "Otra persona cambió lo mismo. Puedes conservar tu cambio como uno nuevo o descartarlo.");
            _pending.Clear();
            _generation++;   // los envíos que esperaban una reserva ya no salen (revisión B12, hallazgo 6)
            Persist();
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
