using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Dathinel.Sala;
using Rodaje.App.Services.Collab;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Collab;

/// <summary>Un hilo que ejecuta acciones de a una y en orden, como el hilo de la interfaz.</summary>
internal sealed class UiLoop : IDisposable
{
    private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>();
    private readonly Task _loop;

    public UiLoop() => _loop = Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning);

    public Task Post(Action action)
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite(() =>
        {
            try
            {
                action();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _loop.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        foreach (Action a in _queue.Reader.ReadAllAsync().ToBlockingEnumerable())
        {
            a();
        }
    }
}

/// <summary>El manejador que se rellena después de crear la sala (el motor necesita la sala y la sala necesita el manejador).</summary>
internal sealed class LateHandler : ICollabPayloadHandler
{
    public ICollabPayloadHandler? Target { get; set; }

    public ValueTask<Veredicto> ValidateAsync(Miembro author, JsonElement payload, CancellationToken cancellationToken) =>
        Target?.ValidateAsync(author, payload, cancellationToken) ?? ValueTask.FromResult(Veredicto.No("sin-motor"));

    public void Apply(Ordenado ordered) => Target?.Apply(ordered);

    public void Rejected(Rechazo rejection) => Target?.Rejected(rejection);
}

/// <summary>Un equipo de una sala: su sala, su presencia, su proyecto «vivo», su hilo de interfaz y su motor.</summary>
internal sealed class Replica : IAsyncDisposable
{
    public RoomService Room { get; }

    public CollabPresence Presence { get; }

    public UiLoop Ui { get; } = new();

    public Project Live { get; private set; } = new();

    public CollabEngine Engine { get; private set; } = null!;

    public ConcurrentQueue<string> Notices { get; } = new();

    public ConcurrentQueue<string> Desyncs { get; } = new();

    public int Refreshes;

    private readonly LateHandler _late = new();

    public Replica(string version = "1.0.4")
    {
        Room = new RoomService(CollabHarness.Env(version), _late);
        Presence = new CollabPresence(Room);
    }

    public void Attach(Project live)
    {
        Live = live;
        Engine = new CollabEngine(live, Room, Presence, Ui.Post, () => Interlocked.Increment(ref Refreshes), (text, problem) => Notices.Enqueue(text));
        Engine.Desynced += Desyncs.Enqueue;
        _late.Target = Engine;
    }

    /// <summary>Hace un cambio local en el hilo de la interfaz y lo deja salir como parche.</summary>
    public Task Edit(Action<Project> change, string description = "Cambio de prueba") =>
        Ui.Post(() =>
        {
            change(Live);
            Engine.OnLocalChange(description);
        });

    public string Hash() => SharedState.Capture(Live).Hash;

    public string HashOnUi()
    {
        string hash = "";
        Ui.Post(() => hash = SharedState.Capture(Live).Hash).Wait(TimeSpan.FromSeconds(10));
        return hash;
    }

    public async ValueTask DisposeAsync()
    {
        Engine?.Dispose();
        Presence.Dispose();
        await Room.DisposeAsync();
        Ui.Dispose();
    }
}

public sealed class CollabEngineTests
{
    private static Marker MarkerAt(string id, string name, double seconds = 1) => new() { Id = id, Name = name, Time = Time.FromSeconds(seconds) };

    /// <summary>Anfitrión con el proyecto de ejemplo y un invitado que lo recibió por el mismo camino que en la app.</summary>
    private static async Task<(Replica Host, Replica Guest)> StartAsync(Rol role = Rol.Editar)
    {
        Replica host = new(), guest = new();
        await host.Room.HostAsync("Ana", CollabHarness.TempFolder());
        host.Attach(ModelSamples.ValidProject());
        await host.Ui.Post(() => host.Engine.StartAsHost());

        CollabInvitation invite = await host.Room.CreateInviteAsync(TimeSpan.FromMinutes(10));
        Task joining = guest.Room.JoinAsync(invite.Link, "Luis", CollabHarness.TempFolder());
        await CollabHarness.Until(() => host.Room.State.Requests.Count == 1, "la solicitud de Luis");
        await host.Room.ApproveAsync(host.Room.State.Requests[0].Key, role);
        await joining.WaitAsync(TimeSpan.FromSeconds(20));

        (string json, long seq) = host.Engine.CaptureForGuest();
        ProjectParseResult parsed = ProjectWire.TryParse(json);
        Assert.True(parsed.IsValid, parsed.Rejection?.Message);
        guest.Attach(parsed.Project!);
        await guest.Ui.Post(() => guest.Engine.StartAsGuest(parsed.Project!.Clone(), parsed.Versions!, seq));
        await CollabHarness.Until(() => host.Room.State.Members.Count == 2 && guest.Room.State.Members.Count == 2, "dos miembros");
        return (host, guest);
    }

    private static async Task Settled(Replica a, Replica b, string what)
    {
        await CollabHarness.Until(() => a.HashOnUi() == b.HashOnUi(), what);
        Assert.Equal(a.HashOnUi(), b.HashOnUi());
    }

    [SalaFact]
    public async Task Un_cambio_del_anfitrion_llega_al_invitado_y_el_estado_compartido_es_igual()
    {
        (Replica host, Replica guest) = await StartAsync();
        await using Replica h = host;
        await using Replica g = guest;
        Assert.Equal(host.HashOnUi(), guest.HashOnUi());

        await host.Edit(p => p.Sequences[0].Markers.Add(MarkerAt("mk-host", "del anfitrión")));

        await CollabHarness.Until(() => guest.Live.Sequences[0].Markers.Any(m => m.Id == "mk-host"), "el marcador en el invitado");
        await Settled(host, guest, "mismo estado compartido");
        Assert.True(guest.Refreshes > 0, "la vista del invitado se rehízo");
    }

    [SalaFact]
    public async Task Un_cambio_del_invitado_llega_al_anfitrion_con_su_reserva_del_proyecto()
    {
        (Replica host, Replica guest) = await StartAsync();
        await using Replica h = host;
        await using Replica g = guest;

        await guest.Edit(p => p.Sequences[0].Markers.Add(MarkerAt("mk-guest", "del invitado")));

        await CollabHarness.Until(() => host.Live.Sequences[0].Markers.Any(m => m.Id == "mk-guest"), "el marcador en el anfitrión");
        await Settled(host, guest, "mismo estado compartido");
        Assert.Empty(guest.Notices);
    }

    [SalaFact]
    public async Task Cambios_de_los_dos_a_la_vez_convergen_y_lo_que_choca_se_deshace_con_un_aviso()
    {
        (Replica host, Replica guest) = await StartAsync();
        await using Replica h = host;
        await using Replica g = guest;

        // Los dos piden la reserva «proyecto» (marcadores): uno la tiene y el otro espera su turno o se deshace.
        Task a = host.Edit(p => p.Sequences[0].Markers.Add(MarkerAt("mk-a", "A", 2)));
        Task b = guest.Edit(p => p.Sequences[0].Markers.Add(MarkerAt("mk-b", "B", 3)));
        await Task.WhenAll(a, b);

        try
        {
            await CollabHarness.Until(() => host.HashOnUi() == guest.HashOnUi() && host.Live.Sequences[0].Markers.Count == guest.Live.Sequences[0].Markers.Count, "convergen", 30);
        }
        catch (Xunit.Sdk.XunitException ex)
        {
            // Si pasa en la CI, el informe trae lo que había en cada lado para encontrar la causa.
            string Dump(Replica r) => string.Join(",", r.Live.Sequences[0].Markers.Select(m => m.Id)) + " | avisos: " + string.Join(" / ", r.Notices) + " | desinc: " + string.Join(" / ", r.Desyncs);
            Assert.Fail($"{ex.Message}
anfitrión: {Dump(host)}
invitado: {Dump(guest)}");
        }

        int markers = host.Live.Sequences[0].Markers.Count(m => m.Id is "mk-a" or "mk-b");
        Assert.InRange(markers, 1, 2);
        if (markers == 1)
        {
            Assert.True(!host.Notices.IsEmpty || !guest.Notices.IsEmpty, "quien perdió recibió un aviso");
        }
    }

    [SalaFact]
    public async Task Quien_solo_mira_no_cambia_nada_y_se_le_dice()
    {
        (Replica host, Replica guest) = await StartAsync(Rol.Mirar);
        await using Replica h = host;
        await using Replica g = guest;
        string before = host.HashOnUi();

        await guest.Edit(p => p.Sequences[0].Markers.Add(MarkerAt("mk-no", "no debería entrar")));

        Assert.DoesNotContain(guest.Live.Sequences[0].Markers, m => m.Id == "mk-no");
        Assert.Contains(guest.Notices, n => n.Contains("Solo puedes mirar", StringComparison.Ordinal));
        await Task.Delay(300);
        Assert.Equal(before, host.HashOnUi());
    }

    [SalaFact]
    public async Task Deshacer_solo_deshace_lo_propio_y_llega_a_todos()
    {
        (Replica host, Replica guest) = await StartAsync();
        await using Replica h = host;
        await using Replica g = guest;
        await guest.Edit(p => p.Sequences[0].Markers.Add(MarkerAt("mk-undo", "para deshacer")), "Añadir marcador");
        await CollabHarness.Until(() => guest.Engine.CanUndo, "el paso propio confirmado");
        await host.Edit(p => p.Sequences[0].Markers.Add(MarkerAt("mk-otro", "del otro", 5)));
        await CollabHarness.Until(() => guest.Live.Sequences[0].Markers.Any(m => m.Id == "mk-otro"), "el cambio del otro en el invitado");

        bool undone = false;
        await guest.Ui.Post(() => undone = guest.Engine.Undo());

        Assert.True(undone);
        await CollabHarness.Until(() => host.Live.Sequences[0].Markers.All(m => m.Id != "mk-undo"), "el marcador deshecho en el anfitrión");
        Assert.Contains(host.Live.Sequences[0].Markers, m => m.Id == "mk-otro");
        Assert.Contains(guest.Live.Sequences[0].Markers, m => m.Id == "mk-otro");
        await Settled(host, guest, "mismo estado compartido");
        Assert.True(guest.Engine.CanRedo);
    }

    [SalaFact]
    public async Task No_se_puede_deshacer_lo_que_otra_persona_cambio_despues()
    {
        (Replica host, Replica guest) = await StartAsync();
        await using Replica h = host;
        await using Replica g = guest;
        await guest.Edit(p => p.Sequences[0].Markers.Add(MarkerAt("mk-tocado", "mío")), "Añadir marcador");
        await CollabHarness.Until(() => guest.Engine.CanUndo, "el paso propio confirmado");
        await CollabHarness.Until(() => host.Live.Sequences[0].Markers.Any(m => m.Id == "mk-tocado"), "el marcador en el anfitrión");
        await host.Edit(p => p.Sequences[0].Markers.First(m => m.Id == "mk-tocado").Name = "cambiado por Ana");
        await CollabHarness.Until(() => guest.Live.Sequences[0].Markers.First(m => m.Id == "mk-tocado").Name == "cambiado por Ana", "el cambio de Ana en el invitado");

        bool undone = true;
        await guest.Ui.Post(() => undone = guest.Engine.Undo());

        Assert.False(undone);
        Assert.Contains(guest.Notices, n => n.Contains("otra persona cambió ese elemento", StringComparison.Ordinal));
        await Settled(host, guest, "mismo estado compartido");
    }

    [SalaFact]
    public async Task El_anfitrion_rechaza_un_parche_hostil_sin_cambiar_nada()
    {
        (Replica host, Replica guest) = await StartAsync();
        await using Replica h = host;
        await using Replica g = guest;
        string before = host.HashOnUi();
        Miembro luis = new(guest.Room.State.MyKey, "Luis", Rol.Editar);

        foreach (string hostile in new[]
        {
            "\"no es un parche\"",
            "{}",
            "[]",
            "{\"id\":\"x\",\"base\":0,\"author\":\"k\",\"desc\":\"d\",\"ops\":[{\"kind\":\"media\",\"id\":\"m9\",\"parent\":null,\"v\":0,\"content\":{\"id\":\"m9\",\"path\":\"\\\\\\\\evil\\\\share\\\\x.mp4\",\"name\":\"x\"}}],\"orders\":[],\"blobs\":[]}",
        })
        {
            using JsonDocument doc = JsonDocument.Parse(hostile);
            Veredicto verdict = await host.Engine.ValidateAsync(luis, doc.RootElement, CancellationToken.None);
            Assert.False(verdict.Aceptada, hostile);
        }

        Assert.Equal(before, host.HashOnUi());
    }
}
