using System.Text.Json;
using Dathinel.Sala;
using Rodaje.App.Services.Collab;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Collab;

/// <summary>Los casos de la revisión independiente de la edición compartida, cada uno como se producía antes de su arreglo.</summary>
public sealed class CollabReviewFixesTests
{
    private static MediaRef Medium(string id, string name) => new()
    {
        Id = id,
        Path = name,
        Name = name,
        Duration = Time.FromSeconds(5),
        HasVideo = true,
        FrameRate = Rational.Fps30,
        Width = 640,
        Height = 360,
    };

    /// <summary>Sala con el anfitrión editando el proyecto de ejemplo; <paramref name="guestBusy"/> simula un gesto abierto en el invitado.</summary>
    private static async Task<(Replica Host, Replica Guest)> StartAsync(Func<bool>? guestBusy = null)
    {
        Replica host = new(), guest = new();
        await host.Room.HostAsync("Ana", CollabHarness.TempFolder());
        host.Attach(ModelSamples.ValidProject());
        await host.Ui.Post(() => host.Engine.StartAsHost());

        CollabInvitation invite = await host.Room.CreateInviteAsync(TimeSpan.FromMinutes(10));
        Task joining = guest.Room.JoinAsync(invite.Link, "Luis", CollabHarness.TempFolder());
        await CollabHarness.Until(() => host.Room.State.Requests.Count == 1, "la solicitud de Luis");
        await host.Room.ApproveAsync(host.Room.State.Requests[0].Key, Rol.Editar);
        await joining.WaitAsync(TimeSpan.FromSeconds(20));

        (string json, long seq) = host.Engine.CaptureForGuest();
        ProjectParseResult parsed = ProjectWire.TryParse(json);
        Assert.True(parsed.IsValid, parsed.Rejection?.Message);
        guest.Attach(parsed.Project!, guestBusy);
        await guest.Ui.Post(() => guest.Engine.StartAsGuest(parsed.Project!.Clone(), parsed.Versions!, seq));
        await CollabHarness.Until(() => host.Room.State.Members.Count == 2 && guest.Room.State.Members.Count == 2, "dos miembros");
        return (host, guest);
    }

    [SalaFact]
    public async Task Dos_altas_de_medio_a_la_vez_quedan_en_el_mismo_orden_en_los_dos_equipos()
    {
        // Revisión, hallazgo 1: dar de alta un medio no pide reservas. Si B ya veía el suyo cuando aplicó el de A,
        // su lista quedaba al revés de la confirmada y ninguna comprobación lo notaba.
        bool[] busy = [true];   // el invitado retiene lo ajeno como si tuviera un gesto abierto
        (Replica host, Replica guest) = await StartAsync(() => busy[0]);
        await using Replica h = host;
        await using Replica g = guest;

        await host.Edit(p => p.Media.Add(Medium("mz-ana", "ana.mp4")), "Añadir ana.mp4");
        await CollabHarness.Until(() => guest.Engine.DeferredCount == 1, "la ordenada del anfitrión retenida en el invitado");

        await guest.Edit(p => p.Media.Add(Medium("mz-luis", "luis.mp4")), "Añadir luis.mp4");
        await CollabHarness.Until(() => host.Live.Media.Any(m => m.Id == "mz-luis"), "la alta del invitado confirmada en el anfitrión");

        busy[0] = false;
        await CollabHarness.Until(() => guest.Live.Media.Any(m => m.Id == "mz-ana"), "la alta del anfitrión en el invitado");

        await CollabHarness.Until(() => host.HashOnUi() == guest.HashOnUi(), "mismo estado compartido", 15);
        Assert.Equal(host.Live.Media.Select(m => m.Id), guest.Live.Media.Select(m => m.Id));
    }

    [SalaFact]
    public async Task Una_ordenada_que_suplanta_el_contenido_de_un_pendiente_desincroniza()
    {
        // Revisión, hallazgo 2: el invitado solo comparaba el Patch.Id de la confirmación con su pendiente. Un
        // anfitrión hostil podía devolver otro contenido con el mismo identificador y se aceptaba como propio.
        RecordingHandler hh = new();
        RoomService host = new(CollabHarness.Env(), hh);
        await host.HostAsync("Ana", CollabHarness.TempFolder());
        Replica guest = new();
        await using RoomService h = host;
        await using Replica g = guest;

        CollabInvitation invite = await host.CreateInviteAsync(TimeSpan.FromMinutes(10));
        Task joining = guest.Room.JoinAsync(invite.Link, "Luis", CollabHarness.TempFolder());
        await CollabHarness.Until(() => host.State.Requests.Count == 1, "la solicitud de Luis");
        await host.ApproveAsync(host.State.Requests[0].Key, Rol.Editar);
        await joining.WaitAsync(TimeSpan.FromSeconds(20));

        guest.Attach(ModelSamples.ValidProject());
        await guest.Ui.Post(() => guest.Engine.StartAsGuest(ModelSamples.ValidProject(), new ElementVersions(), 0));

        // La validación del anfitrión retiene la ordenada real mientras la prueba inyecta la falsa.
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hh.Decide = payload =>
        {
            seen.TrySetResult(payload.GetRawText());
            release.Task.Wait(TimeSpan.FromSeconds(30));
            return Veredicto.Si;
        };

        await guest.Edit(p => p.Media.Add(Medium("mz", "z.mp4")), "Añadir z");
        string sent = await seen.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // La ordenada falsa lleva el mismo Patch.Id que el pendiente, el autor del invitado y otro contenido.
        var patch = System.Text.Json.Nodes.JsonNode.Parse(sent)!.AsObject();
        patch["desc"] = "El contenido lo cambió quien hospeda";
        var orden = new Ordenado(0, 1, Guid.NewGuid(), guest.Room.State.MyKey,
            JsonDocument.Parse(patch.ToJsonString()).RootElement.Clone());
        guest.Engine.Apply(orden);
        release.TrySetResult();

        await CollabHarness.Until(() => !guest.Desyncs.IsEmpty, "la sesión se marca desincronizada", 10);
    }

    [Fact]
    public async Task Un_pendiente_descartado_mientras_esperaba_la_reserva_no_se_manda()
    {
        // Revisión, hallazgo 6: el envío comprobaba el pendiente antes de esperar las reservas; si la recuperación lo
        // limpiaba durante la espera, el cambio salía igualmente y reaparecía como remoto.
        await using RoomService room = new(new CollabEnvironment("red-de-pruebas", Path.Combine(CollabHarness.TempFolder(), "id.bin"), "1.0.4"), new LateHandler());
        using CollabPresence presence = new(room);
        using UiLoop ui = new();
        Project live = ModelSamples.ValidProject();
        System.Collections.Concurrent.ConcurrentQueue<string> notices = new();
        using CollabEngine engine = new(live, room, presence, ui.Post, () => { }, (text, _) => notices.Enqueue(text));
        await ui.Post(() => engine.StartAsHost());

        var ensureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.EnsureReservations = async (_, _) =>
        {
            ensureStarted.TrySetResult();
            await release.Task;
            return null;
        };

        // Cambiar un medio que ya existe pide la reserva «proyecto»: el envío se queda esperándola.
        await ui.Post(() =>
        {
            live.Media[0].Name = "cambio mío";
            engine.OnLocalChange("Renombrar medio");
        });
        await ensureStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Mientras espera, entra ordenado un cambio ajeno sobre el mismo medio: lo vivo choca y vuelve a lo confirmado,
        // lo que descarta el pendiente.
        Project other = ModelSamples.ValidProject();
        other.Media[0].Name = "del otro";
        Patch remote = PatchDiff.Create(
            SharedState.Capture(ModelSamples.ValidProject()),
            SharedState.Capture(other),
            new ElementVersions(),
            new PatchHeader("remoto", 0, "otra-clave", "Renombrar medio"))!;
        engine.Apply(new Ordenado(0, 1, Guid.NewGuid(), "otra-clave", JsonDocument.Parse(PatchWire.ToJson(remote)).RootElement.Clone()));
        await CollabHarness.Until(() => live.Media[0].Name == "del otro", "la copia vuelve a lo confirmado");

        // Al completarse la espera, el pendiente ya no está: no puede salir el cambio descartado.
        release.TrySetResult();
        await Task.Delay(500);
        Assert.Equal(0, engine.SendAttempts);
        Assert.DoesNotContain(notices, n => n.Contains("No se pudo mandar", StringComparison.Ordinal));
        Assert.Equal("del otro", live.Media[0].Name);
    }

    [Fact]
    public async Task El_anfitrion_rechaza_un_parche_con_el_mismo_id_que_ya_acepto()
    {
        // Revisión, hallazgo 7: no había registro de los Patch.Id aceptados; reenviar el mismo parche dentro de otra
        // operación volvía a pasar y restauraba su orden antiguo.
        await using RoomService room = new(new CollabEnvironment("red-de-pruebas", Path.Combine(CollabHarness.TempFolder(), "id.bin"), "1.0.4"), new LateHandler());
        using CollabPresence presence = new(room);
        using UiLoop ui = new();
        using CollabEngine engine = new(ModelSamples.ValidProject(), room, presence, ui.Post, () => { }, (_, _) => { });
        await ui.Post(() => engine.StartAsHost());

        Project withMedia = ModelSamples.ValidProject();
        withMedia.Media.Add(Medium("m9", "m9.mp4"));
        Miembro luis = new(new string('a', 64), "Luis", Rol.Editar);
        Patch patch = PatchDiff.Create(
            SharedState.Capture(ModelSamples.ValidProject()),
            SharedState.Capture(withMedia),
            new ElementVersions(),
            new PatchHeader(Guid.NewGuid().ToString("N"), 0, luis.Clave, "Añadir m9"))!;
        using JsonDocument doc = JsonDocument.Parse(PatchWire.ToJson(patch));

        Assert.True((await engine.ValidateAsync(luis, doc.RootElement, CancellationToken.None)).Aceptada);
        Veredicto again = await engine.ValidateAsync(luis, doc.RootElement, CancellationToken.None);

        Assert.False(again.Aceptada);
        Assert.Equal("duplicado", again.Motivo);
    }
}
