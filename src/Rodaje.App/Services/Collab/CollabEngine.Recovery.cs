using Dathinel.Sala;
using System.Text;
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
    private readonly List<JournalEntry> _separated = [];
    private readonly HashSet<string> _sendingSeparated = new(StringComparer.Ordinal);
    private readonly List<string> _recoveryHosts = [];
    internal const int MaxSeparatedPerAuthor = 256;
    internal const int MaxSeparatedBytes = 64 * 1024 * 1024;
    private long _separatedBytes;
    private long? _separation;
    private bool _superseded;
    private int _recovering;
    private bool _persistenceFailed;
    private bool _journalBlocked;
    private bool _recoveryReady;

    public UnsettledChanges Unsettled { get; } = new();
    public event Action? UnsettledChanged;

    // Bajo _m. Los pendientes nunca se recortan para cumplir el tope: se deja de enviar si no caben.
    private bool Persist()
    {
        if (_journalBlocked) return false;
        if (_journal is null) return true;
        List<JournalEntry> entries = [.. _pending.Where(p => !p.Confirmed).Select(p => new JournalEntry("pending", p.Patch)),
            .. Unsettled.Items.Select(i => new JournalEntry("unsettled", i.Patch, Reason: i.Reason)),
            .. _branch.Where(e => e.Patch.Author == _room.State.MyKey), .. _separated];
        bool saved = _journal.Save(new JournalState(_seq, ProjectWire.ToJson(_mirror, _mirrorVersions), _separation, _acceptedOrder.ToArray(), entries, _recoveryHosts));
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
                foreach (string host in saved.RecoveryHosts ?? []) RememberRecoveryHost(host);
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
                    if (entry.Kind == "separated")
                    {
                        AddSeparated(entry.Patch);
                        continue;
                    }
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
            _recoveryReady = true;
        }
        OnRoomChanged(_room.State);
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
            string? host = state.Members.FirstOrDefault(m => m.IsHost)?.Key;
            if (host is not null && RememberRecoveryHost(host) && _recoveryReady) Persist();
            // Sala conserva y reenvía las cargas que ya tienen id de transporte. Solo salen aquí las aún no enviadas.
            foreach (Pending pending in _pending.Where(p => !p.Confirmed && !p.Attempted))
                _outbox.Writer.TryWrite(pending);
        }
        UiTask.Run(SendSeparatedAsync());
        if (!state.IsHost && _recoveryReady) UiTask.Run(SendRecoveryControlAsync("cambios-separados-listo", null, _room.HostKey));
    }

    private bool RememberRecoveryHost(string key)
    {
        if (_recoveryHosts.Contains(key)) return false;
        _recoveryHosts.Add(key);
        if (_recoveryHosts.Count > 16) _recoveryHosts.RemoveAt(0);
        return true;
    }

    internal void OnConnectionLost()
    {
        lock (_m)
        {
            // Se conserva el primer corte: ante Superada se ofrece recuperar de más antes que perder cambios.
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
            foreach (JournalEntry entry in branch.Where(e => e.Patch.Author != _room.State.MyKey))
                AddSeparated(entry.Patch);
            _pending.Clear();
            _branch.Clear();
            if (!Persist()) _notice("No se pudo guardar «Cambios que no entraron». Conserva el proyecto abierto y comprueba el espacio y los permisos antes de cerrar.", true);
        }
        _ = _ui(() => UnsettledChanged?.Invoke());
        await SendSeparatedAsync().ConfigureAwait(false);
    }

    // Bajo _m. La cola conserva su orden de llegada, también al restaurar el registro.
    private void AddSeparated(Patch patch)
    {
        if (_separated.Any(e => e.Patch.Id == patch.Id)) return;
        _separated.Add(new JournalEntry("separated", patch));
        _separatedBytes += SeparatedBytes(patch);
        bool trimmed = false;
        while (_separated.Count(e => e.Patch.Author == patch.Author) > MaxSeparatedPerAuthor)
        {
            RemoveSeparated(_separated.First(e => e.Patch.Author == patch.Author));
            trimmed = true;
        }
        while (_separatedBytes > MaxSeparatedBytes || _separated.Count > CollabJournal.MaxEntries / 2)
        {
            RemoveSeparated(_separated[0]);
            trimmed = true;
        }
        if (trimmed) _ = _ui(() => _notice("Se alcanzó el límite de cambios separados sin entregar. Se descartaron los más antiguos. Conserva una copia del proyecto anterior para recuperarlos.", true));
    }

    private static int SeparatedBytes(Patch patch) => Encoding.UTF8.GetByteCount(PatchWire.ToJson(patch)) + 128;

    private void RemoveSeparated(JournalEntry entry)
    {
        _separated.Remove(entry);
        _separatedBytes -= SeparatedBytes(entry.Patch);
    }

    private async Task SendSeparatedAsync()
    {
        List<JournalEntry> deliveries;
        lock (_m)
        {
            RoomState state = _room.State;
            if (!_recoveryReady || state.Phase is not (RoomPhase.Hosting or RoomPhase.Connected) || _separated.Count == 0 || !Persist()) return;
            deliveries = [.. _separated.Where(e => (!state.IsHost || state.Members.Any(m => m.Key == e.Patch.Author && m.Online))
                && _sendingSeparated.Add(e.Patch.Id))];
        }
        foreach (JournalEntry entry in deliveries)
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
                using JsonDocument message = JsonDocument.Parse(stream.ToArray(), new JsonDocumentOptions { MaxDepth = WireLimits.Default.MaxDepth + 4 });
                string target = _room.State.IsHost ? entry.Patch.Author : _room.HostKey;
                await _room.SendDirectAsync(message.RootElement, target, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CollabException or OperationCanceledException)
            {
                // Se conserva hasta el acuse; el próximo cambio de conexión vuelve a intentarlo.
            }
            finally { lock (_m) _sendingSeparated.Remove(entry.Patch.Id); }
        }
    }

    internal void OnRecoveryDirect(string from, JsonElement message)
    {
        if (!_recoveryReady || message.ValueKind != JsonValueKind.Object) return;
        string json = message.GetRawText();
        if (json.Length > WireLimits.Default.MaxPatchBytes + 1024 || Encoding.UTF8.GetByteCount(json) > WireLimits.Default.MaxPatchBytes + 1024) return;
        HashSet<string> fields = new(StringComparer.Ordinal);
        if (message.EnumerateObject().Any(p => !fields.Add(p.Name))
            || !message.TryGetProperty("tipo", out JsonElement type) || type.ValueKind != JsonValueKind.String) return;
        string? kind = type.GetString();
        RoomState state = _room.State;
        if (kind == "cambios-separados-listo")
        {
            if (fields.Count == 1 && state.IsHost && state.Members.Any(m => m.Key == from && m.Online)) UiTask.Run(SendSeparatedAsync());
            return;
        }
        bool custody = kind == "cambio-separado-guardado";
        if (kind == "cambio-separado-recibido" || custody)
        {
            if ((custody ? state.IsHost || from != _room.HostKey : !state.IsHost)
                || fields.Count != 2 || !message.TryGetProperty("id", out JsonElement id)
                || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out _)) return;
            lock (_m)
            {
                JournalEntry? entry = _separated.FirstOrDefault(e => e.Patch.Id == id.GetString() && (custody || e.Patch.Author == from));
                if (entry is null) return;
                int index = _separated.IndexOf(entry);
                RemoveSeparated(entry);
                if (!Persist()) { _separated.Insert(index, entry); _separatedBytes += SeparatedBytes(entry.Patch); }
            }
            return;
        }
        if (kind != "cambio-separado" || fields.Count != 2
            || !message.TryGetProperty("patch", out JsonElement data) || data.ValueKind != JsonValueKind.Object) return;
        bool relay;
        lock (_m) relay = state.IsHost && _recoveryHosts.Contains(from)
            && state.Members.Any(m => m.Key == from && m.Online && m.Role == Rol.Editar);
        if (!relay && from != _room.HostKey) return;
        Patch? patch = PatchWire.TryParse(data.GetRawText()).Patch;
        if (patch is null || !Guid.TryParse(patch.Id, out _) || !CollabJournal.IsMemberKey(patch.Author)) return;
        if (relay && patch.Author != state.MyKey)
        {
            lock (_m)
            {
                JournalEntry? existing = _separated.FirstOrDefault(e => e.Patch.Id == patch.Id);
                if (existing is not null && PatchWire.ToJson(existing.Patch) != PatchWire.ToJson(patch)) return;
                AddSeparated(patch);
                if (!Persist()) return;
            }
            UiTask.Run(SendRecoveryControlAsync("cambio-separado-guardado", patch.Id, from));
            UiTask.Run(SendSeparatedAsync());
            return;
        }
        if (patch.Author != state.MyKey) return;
        lock (_m)
        {
            Unsettled.Add(patch, "Otra copia de la sala quedó al mando. Este cambio quedó en la copia anterior. Vuelve a entrar para conservarlo como uno nuevo o descartarlo.");
            if (!Persist()) return;
        }
        _ = _ui(() => UnsettledChanged?.Invoke());
        UiTask.Run(SendRecoveryControlAsync(relay ? "cambio-separado-guardado" : "cambio-separado-recibido", patch.Id, from));
    }

    private async Task SendRecoveryControlAsync(string type, string? id, string target)
    {
        try
        {
            JsonElement message = ShareMessages.Make(type, id is null ? null : w => w.WriteString("id", id));
            await _room.SendDirectAsync(message, target, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabException or OperationCanceledException)
        {
            // Un reenvío se deduplica en Unsettled y vuelve a acusarse incluso si ya estaba guardado.
        }
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
        if (patch.IsEmpty)
        {
            lock (_m)
            {
                Unsettled.Remove(id);
                if (!Persist()) { Unsettled.Add(item.Patch, item.Reason); return false; }
            }
            UnsettledChanged?.Invoke();
            return true;
        }
        SharedState before = SharedState.Capture(_live);
        if (!PatchApply.TryApply(_live, _liveVersions, patch).IsAccepted) return false;
        Pending pending = new(patch, before, SharedState.Capture(_live), item.Description, false, false);
        Enqueue(pending, pending.After, alreadyApplied: true);
        lock (_m)
        {
            if (!Persist()) return false;
            Unsettled.Remove(id);
            if (!Persist()) { Unsettled.Add(item.Patch, item.Reason); return false; }
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
