using Dathinel.Sala;
using System.Text.Json;
using Rodaje.App.Services.Crashes;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Collab;

public sealed partial class CollabEngine
{
    private CollabJournal? _journal;
    private JournalState? _restore;
    private readonly HashSet<string> _accepted = new(StringComparer.Ordinal);
    private readonly Queue<string> _acceptedOrder = new();
    private readonly List<JournalEntry> _branch = [];
    private long? _separation;
    private bool _superseded;
    private int _recovering;
    private bool _persistenceFailed;
    private bool _journalBlocked;

    public UnsettledChanges Unsettled { get; } = new();
    public event Action? UnsettledChanged;

    // Bajo _m. Los pendientes nunca se recortan para cumplir el tope: se deja de enviar si no caben.
    private bool Persist()
    {
        if (_journalBlocked) return false;
        if (_journal is null) return true;
        List<JournalEntry> entries = [.. _pending.Where(p => !p.Confirmed).Select(p => new JournalEntry("pending", p.Patch)),
            .. Unsettled.Items.Select(i => new JournalEntry("unsettled", i.Patch, Reason: i.Reason)),
            .. _branch.Where(e => e.Patch.Author == _room.State.MyKey)];
        bool saved = _journal.Save(new JournalState(_seq, ProjectWire.ToJson(_mirror, _mirrorVersions), _separation, _acceptedOrder.ToArray(), entries));
        if (!saved && !_persistenceFailed)
            _ = _ui(() => _notice("No se pudo guardar el registro de la sala. Conserva el proyecto abierto y comprueba el espacio y los permisos de su carpeta antes de cerrar.", true));
        _persistenceFailed = !saved;
        return saved;
    }

    private void RememberAccepted(string id)
    {
        if (!_accepted.Add(id)) return;
        _acceptedOrder.Enqueue(id);
        if (_acceptedOrder.Count > 8192) _accepted.Remove(_acceptedOrder.Dequeue());
    }

    private void RestoreJournal()
    {
        lock (_m)
        {
            JournalState? saved = _restore;
            _restore = null;
            if (saved is not null)
            {
                ProjectParseResult snapshot = ProjectWire.TryParse(saved.Snapshot);
                if (snapshot.Project?.Id != _mirror.Id)
                {
                    _notice("El registro pertenece a otro proyecto. Conserva una copia del proyecto anterior y usa otra carpeta para esta sala antes de enviar cambios.", true);
                    _journalBlocked = true;
                    return;
                }
                if ((_room.State.IsHost || saved.Seq == _seq) && SharedState.Capture(snapshot.Project!).Hash == SharedState.Capture(_mirror).Hash)
                    foreach (string id in saved.Accepted) RememberAccepted(id);
                _separation = saved.Separation is { } separation ? Math.Min(separation, _seq) : null;
                foreach (JournalEntry entry in saved.Entries)
                {
                    Patch patch = entry.Patch with { Author = _room.State.MyKey };
                    if (entry.Kind == "unsettled") Unsettled.Add(patch, entry.Reason);
                    else if (entry.Kind == "branch")
                    {
                        if (!IsReflected(patch))
                            Unsettled.Add(patch, "Este cambio quedó confirmado en otra copia de la sala y no está en la copia actual. Puedes conservarlo como uno nuevo o descartarlo.");
                        else _branch.Add(entry with { Patch = patch, Seq = Math.Min(entry.Seq, _seq) });
                    }
                    else if (!_accepted.Contains(patch.Id))
                    {
                        if (IsReflected(patch)) { RememberAccepted(patch.Id); continue; }
                        SharedState before = SharedState.Capture(_live);
                        PatchResult result = PatchApply.TryApply(_live, _liveVersions, patch);
                        if (!result.IsAccepted)
                        {
                            Unsettled.Add(patch, "La sala cambió mientras estabas fuera. Puedes conservar este cambio como uno nuevo o descartarlo.");
                            continue;
                        }
                        Pending pending = new(patch, before, SharedState.Capture(_live), patch.Description, false, false)
                        { Keys = PatchReservations.Required(patch, _live) };
                        _pending.Add(pending);
                        _outbox.Writer.TryWrite(pending);
                    }
                }
                _baseline = SharedState.Capture(_live);
                _liveChanged();
                UnsettledChanged?.Invoke();
            }
            Persist();
        }
    }

    private bool IsReflected(Patch patch)
    {
        SharedState confirmed = SharedState.Capture(_mirror);
        if (patch.Ops.Count == 0) return false;
        foreach (PatchOp op in patch.Ops)
        {
            if (_mirrorVersions.Get(op.Key) <= op.BaseVersion) return false;
            bool exists = confirmed.Elements.TryGetValue(op.Key, out ElementState? element);
            if (op.IsDelete)
            {
                if (exists) return false;
            }
            else if (element is null || element.Parent != op.Parent || element.Content != op.Content) return false;
        }
        return patch.Orders.All(o => confirmed.Orders.TryGetValue(o.Container, out IReadOnlyList<string>? ids) && ids.SequenceEqual(o.Ids));
    }

    private void OnRoomChanged(RoomState state)
    {
        if (state.Phase is not (RoomPhase.Hosting or RoomPhase.Connected)) return;
        lock (_m)
        {
            // Sala conserva y reenvía las cargas que ya tienen id de transporte. Solo salen aquí las aún no enviadas.
            foreach (Pending pending in _pending.Where(p => !p.Confirmed && !p.Attempted))
                _outbox.Writer.TryWrite(pending);
        }
    }

    internal void OnConnectionLost()
    {
        lock (_m)
        {
            // TODO B15a: acordar un punto común con acuse de todos los miembros al reconectar.
            // Hasta entonces se conserva el primer corte: ante Superada se ofrece recuperar de más antes que perder cambios.
            _separation ??= _seq;
            Persist();
        }
    }

    internal async Task HandleSupersededAsync()
    {
        List<JournalEntry> branch;
        lock (_m)
        {
            _superseded = true;
            branch = [.. _branch.Where(e => e.Seq > (_separation ?? _seq))];
            foreach (JournalEntry entry in branch.Where(e => e.Patch.Author == _room.State.MyKey))
                Unsettled.Add(entry.Patch, "Otra copia de la sala quedó al mando durante el corte. Este cambio quedó en la copia anterior. Vuelve a entrar y elige conservarlo como uno nuevo o descartarlo.");
            foreach (Pending pending in _pending.Where(p => !p.Confirmed))
                Unsettled.Add(pending.Patch, "El cambio seguía sin respuesta cuando otra copia de la sala quedó al mando. Vuelve a entrar para conservarlo como uno nuevo o descartarlo.");
            _pending.Clear();
            _branch.Clear();
            if (!Persist()) _notice("No se pudo guardar «Cambios que no entraron». Conserva el proyecto abierto y comprueba el espacio y los permisos antes de cerrar.", true);
        }
        _ = _ui(() => UnsettledChanged?.Invoke());
        if (!_room.State.IsHost) return;
        foreach (JournalEntry entry in branch.Where(e => e.Patch.Author != _room.State.MyKey))
        {
            try
            {
                using MemoryStream stream = new();
                using (Utf8JsonWriter writer = new(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("tipo", "cambio-separado");
                    writer.WritePropertyName("patch");
                    writer.WriteRawValue(PatchWire.ToJson(entry.Patch));
                    writer.WriteEndObject();
                }
                using JsonDocument message = JsonDocument.Parse(stream.ToArray());
                await _room.SendDirectAsync(message.RootElement, entry.Patch.Author, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CollabException or OperationCanceledException)
            {
                // TODO B15a: entrega con acuse al autor que ya no está conectado a la rama perdedora.
                // Ese autor conserva su registro local; no se fusiona ni se borra su copia.
            }
        }
    }

    private void OnRecoveryDirect(string from, JsonElement message)
    {
        if (from != _room.HostKey || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("tipo", out JsonElement type) || type.ValueKind != JsonValueKind.String || type.GetString() != "cambio-separado"
            || !message.TryGetProperty("patch", out JsonElement data)) return;
        Patch? patch = PatchWire.TryParse(data.GetRawText()).Patch;
        if (patch is null || patch.Author != _room.State.MyKey) return;
        lock (_m)
        {
            Unsettled.Add(patch, "Otra copia de la sala quedó al mando. Este cambio quedó en la copia anterior. Vuelve a entrar para conservarlo como uno nuevo o descartarlo.");
            Persist();
        }
        _ = _ui(() => UnsettledChanged?.Invoke());
    }

    private void SaveUnsettled(Patch patch, string reason)
    {
        lock (_m)
        {
            if (!Unsettled.Add(patch, reason)) return;
            Persist();
        }
        UnsettledChanged?.Invoke();
    }

    /// <summary>Solo desde ui. Descarta el paso guardado, sin cambiar el proyecto de la sala.</summary>
    public void DiscardUnsettled(string id)
    {
        lock (_m)
        {
            UnsettledChange? item = Unsettled.Items.FirstOrDefault(i => i.Id == id);
            if (item is null || !Unsettled.Remove(id)) return;
            if (!Persist()) { Unsettled.Add(item.Patch, item.Reason); return; }
        }
        UnsettledChanged?.Invoke();
    }

    /// <summary>Solo desde ui. Propone una copia nueva; el original se retira únicamente al quedar registrada la propuesta.</summary>
    public bool KeepUnsettled(string id)
    {
        if (_room.State.MyRole != Rol.Editar || !_room.State.InRoom || _superseded) return false;
        UnsettledChange? item = Unsettled.Items.FirstOrDefault(i => i.Id == id);
        if (item is null) return false;
        Patch? patch = UnsettledRecovery.Create(item.Patch, _live, _liveVersions, Header(item.Description), _room.Reservations);
        if (patch is null)
        {
            _notice("Este cambio no se pudo montar como una copia nueva. Sigue guardado en «Cambios que no entraron»; puedes rehacerlo manualmente o descartarlo.", true);
            return false;
        }
        SharedState before = SharedState.Capture(_live);
        if (!PatchApply.TryApply(_live, _liveVersions, patch).IsAccepted) return false;
        Pending pending = new(patch, before, SharedState.Capture(_live), item.Description, false, false);
        Enqueue(pending, pending.After, alreadyApplied: true);
        lock (_m)
        {
            if (!Persist()) return false;
            Unsettled.Remove(id);
            if (!Persist()) Unsettled.Add(item.Patch, item.Reason);
        }
        _liveChanged();
        UnsettledChanged?.Invoke();
        return true;
    }

    private void ConfirmDuplicate(Patch patch)
    {
        lock (_m)
        {
            Pending? pending = _pending.FirstOrDefault(p => p.Patch.Id == patch.Id);
            if (pending is null) return;
            _pending.Remove(pending);
            _presence.Done(pending.Keys);
            Persist();
        }
        // Una carga repetida se confirma sin volver a aplicar el efecto sobre lo vivo.
    }

    private void OnSnapshotNeeded(long epoch)
    {
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0) return;
        UiTask.Run(RecoverSnapshotAsync(epoch));
    }

    private async Task RecoverSnapshotAsync(long epoch)
    {
        try
        {
            ReceivedProject received = await ProjectShareGuest.ReceiveAsync(_room, _room.RoomFolder, cancellationToken: _stop.Token).ConfigureAwait(false);
            if (epoch != _room.Epoch) throw new CollabException("La sala volvió a cambiar de anfitrión. Vuelve a entrar para recibir la copia actual.");
            bool ready = false;
            await _ui(() =>
            {
                lock (_m)
                {
                    _mirror = received.Project;
                    _mirrorVersions = received.Versions.Clone();
                    _seq = received.Seq;
                    _deferred.Clear();
                    if (!ResetToConfirmed()) return;
                    foreach (Pending pending in _pending.ToArray())
                    {
                        if (IsReflected(pending.Patch)) { _pending.Remove(pending); _presence.Done(pending.Keys); continue; }
                        // Conserva el parche exacto y su id: no se rebasa silenciosamente una edición antigua.
                        if (!PatchApply.TryApply(_live, _liveVersions, pending.Patch).IsAccepted)
                        {
                            Unsettled.Add(pending.Patch, "La copia actual cambió mientras estabas fuera. Puedes conservar tu cambio como uno nuevo o descartarlo.");
                            _pending.Remove(pending);
                        }
                    }
                    _baseline = SharedState.Capture(_live);
                    ready = Persist();
                }
                _liveChanged();
                UnsettledChanged?.Invoke();
            }).ConfigureAwait(false);
            if (!ready) throw new CollabException("No se pudo guardar la copia actual de la sala. Comprueba la carpeta del proyecto y vuelve a entrar.");
            await _room.ConfirmSnapshotAsync(epoch, received.Seq, _stop.Token).ConfigureAwait(false);
            OnRoomChanged(_room.State);
        }
        catch (Exception ex) when (ex is CollabException or OperationCanceledException or IOException)
        {
            if (ex is not OperationCanceledException)
                await _ui(() => _notice("No se pudo recibir la copia actual de la sala. Tus cambios siguen guardados aquí. Vuelve a entrar para reintentarlo.", true)).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref _recovering, 0); }
    }
}
