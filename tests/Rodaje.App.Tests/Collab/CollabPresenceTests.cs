using System.Text.Json;
using Rodaje.App.Services.Collab;

namespace Rodaje.App.Tests.Collab;

public sealed class CollabPresenceTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void La_presencia_ajena_se_lee_como_entrada_hostil()
    {
        Assert.NotNull(CollabPresence.Parse(Json("""{"t":12.5,"sel":["c1","c2"],"tr":"v1"}""")));
        (double playhead, string[] selected, string? track) = CollabPresence.Parse(Json("""{"t":3,"sel":["a",1,null,"b\u0000c",""],"tr":"x\u0001y"}"""))!.Value;
        Assert.Equal(3, playhead);
        Assert.Equal(["a"], selected);
        Assert.Null(track);
        foreach (string bad in new[] { "[]", "7", """{"t":"1"}""", """{"sel":[]}""", """{"t":-1}""", """{"t":1e300}""", """{"t":null}""" })
        {
            Assert.Null(CollabPresence.Parse(Json(bad)));
        }

        string many = "[" + string.Join(",", Enumerable.Range(0, 500).Select(i => $"\"c{i}\"")) + "]";
        Assert.Equal(CollabPresence.MaxSelected, CollabPresence.Parse(Json($$"""{"t":1,"sel":{{many}}}"""))!.Value.Selected.Length);
    }

    [Fact]
    public void Lo_que_se_manda_cabe_y_vuelve_igual()
    {
        string json = CollabPresence.Serialize(12.34, ["c1", "c2"], "v1");
        Assert.True(json.Length < 4096);
        (double playhead, string[] selected, string? track) = CollabPresence.Parse(Json(json))!.Value;
        Assert.Equal(12.34, playhead);
        Assert.Equal(["c1", "c2"], selected);
        Assert.Equal("v1", track);
    }

    [SalaFact]
    public async Task Cada_uno_ve_el_cabezal_y_la_seleccion_del_otro_y_las_pistas_reservadas_con_su_nombre()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        long now = 0;
        using CollabPresence onHost = new(host, () => Interlocked.Read(ref now));
        using CollabPresence onGuest = new(guest, () => Interlocked.Read(ref now));
        await CollabHarness.Until(() => host.State.Members.Count == 2 && guest.State.Members.Count == 2, "dos miembros");

        onGuest.SetLocal(4.5, ["c1", "c2"], "v1");
        onGuest.WantTracks(["v1"]);
        await CollabHarness.Until(() => onHost.Peers.Count == 1, "la presencia de Luis en el anfitrión");
        PeerPresence luis = onHost.Peers.Values.Single();
        Assert.Equal("Luis", luis.Alias);
        Assert.Equal(4.5, luis.PlayheadSeconds);
        Assert.Equal(["c1", "c2"], luis.SelectedClips);
        Assert.Equal(MemberColors.IndexFor(guest.State.MyKey), luis.ColorIndex);

        await CollabHarness.Until(() => onHost.Locks.ContainsKey("v1") && onGuest.Locks.ContainsKey("v1"), "la reserva de v1 en los dos lados");
        TrackLock seenByHost = onHost.Locks["v1"];
        Assert.Equal("Luis", seenByHost.OwnerAlias);
        Assert.False(seenByHost.Mine);
        Assert.True(onGuest.Locks["v1"].Mine);

        ReservationBlock? blocked = await onHost.EnsureAsync([CollabPresence.KeyForTrack("v1"), CollabPresence.KeyForTrack("a1")]);
        Assert.Equal("Luis", blocked?.OwnerAlias);
        Assert.Equal("v1", blocked?.TrackId);
        Assert.Null(await onGuest.EnsureAsync([CollabPresence.KeyForTrack("v1")]));
        Assert.Null(await onHost.EnsureAsync([CollabPresence.KeyForTrack("a1"), "proyecto"]));

        onGuest.WantTracks([]);
        Interlocked.Add(ref now, 11_000);
        await CollabHarness.Until(() => !onHost.Locks.ContainsKey("v1"), "la pista queda libre a los 10 s de dejar de interesar");
    }

    [SalaFact]
    public async Task Al_salir_alguien_su_cabezal_desaparece()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        using CollabPresence onHost = new(host);
        using CollabPresence onGuest = new(guest);
        onGuest.SetLocal(1, [], null);
        await CollabHarness.Until(() => onHost.Peers.Count == 1, "la presencia de Luis");
        await guest.LeaveAsync();
        await guest.DisposeAsync();
        await CollabHarness.Until(() => onHost.Peers.Count == 0, "el cabezal de Luis se quita al salir");
    }
}
