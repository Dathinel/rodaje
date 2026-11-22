using System.Text.Json;
using Dathinel.Sala;
using Rodaje.App.Services.Collab;

namespace Rodaje.App.Tests.Collab;

public sealed class TrackRequestsTests
{
    private static readonly string Host = new('a', 64);
    private static readonly string Ana = new('b', 64);
    private static readonly string Luis = new('c', 64);
    private static readonly string Eva = new('d', 64);
    private static readonly string Stranger = new('e', 64);

    private static readonly Dictionary<string, string> Tracks = new(StringComparer.Ordinal) { ["v1"] = "Video 1", ["a1"] = "Audio 1" };

    private sealed class FakeRoom(string me) : ITrackRequestRoom
    {
        public readonly List<(JsonElement Payload, string? To)> Sent = [];
        public readonly List<string> Yielded = [];
        public readonly List<string> Released = [];
        public Dictionary<string, string> Held { get; } = new(StringComparer.Ordinal);
        public Rol MyRole { get; set; } = Rol.Editar;
        public Rol EvaRole { get; set; } = Rol.Mirar;
        public bool Fail { get; set; }

        public RoomState State => new(me == Host ? RoomPhase.Hosting : RoomPhase.Connected, me == Host, me, Alias(me), MyRole,
            [Member(Host, "Sara"), Member(Ana, "Ana"), Member(Luis, "Luis"), Member(Eva, "Eva", EvaRole)], [], null);

        public IReadOnlyDictionary<string, string> Reservations => Held;

        private RoomMember Member(string key, string alias, Rol role = Rol.Editar) =>
            new(key, alias, key == me ? MyRole : role, key == Host, key == me, "", 0);

        private static string Alias(string key) => key == Host ? "Sara" : key == Ana ? "Ana" : key == Luis ? "Luis" : "Eva";

        public Task SendDirectAsync(JsonElement payload, string? to, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new CollabException("No hay ninguna sala abierta.");
            }

            Sent.Add((payload.Clone(), to));
            return Task.CompletedTask;
        }

        public void Yield(string trackId) => Yielded.Add(trackId);

        public Task ReleaseAsync(string trackId, CancellationToken cancellationToken)
        {
            Released.Add(trackId);
            return Task.CompletedTask;
        }
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static string Key(string track) => CollabPresence.KeyForTrack(track);

    private static (TrackRequests Requests, FakeRoom Room, List<TrackRequest> Asked, List<string> Told) Make(string me, Func<long>? clock = null, Func<bool>? busy = null,
        Dictionary<string, string>? tracks = null)
    {
        FakeRoom room = new(me);
        Dictionary<string, string> names = tracks ?? Tracks;
        TrackRequests requests = new(room, id => names.GetValueOrDefault(id), busy, clock ?? (() => 0));
        List<TrackRequest> asked = [];
        List<string> told = [];
        requests.Requested += asked.Add;
        requests.Told += told.Add;
        return (requests, room, asked, told);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) ? v.GetString() : null;

    [Fact]
    public async Task El_invitado_pide_al_anfitrion_una_sola_vez_y_el_anfitrion_ve_el_pedido()
    {
        (TrackRequests guest, FakeRoom guestRoom, _, _) = Make(Ana);
        using TrackRequests g = guest;
        guestRoom.Held[Key("v1")] = Host;

        Assert.Equal("Sara", guest.OwnerToAsk("v1"));
        Assert.Contains("Le pediste la pista Video 1 a Sara", await guest.AskAsync("v1"), StringComparison.Ordinal);
        Assert.Contains("Ya le pediste", await guest.AskAsync("v1"), StringComparison.Ordinal);
        (JsonElement payload, string? to) = Assert.Single(guestRoom.Sent);
        Assert.Null(to);
        Assert.Equal("""{"tipo":"pedir-pista","pista":"v1"}""", payload.GetRawText());

        (TrackRequests host, FakeRoom hostRoom, List<TrackRequest> asked, _) = Make(Host);
        using TrackRequests h = host;
        hostRoom.Held[Key("v1")] = Host;
        host.OnDirect(Ana, payload);
        host.OnDirect(Ana, payload);
        TrackRequest request = Assert.Single(asked);
        Assert.Equal(new TrackRequest(Ana, "Ana", "v1", "Video 1"), request);
        Assert.Empty(hostRoom.Sent);
    }

    [Fact]
    public async Task Solo_se_ofrece_pedir_cuando_la_tiene_otra_persona_y_se_puede_editar()
    {
        (TrackRequests requests, FakeRoom room, _, _) = Make(Ana);
        using TrackRequests r = requests;
        Assert.Null(requests.OwnerToAsk("v1"));
        Assert.Contains("ya está libre", await requests.AskAsync("v1"), StringComparison.Ordinal);
        room.Held[Key("v1")] = Ana;
        Assert.Null(requests.OwnerToAsk("v1"));
        room.Held[Key("v1")] = Luis;
        Assert.Equal("Luis", requests.OwnerToAsk("v1"));
        await Assert.ThrowsAsync<CollabException>(() => requests.AskAsync("no-existe"));

        room.MyRole = Rol.Mirar;
        Assert.Null(requests.OwnerToAsk("v1"));
        CollabException ex = await Assert.ThrowsAsync<CollabException>(() => requests.AskAsync("v1"));
        Assert.Contains("Solo puedes mirar", ex.Message, StringComparison.Ordinal);
        Assert.Empty(room.Sent);
    }

    [Fact]
    public async Task Si_el_envio_falla_el_pedido_no_queda_pendiente()
    {
        (TrackRequests requests, FakeRoom room, _, _) = Make(Ana);
        using TrackRequests r = requests;
        room.Held[Key("v1")] = Host;
        room.Fail = true;
        await Assert.ThrowsAsync<CollabException>(() => requests.AskAsync("v1"));
        room.Fail = false;
        Assert.Contains("Le pediste", await requests.AskAsync("v1"), StringComparison.Ordinal);
    }

    [Fact]
    public void El_anfitrion_reenvia_el_pedido_al_dueno_y_su_respuesta_solo_a_quien_pidio()
    {
        (TrackRequests host, FakeRoom room, List<TrackRequest> asked, List<string> told) = Make(Host);
        using TrackRequests h = host;
        room.Held[Key("v1")] = Luis;

        host.OnDirect(Ana, Json("""{"tipo":"pedir-pista","pista":"v1"}"""));
        host.OnDirect(Ana, Json("""{"tipo":"pedir-pista","pista":"v1"}"""));
        (JsonElement forward, string? to) = Assert.Single(room.Sent);
        Assert.Equal(Luis, to);
        Assert.Equal("pedir-pista", Str(forward, "tipo"));
        Assert.Equal(Ana, Str(forward, "de"));
        Assert.Empty(asked);

        // Una respuesta para alguien que no pidió, o de quien no es el dueño, no se reenvía.
        host.OnDirect(Luis, Json($$"""{"tipo":"pista-ocupada","pista":"v1","a":"{{Eva}}"}"""));
        host.OnDirect(Eva, Json($$"""{"tipo":"pista-ocupada","pista":"v1","a":"{{Ana}}"}"""));
        Assert.Single(room.Sent);

        host.OnDirect(Luis, Json($$"""{"tipo":"pista-ocupada","pista":"v1","a":"{{Ana}}"}"""));
        Assert.Equal(2, room.Sent.Count);
        (JsonElement back, string? backTo) = room.Sent[1];
        Assert.Equal(Ana, backTo);
        Assert.Equal("pista-ocupada", Str(back, "tipo"));
        Assert.Equal(Luis, Str(back, "de"));

        // Ya contestado: otra respuesta igual no se reenvía.
        host.OnDirect(Luis, Json($$"""{"tipo":"pista-ocupada","pista":"v1","a":"{{Ana}}"}"""));
        Assert.Equal(2, room.Sent.Count);
        Assert.Empty(told);
    }

    [Fact]
    public async Task Ahora_no_llega_como_sigue_con_la_pista()
    {
        // Luis (invitado) tiene la pista y Ana se la pidió por el anfitrión.
        (TrackRequests owner, FakeRoom ownerRoom, List<TrackRequest> asked, _) = Make(Luis);
        using TrackRequests o = owner;
        ownerRoom.Held[Key("v1")] = Luis;
        owner.OnDirect(Host, Json($$"""{"tipo":"pedir-pista","pista":"v1","de":"{{Ana}}"}"""));
        TrackRequest request = Assert.Single(asked);
        Assert.Equal("Ana", request.FromAlias);

        await owner.DeclineAsync(request);
        await owner.DeclineAsync(request);
        (JsonElement answer, string? to) = Assert.Single(ownerRoom.Sent);
        Assert.Null(to);
        Assert.Equal("pista-ocupada", Str(answer, "tipo"));
        Assert.Equal(Ana, Str(answer, "a"));

        (TrackRequests asker, FakeRoom askerRoom, _, List<string> told) = Make(Ana);
        using TrackRequests a = asker;
        askerRoom.Held[Key("v1")] = Luis;
        await asker.AskAsync("v1");

        // De quien no es el dueño al que se pidió, o sin pedido de esa pista: nada.
        asker.OnDirect(Host, Json($$"""{"tipo":"pista-ocupada","pista":"v1","de":"{{Eva}}"}"""));
        asker.OnDirect(Host, Json("""{"tipo":"pista-ocupada","pista":"v1"}"""));
        asker.OnDirect(Luis, Json("""{"tipo":"pista-ocupada","pista":"v1"}"""));
        asker.OnDirect(Host, Json($$"""{"tipo":"pista-ocupada","pista":"a1","de":"{{Luis}}"}"""));
        Assert.Empty(told);

        asker.OnDirect(Host, Json($$"""{"tipo":"pista-ocupada","pista":"v1","de":"{{Luis}}"}"""));
        Assert.Equal(["Luis sigue con la pista Video 1."], told);
        Assert.Contains("Le pediste", await asker.AskAsync("v1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task El_anfitrion_dueno_contesta_directo_a_quien_pidio()
    {
        (TrackRequests host, FakeRoom room, List<TrackRequest> asked, _) = Make(Host);
        using TrackRequests h = host;
        room.Held[Key("v1")] = Host;
        host.OnDirect(Ana, Json("""{"tipo":"pedir-pista","pista":"v1"}"""));
        await host.DeclineAsync(Assert.Single(asked));
        (JsonElement answer, string? to) = Assert.Single(room.Sent);
        Assert.Equal(Ana, to);
        Assert.Equal("""{"tipo":"pista-ocupada","pista":"v1"}""", answer.GetRawText());
    }

    [Fact]
    public async Task Soltar_la_pista_espera_al_final_del_gesto_y_quien_la_pidio_ve_que_ya_puede_editar()
    {
        bool busy = true;
        (TrackRequests owner, FakeRoom room, List<TrackRequest> asked, List<string> ownerTold) = Make(Luis, busy: () => Volatile.Read(ref busy));
        using TrackRequests o = owner;
        room.Held[Key("v1")] = Luis;
        owner.OnDirect(Host, Json($$"""{"tipo":"pedir-pista","pista":"v1","de":"{{Ana}}"}"""));
        Task yielding = owner.YieldAsync(Assert.Single(asked));
        await Task.Delay(300);
        Assert.Equal(["v1"], room.Yielded);
        Assert.Empty(room.Released);
        Volatile.Write(ref busy, false);
        await yielding.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["v1"], room.Released);
        Assert.Equal(["Soltaste la pista Video 1. Ana ya puede editarla."], ownerTold);

        (TrackRequests asker, FakeRoom askerRoom, _, List<string> told) = Make(Ana);
        using TrackRequests a = asker;
        askerRoom.Held[Key("v1")] = Luis;
        await asker.AskAsync("v1");
        asker.OnReservation(Key("a1"), null);
        asker.OnReservation("proyecto", null);
        Assert.Empty(told);
        askerRoom.Held.Remove(Key("v1"));
        asker.OnReservation(Key("v1"), null);
        asker.OnReservation(Key("v1"), null);
        Assert.Equal(["Ya puedes editar la pista Video 1."], told);
    }

    [Fact]
    public void Lo_que_llega_es_hostil_solo_miembros_que_editan_y_pistas_que_existen()
    {
        (TrackRequests host, FakeRoom room, List<TrackRequest> asked, _) = Make(Host);
        using TrackRequests h = host;
        room.Held[Key("v1")] = Host;
        room.Held[Key("a1")] = Host;
        foreach (string bad in new[]
        {
            """{"tipo":"pedir-pista"}""",
            """{"tipo":"pedir-pista","pista":7}""",
            """{"tipo":"pedir-pista","pista":null}""",
            """{"tipo":"pedir-pista","pista":""}""",
            """{"tipo":"pedir-pista","pista":"v\u00001"}""",
            """{"tipo":"pedir-pista","pista":"no-existe"}""",
            $$"""{"tipo":"pedir-pista","pista":"{{new string('v', 300)}}"}""",
            """{"tipo":"otra-cosa","pista":"v1"}""",
            """["pedir-pista"]""",
            "7",
        })
        {
            host.OnDirect(Ana, Json(bad));
        }

        host.OnDirect(Eva, Json("""{"tipo":"pedir-pista","pista":"v1"}"""));
        host.OnDirect(Stranger, Json("""{"tipo":"pedir-pista","pista":"v1"}"""));
        host.OnDirect("no-es-una-clave", Json("""{"tipo":"pedir-pista","pista":"v1"}"""));
        host.OnDirect(Host, Json("""{"tipo":"pedir-pista","pista":"v1"}"""));
        Assert.Empty(asked);
        Assert.Empty(room.Sent);

        // Un invitado solo atiende lo que viene del anfitrión, con un «de» que sea un miembro que edita.
        (TrackRequests guest, FakeRoom guestRoom, List<TrackRequest> guestAsked, _) = Make(Luis);
        using TrackRequests g = guest;
        guestRoom.Held[Key("v1")] = Luis;
        guest.OnDirect(Ana, Json("""{"tipo":"pedir-pista","pista":"v1"}"""));
        guest.OnDirect(Host, Json($$"""{"tipo":"pedir-pista","pista":"v1","de":"{{Eva}}"}"""));
        guest.OnDirect(Host, Json($$"""{"tipo":"pedir-pista","pista":"v1","de":"{{Stranger}}"}"""));
        guest.OnDirect(Host, Json($$"""{"tipo":"pedir-pista","pista":"v1","de":"{{Luis}}"}"""));
        guest.OnDirect(Host, Json("""{"tipo":"pedir-pista","pista":"v1","de":7}"""));
        guest.OnDirect(Host, Json($$"""{"tipo":"pedir-pista","pista":"a1","de":"{{Ana}}"}"""));
        Assert.Empty(guestAsked);
        guest.OnDirect(Host, Json($$"""{"tipo":"pedir-pista","pista":"v1","de":"{{Ana}}"}"""));
        Assert.Equal("Ana", Assert.Single(guestAsked).FromAlias);
    }

    [Fact]
    public void El_ritmo_y_las_colas_estan_acotados()
    {
        long now = 0;
        Dictionary<string, string> many = Enumerable.Range(0, 40).ToDictionary(i => "p" + i, i => "Pista " + i, StringComparer.Ordinal);
        (TrackRequests host, FakeRoom room, List<TrackRequest> asked, _) = Make(Host, () => Interlocked.Read(ref now), tracks: many);
        using TrackRequests h = host;
        foreach (string id in many.Keys)
        {
            room.Held[Key(id)] = Host;
        }

        foreach (int i in Enumerable.Range(0, 40))
        {
            host.OnDirect(Ana, Json($$"""{"tipo":"pedir-pista","pista":"p{{i}}"}"""));
        }

        Assert.Equal(TrackRequests.RateMax, asked.Count);

        Interlocked.Add(ref now, (long)TrackRequests.RateWindow.TotalMilliseconds);
        foreach (int i in Enumerable.Range(10, 10))
        {
            host.OnDirect(Luis, Json($$"""{"tipo":"pedir-pista","pista":"p{{i}}"}"""));
        }

        Assert.Equal(TrackRequests.MaxIncoming, asked.Count);

        // Pasado el tiempo, los pedidos sin respuesta caducan y vuelve a haber sitio.
        Interlocked.Add(ref now, (long)TrackRequests.Expiry.TotalMilliseconds);
        host.OnDirect(Ana, Json("""{"tipo":"pedir-pista","pista":"p30"}"""));
        Assert.Equal(TrackRequests.MaxIncoming + 1, asked.Count);
    }

    [SalaFact]
    public async Task Por_la_sala_el_anfitrion_pide_la_pista_el_invitado_la_suelta_y_no_vuelve_a_reservarla_sola()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService hr = host;
        await using RoomService gr = guest;
        using CollabPresence onHost = new(host);
        using CollabPresence onGuest = new(guest);
        using TrackRequests hostAsks = new(host, onHost, id => Tracks.GetValueOrDefault(id));
        using TrackRequests guestAsks = new(guest, onGuest, id => Tracks.GetValueOrDefault(id));
        System.Collections.Concurrent.ConcurrentQueue<TrackRequest> asked = new();
        System.Collections.Concurrent.ConcurrentQueue<string> told = new();
        guestAsks.Requested += asked.Enqueue;
        hostAsks.Told += told.Enqueue;
        await CollabHarness.Until(() => host.State.Members.Count == 2 && guest.State.Members.Count == 2, "dos miembros");

        onGuest.WantTracks(["v1"]);
        await CollabHarness.Until(() => hostAsks.OwnerToAsk("v1") == "Luis", "la reserva de v1 de Luis en el anfitrión");
        Assert.Contains("Le pediste la pista Video 1 a Luis", await hostAsks.AskAsync("v1"), StringComparison.Ordinal);
        await CollabHarness.Until(() => !asked.IsEmpty, "el pedido llega a Luis");
        asked.TryDequeue(out TrackRequest? request);
        Assert.Equal("v1", request!.TrackId);

        await guestAsks.YieldAsync(request);
        await CollabHarness.Until(() => told.Contains("Ya puedes editar la pista Video 1."), "el anfitrión ve que ya puede editar");
        await Task.Delay(500);
        Assert.False(host.Reservations.ContainsKey(Key("v1")), "con v1 aún seleccionada, Luis no vuelve a reservarla");
    }

    [Fact]
    public async Task Al_cambiar_de_dueno_el_pedido_se_cierra_y_se_dice_quien_la_tiene()
    {
        (TrackRequests asker, FakeRoom room, _, List<string> told) = Make(Ana);
        using TrackRequests a = asker;
        room.Held[Key("v1")] = Luis;
        await asker.AskAsync("v1");
        room.Held[Key("v1")] = Host;
        asker.OnReservation(Key("v1"), Host);
        Assert.Equal(["Sara tomó la pista Video 1. Si la necesitas, pídesela."], told);
        Assert.Contains("Le pediste la pista Video 1 a Sara", await asker.AskAsync("v1"), StringComparison.Ordinal);
    }
}
