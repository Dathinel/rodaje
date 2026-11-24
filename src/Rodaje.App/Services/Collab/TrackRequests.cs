using System.Text.Json;
using Dathinel.Sala;
using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Services.Collab;

/// <summary>Los mensajes directos de «Pedir la pista» (fuera del orden de la sala).</summary>
internal static class TrackRequestMessages
{
    public const string Ask = "pedir-pista";
    public const string Busy = "pista-ocupada";
    public const string Track = "pista";

    /// <summary>Quién lo manda de verdad: lo pone el anfitrión al reenviarlo, porque a un invitado todo le llega del anfitrión.</summary>
    public const string From = "de";

    /// <summary>Para quién es la respuesta: el anfitrión la reenvía solo si él mismo reenvió ese pedido.</summary>
    public const string To = "a";
}

/// <summary>Lo que el pedido de pistas necesita de la sala; en la app, <see cref="RoomService"/> con <see cref="CollabPresence"/>.</summary>
internal interface ITrackRequestRoom
{
    RoomState State { get; }

    IReadOnlyDictionary<string, string> Reservations { get; }

    Task SendDirectAsync(JsonElement payload, string? to, CancellationToken cancellationToken);

    /// <summary>La pista se cede: deja de reservarse sola mientras siga seleccionada y un cambio que la usaba la suelta al confirmarse.</summary>
    void Yield(string trackId);

    /// <summary>Suelta la reserva de la pista si sigue siendo de este equipo.</summary>
    Task ReleaseAsync(string trackId, CancellationToken cancellationToken);
}

/// <summary>Alguien pide una pista que tiene este equipo. <paramref name="FromKey"/> es para responder; nunca se muestra.</summary>
public sealed record TrackRequest(string FromKey, string FromAlias, string TrackId, string TrackName);

/// <summary>
/// «Pedir la pista»: quien choca con una pista reservada se la pide a su dueño, que la suelta o dice «Ahora no». Un invitado solo
/// habla con el anfitrión, así que el anfitrión reenvía el pedido al dueño (y su respuesta al que pidió) con quién lo manda de
/// verdad. Lo que llega es hostil: solo de miembros con permiso de editar, de pistas que existen, uno pendiente por persona y
/// pista, con colas acotadas y como mucho <see cref="RateMax"/> mensajes cada 10 s por persona. «Ya puedes editar» no necesita
/// mensaje: se ve al quedar libre la reserva.
/// </summary>
public sealed partial class TrackRequests : IDisposable
{
    public const int MaxTrackId = 64;
    public const int MaxOutgoing = 8;
    public const int MaxIncoming = 8;
    public const int MaxRelayed = 64;
    public const int RateMax = 5;
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxGestureWait = TimeSpan.FromMinutes(10);

    /// <summary>Tras el gesto, el cambio que hizo sale y se confirma con la reserva aún puesta: se suelta un poco después.</summary>
    public static readonly TimeSpan AfterGesture = TimeSpan.FromSeconds(1);

    private readonly ITrackRequestRoom _room;
    private readonly Func<string, string?> _trackName;
    private readonly Func<bool> _isBusy;
    private readonly Func<long> _clock;
    private readonly RoomService? _service;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, (string Owner, long At)> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<(string From, string Track), long> _incoming = [];
    private readonly Dictionary<(string From, string Track), (string Owner, long At)> _relayed = [];
    private readonly Dictionary<string, Queue<long>> _rate = new(StringComparer.Ordinal);

    /// <param name="trackName">El nombre de una pista del proyecto abierto por su id; <c>null</c> si no existe. Se llama desde el hilo de Sala.</param>
    /// <param name="isBusy">Hay un gesto abierto: la pista se suelta al terminarlo.</param>
    public TrackRequests(RoomService room, CollabPresence presence, Func<string, string?> trackName, Func<bool>? isBusy = null, Func<long>? clock = null)
        : this(new RoomLink(room, presence), trackName, isBusy, clock)
    {
        _service = room;
        room.DirectReceived += OnDirect;
        room.ReservationChanged += OnReservation;
        room.StateChanged += OnState;
    }

    internal TrackRequests(ITrackRequestRoom room, Func<string, string?> trackName, Func<bool>? isBusy = null, Func<long>? clock = null)
    {
        _room = room;
        _trackName = trackName;
        _isBusy = isBusy ?? (() => false);
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>Otra persona pide una pista de este equipo: la ventana ofrece «Soltar la pista» y «Ahora no». Llega en el hilo de Sala.</summary>
    public event Action<TrackRequest>? Requested;

    /// <summary>Un aviso para esta persona («Ana sigue con la pista V1», «Ya puedes editar la pista V1»). Llega en el hilo de Sala.</summary>
    public event Action<string>? Told;

    public void Dispose()
    {
        if (_service is { } room)
        {
            room.DirectReceived -= OnDirect;
            room.ReservationChanged -= OnReservation;
            room.StateChanged -= OnState;
        }

        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>El nombre de quien tiene la pista si se le puede pedir (otra persona y esta puede editar); si no, <c>null</c>.</summary>
    public string? OwnerToAsk(string trackId)
    {
        RoomState state = _room.State;
        if (!state.CanEdit || _room.Reservations.GetValueOrDefault(CollabPresence.KeyForTrack(trackId)) is not { } owner || owner == state.MyKey)
        {
            return null;
        }

        return AliasOf(state, owner);
    }

    /// <summary>Pide la pista a quien la tiene. Devuelve el texto para la persona; lanza <see cref="CollabException"/> si no se pudo.</summary>
    public async Task<string> AskAsync(string trackId, CancellationToken cancellationToken = default)
    {
        RoomState state = _room.State;
        if (!state.CanEdit)
        {
            throw new CollabException(state.MyRole != Rol.Editar
                ? "Solo puedes mirar en esta sala, así que no puedes pedir pistas."
                : "Ahora no hay conexión con la sala. Pide la pista cuando vuelva.");
        }

        string name = _trackName(trackId) ?? throw new CollabException("Esa pista ya no está en el proyecto.");
        if (_room.Reservations.GetValueOrDefault(CollabPresence.KeyForTrack(trackId)) is not { } owner || owner == state.MyKey)
        {
            return $"La pista {name} ya está libre: puedes editarla.";
        }

        string alias = AliasOf(state, owner);
        lock (_gate)
        {
            Expire(_clock());
            if (_outgoing.TryGetValue(trackId, out (string Owner, long At) pending) && pending.Owner == owner)
            {
                return $"Ya le pediste la pista {name} a {alias}. Espera su respuesta.";
            }

            if (!_outgoing.ContainsKey(trackId) && _outgoing.Count >= MaxOutgoing)
            {
                throw new CollabException("Ya pediste muchas pistas a la vez. Espera alguna respuesta y vuelve a probar.");
            }

            _outgoing[trackId] = (owner, _clock());
        }

        try
        {
            await _room.SendDirectAsync(ShareMessages.Make(TrackRequestMessages.Ask, w => w.WriteString(TrackRequestMessages.Track, trackId)),
                state.IsHost ? owner : null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabException or OperationCanceledException)
        {
            lock (_gate)
            {
                _outgoing.Remove(trackId);
            }

            throw;
        }

        return $"Le pediste la pista {name} a {alias}. Te avisaremos cuando la suelte.";
    }

    /// <summary>«Soltar la pista»: se suelta ya o, si hay un gesto a medias, al terminarlo.</summary>
    public async Task YieldAsync(TrackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            foreach ((string From, string Track) key in _incoming.Keys.Where(k => k.Track == request.TrackId).ToList())
            {
                _incoming.Remove(key);
            }
        }

        _room.Yield(request.TrackId);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        CancellationToken ct = linked.Token;
        try
        {
            if (_isBusy())
            {
                long until = _clock() + (long)MaxGestureWait.TotalMilliseconds;
                while (_isBusy() && _clock() < until)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }

                await Task.Delay(AfterGesture, ct).ConfigureAwait(false);
            }

            await _room.ReleaseAsync(request.TrackId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (CollabException)
        {
            Told?.Invoke($"No se pudo soltar la pista {request.TrackName}. Se soltará sola a los 2 minutos sin usarla.");
            return;
        }

        Told?.Invoke($"Soltaste la pista {request.TrackName}. {request.FromAlias} ya puede editarla.");
    }

    /// <summary>«Ahora no»: quien la pidió ve que la pista sigue ocupada.</summary>
    public async Task DeclineAsync(TrackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (!_incoming.Remove((request.FromKey, request.TrackId)))
            {
                return;
            }
        }

        RoomState state = _room.State;
        try
        {
            await _room.SendDirectAsync(ShareMessages.Make(TrackRequestMessages.Busy, w =>
            {
                w.WriteString(TrackRequestMessages.Track, request.TrackId);
                if (!state.IsHost)
                {
                    w.WriteString(TrackRequestMessages.To, request.FromKey);
                }
            }), state.IsHost ? request.FromKey : null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabException or OperationCanceledException)
        {
            // Es una cortesía: sin respuesta, el pedido caduca solo.
        }
    }

    // ---------- lo que llega ----------

    internal void OnDirect(string from, JsonElement message)
    {
        string? type = ShareMessages.Type(message);
        RoomState state = _room.State;
        if (type is not (TrackRequestMessages.Ask or TrackRequestMessages.Busy) || !state.InRoom || !CollabNames.IsKey(from) || from == state.MyKey
            || !message.TryGetProperty(TrackRequestMessages.Track, out JsonElement t) || t.ValueKind != JsonValueKind.String
            || t.GetString() is not { Length: > 0 and <= MaxTrackId } trackId || trackId.Any(char.IsControl) || _trackName(trackId) is not { } trackName)
        {
            return;
        }

        if (state.IsHost)
        {
            FromGuest(state, from, type, message, trackId, trackName);
        }
        else if (from == state.Members.FirstOrDefault(m => m.IsHost)?.Key)
        {
            FromHost(state, from, type, message, trackId, trackName);
        }
    }

    internal void OnReservation(string key, string? owner)
    {
        if (!key.StartsWith(CollabPresence.TrackPrefix, StringComparison.Ordinal))
        {
            return;
        }

        string trackId = key[CollabPresence.TrackPrefix.Length..];
        RoomState state = _room.State;
        string? text = null;
        lock (_gate)
        {
            if (owner != state.MyKey)
            {
                foreach ((string From, string Track) k in _incoming.Keys.Where(k => k.Track == trackId).ToList())
                {
                    _incoming.Remove(k);
                }
            }

            foreach ((string From, string Track) k in _relayed.Where(kv => kv.Key.Track == trackId && kv.Value.Owner != owner).Select(kv => kv.Key).ToList())
            {
                _relayed.Remove(k);
            }

            if (_outgoing.TryGetValue(trackId, out (string Owner, long At) pending) && pending.Owner != owner)
            {
                _outgoing.Remove(trackId);
                string name = _trackName(trackId) ?? "";
                if (name.Length > 0 && (owner is null || owner == state.MyKey))
                {
                    text = $"Ya puedes editar la pista {name}.";
                }
                else if (name.Length > 0)
                {
                    text = $"{AliasOf(state, owner!)} tomó la pista {name}. Si la necesitas, pídesela.";
                }
            }
        }

        if (text is not null)
        {
            Told?.Invoke(text);
        }
    }

    private void OnState(RoomState state)
    {
        if (!state.InRoom)
        {
            lock (_gate)
            {
                _outgoing.Clear();
                _incoming.Clear();
                _relayed.Clear();
                _rate.Clear();
            }
        }
    }

    /// <summary>En el anfitrión, de un invitado: o es para este equipo, o se reenvía al dueño (pedido) o a quien pidió (respuesta).</summary>
    private void FromGuest(RoomState state, string from, string type, JsonElement message, string trackId, string trackName)
    {
        if (Editor(state, from) is not { } sender || !Admit(from))
        {
            return;
        }

        if (type == TrackRequestMessages.Ask)
        {
            string? owner = _room.Reservations.GetValueOrDefault(CollabPresence.KeyForTrack(trackId));
            if (owner is null || owner == from)
            {
                return;
            }

            if (owner == state.MyKey)
            {
                Show(sender, trackId, trackName);
                return;
            }

            lock (_gate)
            {
                Expire(_clock());
                if (_relayed.ContainsKey((from, trackId)) || _relayed.Count >= MaxRelayed)
                {
                    return;
                }

                _relayed[(from, trackId)] = (owner, _clock());
            }

            Send(ShareMessages.Make(TrackRequestMessages.Ask, w =>
            {
                w.WriteString(TrackRequestMessages.Track, trackId);
                w.WriteString(TrackRequestMessages.From, from);
            }), owner);
            return;
        }

        if (!message.TryGetProperty(TrackRequestMessages.To, out JsonElement a) || a.ValueKind != JsonValueKind.String || a.GetString() is not { } to || !CollabNames.IsKey(to))
        {
            return;
        }

        if (to == state.MyKey)
        {
            Answered(state, from, trackId, trackName);
            return;
        }

        lock (_gate)
        {
            if (!_relayed.TryGetValue((to, trackId), out (string Owner, long At) relay) || relay.Owner != from)
            {
                return;
            }

            _relayed.Remove((to, trackId));
        }

        Send(ShareMessages.Make(TrackRequestMessages.Busy, w =>
        {
            w.WriteString(TrackRequestMessages.Track, trackId);
            w.WriteString(TrackRequestMessages.From, from);
        }), to);
    }

    /// <summary>En un invitado, del anfitrión: el pedido o la respuesta es del anfitrión o de quien dice «de».</summary>
    private void FromHost(RoomState state, string host, string type, JsonElement message, string trackId, string trackName)
    {
        string origin = host;
        if (message.TryGetProperty(TrackRequestMessages.From, out JsonElement d))
        {
            if (d.ValueKind != JsonValueKind.String || d.GetString() is not { } key || !CollabNames.IsKey(key) || key == state.MyKey)
            {
                return;
            }

            origin = key;
        }

        if (Editor(state, origin) is not { } sender || !Admit(origin))
        {
            return;
        }

        if (type == TrackRequestMessages.Ask)
        {
            if (_room.Reservations.GetValueOrDefault(CollabPresence.KeyForTrack(trackId)) == state.MyKey)
            {
                Show(sender, trackId, trackName);
            }
        }
        else
        {
            Answered(state, origin, trackId, trackName);
        }
    }

    private void Show(RoomMember sender, string trackId, string trackName)
    {
        lock (_gate)
        {
            Expire(_clock());
            if (_incoming.ContainsKey((sender.Key, trackId)) || _incoming.Count >= MaxIncoming)
            {
                return;
            }

            _incoming[(sender.Key, trackId)] = _clock();
        }

        Requested?.Invoke(new TrackRequest(sender.Key, sender.Alias, trackId, trackName));
    }

    private void Answered(RoomState state, string owner, string trackId, string trackName)
    {
        lock (_gate)
        {
            if (!_outgoing.TryGetValue(trackId, out (string Owner, long At) pending) || pending.Owner != owner)
            {
                return;
            }

            _outgoing.Remove(trackId);
        }

        Told?.Invoke($"{AliasOf(state, owner)} sigue con la pista {trackName}.");
    }

    private void Send(JsonElement payload, string to) => UiTask.Run(SendQuietlyAsync(payload, to));

    private async Task SendQuietlyAsync(JsonElement payload, string to)
    {
        try
        {
            await _room.SendDirectAsync(payload, to, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CollabException or OperationCanceledException or ObjectDisposedException)
        {
            // Sin reenvío, el pedido caduca solo; quien pidió puede volver a pedirla.
        }
    }

    private static RoomMember? Editor(RoomState state, string key) => state.Members.FirstOrDefault(m => m.Key == key && m.Role == Rol.Editar && !m.IsMe);

    private static string AliasOf(RoomState state, string key) => state.Members.FirstOrDefault(m => m.Key == key)?.Alias ?? "Alguien";

    /// <summary>Como mucho <see cref="RateMax"/> mensajes cada 10 s por persona; solo se llama con miembros, así que la tabla es corta.</summary>
    private bool Admit(string sender)
    {
        lock (_gate)
        {
            long now = _clock();
            if (!_rate.TryGetValue(sender, out Queue<long>? times))
            {
                _rate[sender] = times = new Queue<long>();
            }

            while (times.Count > 0 && now - times.Peek() >= (long)RateWindow.TotalMilliseconds)
            {
                times.Dequeue();
            }

            if (times.Count >= RateMax)
            {
                return false;
            }

            times.Enqueue(now);
            return true;
        }
    }

    private void Expire(long now)
    {
        long limit = (long)Expiry.TotalMilliseconds;
        foreach (string k in _outgoing.Where(kv => now - kv.Value.At >= limit).Select(kv => kv.Key).ToList())
        {
            _outgoing.Remove(k);
        }

        foreach ((string From, string Track) k in _incoming.Where(kv => now - kv.Value >= limit).Select(kv => kv.Key).ToList())
        {
            _incoming.Remove(k);
        }

        foreach ((string From, string Track) k in _relayed.Where(kv => now - kv.Value.At >= limit).Select(kv => kv.Key).ToList())
        {
            _relayed.Remove(k);
        }
    }

    private sealed class RoomLink(RoomService room, CollabPresence presence) : ITrackRequestRoom
    {
        public RoomState State => room.State;

        public IReadOnlyDictionary<string, string> Reservations => room.Reservations;

        public Task SendDirectAsync(JsonElement payload, string? to, CancellationToken cancellationToken) => room.SendDirectAsync(payload, to, cancellationToken);

        public void Yield(string trackId) => presence.Yield(trackId);

        public Task ReleaseAsync(string trackId, CancellationToken cancellationToken)
        {
            string key = CollabPresence.KeyForTrack(trackId);
            return room.Reservations.GetValueOrDefault(key) == room.State.MyKey ? room.ReleaseAsync(key, cancellationToken) : Task.CompletedTask;
        }
    }
}
