using System.Text.Json;
using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Services.Collab;

/// <summary>Dónde está otra persona: su cabezal, qué tiene seleccionado y en qué pista trabaja.</summary>
public sealed record PeerPresence(string Key, string Alias, int ColorIndex, double PlayheadSeconds, IReadOnlyList<string> SelectedClips, string? TrackId);

/// <summary>Una reserva que tiene otra persona: la clave, la pista (si es de pista) y quién la tiene.</summary>
public sealed record ReservationBlock(string Key, string TrackId, string OwnerAlias)
{
    public string Describe() => TrackId.Length > 0
        ? $"{OwnerAlias} tiene esa pista ahora. Espera a que termine o pídesela."
        : $"{OwnerAlias} está cambiando algo del proyecto entero. Espera a que termine.";
}

/// <summary>Una pista que tiene reservada alguien: <paramref name="Mine"/> si es este equipo.</summary>
public sealed record TrackLock(string TrackId, string OwnerKey, string OwnerAlias, int ColorIndex, bool Mine);

/// <summary>
/// La presencia de los demás y las reservas por pista, vistas desde la línea de tiempo. Lo propio (cabezal, selección) se
/// manda como mucho cada 100 ms y solo si cambió; lo ajeno se lee como entrada hostil (números finitos, listas cortas, textos
/// acotados). Las reservas de las pistas de lo seleccionado se piden solas y se sueltan 10 s después de dejar de interesar:
/// el tiempo que tarda el anfitrión en aceptar un cambio que acaba de hacer.
/// </summary>
public sealed partial class CollabPresence : IDisposable
{
    public const string TrackPrefix = "pista:";
    public const int MaxSelected = 32;
    public static readonly TimeSpan ReleaseDelay = TimeSpan.FromSeconds(10);

    private readonly RoomService _room;
    private readonly Func<long> _clock;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Dictionary<string, PeerPresence> _peers = new(StringComparer.Ordinal);
    private Dictionary<string, TrackLock> _locks = new(StringComparer.Ordinal);
    private (double Playhead, string[] Selected, string? Track)? _pending;
    private string _lastSent = "";
    private readonly Dictionary<string, long> _releaseAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wanted = new(StringComparer.Ordinal);

    public CollabPresence(RoomService room, Func<long>? clock = null)
    {
        _room = room;
        _clock = clock ?? (() => Environment.TickCount64);
        room.PresenceReceived += OnPresence;
        room.ReservationChanged += OnReservation;
        room.StateChanged += OnState;
        UiTask.Run(Task.Run(LoopAsync));
    }

    public IReadOnlyDictionary<string, PeerPresence> Peers
    {
        get { lock (_gate) return _peers; }
    }

    /// <summary>Las pistas reservadas, por id de pista.</summary>
    public IReadOnlyDictionary<string, TrackLock> Locks
    {
        get { lock (_gate) return _locks; }
    }

    /// <summary>Algo cambió (alguien se movió, entró, salió o tomó una pista). Se avisa en el hilo de Sala: la vista lo pasa al suyo.</summary>
    public event Action? Changed;

    public void Dispose()
    {
        _room.PresenceReceived -= OnPresence;
        _room.ReservationChanged -= OnReservation;
        _room.StateChanged -= OnState;
        _stop.Cancel();
        _stop.Dispose();
    }

    public static string KeyForTrack(string trackId) => TrackPrefix + trackId;

    /// <summary>Dónde está la persona ahora: se manda en el próximo ciclo si cambió.</summary>
    public void SetLocal(double playheadSeconds, IReadOnlyList<string> selectedClips, string? trackId)
    {
        if (!double.IsFinite(playheadSeconds))
        {
            return;
        }

        lock (_gate)
        {
            _pending = (Math.Round(playheadSeconds, 2), [.. selectedClips.Take(MaxSelected)], trackId);
        }
    }

    /// <summary>Las pistas que interesan ahora (las de lo seleccionado): se reservan y, al dejar de interesar, se sueltan tras 10 s.</summary>
    public void WantTracks(IEnumerable<string> trackIds)
    {
        HashSet<string> next = [.. trackIds.Select(KeyForTrack)];
        lock (_gate)
        {
            long now = _clock();
            foreach (string gone in _wanted.Except(next))
            {
                _releaseAt[gone] = now + (long)ReleaseDelay.TotalMilliseconds;
            }

            foreach (string key in next)
            {
                _releaseAt.Remove(key);
            }

            _wanted.Clear();
            _wanted.UnionWith(next);
        }
    }

    /// <summary>
    /// Se asegura de que las reservas son nuestras antes de mandar un cambio: pide las que faltan (pistas y <c>proyecto</c>).
    /// Devuelve la primera que tiene otra persona (para decirle «Ana tiene esta pista»), o <c>null</c> si todas son nuestras o no se
    /// pueden pedir (una clave que Sala no admite no bloquea la edición).
    /// </summary>
    public async Task<ReservationBlock?> EnsureAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        foreach (string key in keys)
        {
            if (!await _room.ReserveAsync(key, cancellationToken).ConfigureAwait(false) && Dathinel.Sala.Sala.ClaveDeReservaValida(key))
            {
                Rebuild();
                string trackId = key.StartsWith(TrackPrefix, StringComparison.Ordinal) ? key[TrackPrefix.Length..] : "";
                string? ownerKey = _room.Reservations.GetValueOrDefault(key);
                if (ownerKey is not null && ownerKey != _room.State.MyKey)
                {
                    string alias = _room.State.Members.FirstOrDefault(m => m.Key == ownerKey)?.Alias ?? "Alguien";
                    return new ReservationBlock(key, trackId, alias);
                }
            }
        }

        Rebuild();
        return null;
    }

    /// <summary>
    /// Terminó un cambio que había pedido estas reservas (el anfitrión lo aceptó o no entró): las de lo global (<c>proyecto</c>) se
    /// sueltan ya, para que otra persona pueda cambiar marcadores o la cabecera; las de pista se sueltan 10 s después si no
    /// interesan por lo seleccionado.
    /// </summary>
    public void Done(IEnumerable<string> keys)
    {
        List<string> now = [];
        lock (_gate)
        {
            long at = _clock();
            foreach (string key in keys)
            {
                if (!key.StartsWith(TrackPrefix, StringComparison.Ordinal))
                {
                    now.Add(key);
                }
                else if (!_wanted.Contains(key))
                {
                    _releaseAt[key] = at + (long)ReleaseDelay.TotalMilliseconds;
                }
            }
        }

        foreach (string key in now)
        {
            UiTask.Run(Task.Run(async () =>
            {
                try
                {
                    await _room.ReleaseAsync(key, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is CollabException or OperationCanceledException or ObjectDisposedException)
                {
                    // Sala la suelta sola a los 2 minutos sin actividad.
                }
            }));
        }
    }

    private async Task LoopAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (!_room.State.InRoom || _room.State.Phase == RoomPhase.Reconnecting)
                {
                    continue;
                }

                await SendPendingAsync().ConfigureAwait(false);
                await ReserveWantedAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendPendingAsync()
    {
        (double Playhead, string[] Selected, string? Track)? pending;
        lock (_gate)
        {
            pending = _pending;
        }

        if (pending is not { } p)
        {
            return;
        }

        string json = Serialize(p.Playhead, p.Selected, p.Track);
        if (json == _lastSent)
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            await _room.SendPresenceAsync(doc.RootElement, _stop.Token).ConfigureAwait(false);
            _lastSent = json;
        }
        catch (Exception ex) when (ex is CollabException or JsonException or OperationCanceledException or ArgumentException)
        {
            // La presencia es un adorno: si no sale ahora, saldrá en el siguiente cambio.
        }
    }

    private async Task ReserveWantedAsync()
    {
        string[] toReserve, toRelease;
        lock (_gate)
        {
            long now = _clock();
            toRelease = [.. _releaseAt.Where(kv => kv.Value <= now).Select(kv => kv.Key)];
            foreach (string key in toRelease)
            {
                _releaseAt.Remove(key);
            }

            toReserve = [.. _wanted];
        }

        IReadOnlyDictionary<string, string> held = _room.Reservations;
        string me = _room.State.MyKey;
        foreach (string key in toReserve.Where(k => !held.ContainsKey(k)))
        {
            try
            {
                await _room.ReserveAsync(key, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CollabException or OperationCanceledException)
            {
                // Se reintenta en el siguiente ciclo; un cambio real vuelve a pedirla y, si falla, lo dice.
            }
        }

        foreach (string key in toRelease.Where(k => held.TryGetValue(k, out string? owner) && owner == me))
        {
            try
            {
                await _room.ReleaseAsync(key, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CollabException or OperationCanceledException)
            {
                // Sala la suelta sola a los 2 minutos sin actividad.
            }
        }
    }

    // ---------- lo que llega ----------

    private void OnPresence(string key, JsonElement data)
    {
        if (!CollabNames.IsKey(key) || key == _room.State.MyKey || Parse(data) is not { } parsed)
        {
            return;
        }

        RoomMember? member = _room.State.Members.FirstOrDefault(m => m.Key == key);
        PeerPresence peer = new(key, member?.Alias ?? "Alguien", MemberColors.IndexFor(key), parsed.Playhead, parsed.Selected, parsed.Track);
        lock (_gate)
        {
            _peers = new Dictionary<string, PeerPresence>(_peers, StringComparer.Ordinal) { [key] = peer };
        }

        Changed?.Invoke();
    }

    private void OnReservation(string key, string? owner) => Rebuild();

    private void OnState(RoomState state)
    {
        bool changed;
        lock (_gate)
        {
            string[] gone = [.. _peers.Keys.Where(k => state.Members.All(m => m.Key != k || !m.Online))];
            changed = gone.Length > 0;
            if (changed)
            {
                _peers = _peers.Where(kv => !gone.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            }

            if (!state.InRoom)
            {
                changed |= _peers.Count > 0 || _locks.Count > 0;
                _peers = new Dictionary<string, PeerPresence>(StringComparer.Ordinal);
                _locks = new Dictionary<string, TrackLock>(StringComparer.Ordinal);
                _wanted.Clear();
                _releaseAt.Clear();
                _lastSent = "";
            }
        }

        Rebuild(changed);
    }

    private void Rebuild(bool force = false)
    {
        RoomState state = _room.State;
        Dictionary<string, TrackLock> next = new(StringComparer.Ordinal);
        foreach ((string key, string owner) in _room.Reservations)
        {
            if (!key.StartsWith(TrackPrefix, StringComparison.Ordinal) || key.Length == TrackPrefix.Length)
            {
                continue;
            }

            RoomMember? member = state.Members.FirstOrDefault(m => m.Key == owner);
            next[key[TrackPrefix.Length..]] = new TrackLock(key[TrackPrefix.Length..], owner, member?.Alias ?? "Alguien", MemberColors.IndexFor(owner), owner == state.MyKey);
        }

        bool changed;
        lock (_gate)
        {
            changed = force || next.Count != _locks.Count || next.Any(kv => !_locks.TryGetValue(kv.Key, out TrackLock? old) || old != kv.Value);
            if (changed)
            {
                _locks = next;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    // ---------- formato ----------

    internal static string Serialize(double playhead, IReadOnlyList<string> selected, string? track)
    {
        using MemoryStream ms = new();
        using (Utf8JsonWriter w = new(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("t", playhead);
            w.WriteStartArray("sel");
            foreach (string id in selected.Take(MaxSelected).Where(i => i.Length is > 0 and <= 64))
            {
                w.WriteStringValue(id);
            }

            w.WriteEndArray();
            if (track is { Length: > 0 and <= 64 })
            {
                w.WriteString("tr", track);
            }

            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static (double Playhead, string[] Selected, string? Track)? Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("t", out JsonElement t) || t.ValueKind != JsonValueKind.Number || !t.TryGetDouble(out double playhead)
            || !double.IsFinite(playhead) || playhead is < 0 or > 1e7)
        {
            return null;
        }

        List<string> selected = [];
        if (data.TryGetProperty("sel", out JsonElement sel) && sel.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement id in sel.EnumerateArray().Take(MaxSelected))
            {
                if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 64 } text && !text.Any(char.IsControl))
                {
                    selected.Add(text);
                }
            }
        }

        string? track = data.TryGetProperty("tr", out JsonElement tr) && tr.ValueKind == JsonValueKind.String && tr.GetString() is { Length: > 0 and <= 64 } s && !s.Any(char.IsControl) ? s : null;
        return (playhead, [.. selected], track);
    }
}
