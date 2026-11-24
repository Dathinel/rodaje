using System.Collections.Concurrent;
using System.Text.Json;
using Dathinel.Sala;
using Rodaje.App.Services.Collab;

namespace Rodaje.App.Tests.Collab;

/// <summary>
/// Prueba que necesita <c>dathinel-red</c> compilado (<c>SALA_RED</c> o <c>externo/sala/red/target/debug</c>). La CI de Windows
/// lo compila antes; sin él se informa como omitida, con la causa.
/// </summary>
internal sealed class SalaFactAttribute : FactAttribute
{
    public SalaFactAttribute()
    {
        if (CollabHarness.RedPath is null)
        {
            Skip = "Falta dathinel-red: compila externo/sala/red con cargo build o define SALA_RED.";
        }
    }
}

internal static class CollabHarness
{
    public static string? RedPath { get; } = Find();

    private static string? Find()
    {
        if (Environment.GetEnvironmentVariable("SALA_RED") is { Length: > 0 } env && File.Exists(env))
        {
            return env;
        }

        string exe = OperatingSystem.IsWindows() ? "dathinel-red.exe" : "dathinel-red";
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Rodaje.slnx")))
            {
                foreach (string profile in new[] { "debug", "release" })
                {
                    string candidate = Path.Combine(d.FullName, "externo", "sala", "red", "target", profile, exe);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Abre la sala de Ana y mete a Luis: el anfitrión lo aprueba con el rol dado.</summary>
    public static async Task<(RoomService Host, RoomService Guest, RecordingHandler HostH, RecordingHandler GuestH)> RoomAsync(Rol role = Rol.Editar)
    {
        RecordingHandler hh = new(), gh = new();
        RoomService host = new(Env(), hh);
        RoomService guest = new(Env(), gh);
        await host.HostAsync("Ana", TempFolder());
        CollabInvitation invite = await host.CreateInviteAsync(TimeSpan.FromMinutes(10));
        Task joining = guest.JoinAsync(invite.Link, "Luis", TempFolder());
        await Until(() => host.State.Requests.Count == 1, "la solicitud de Luis");
        await host.ApproveAsync(host.State.Requests[0].Key, role);
        await joining.WaitAsync(TimeSpan.FromSeconds(20));
        return (host, guest, hh, gh);
    }

    public static string TempFolder()
    {
        string c = Path.Combine(Path.GetTempPath(), "rodaje-sala-pruebas", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(c);
        return c;
    }

    public static CollabEnvironment Env(string version = "1.0.4") =>
        new(RedPath!, Path.Combine(TempFolder(), "identidad.bin"), version) { Red = Red.SinRele, ValidateTimeout = TimeSpan.FromSeconds(5) };

    public static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > limit)
            {
                Assert.Fail("No pasó a tiempo: " + what);
            }

            await Task.Delay(20);
        }
    }
}

/// <summary>Un manejador que acepta todo y apunta lo que le llega.</summary>
internal sealed class RecordingHandler : ICollabPayloadHandler
{
    public ConcurrentQueue<long> Applied { get; } = new();

    public ConcurrentQueue<string> RejectedReasons { get; } = new();

    public ConcurrentQueue<string> Validated { get; } = new();

    public Func<JsonElement, Veredicto> Decide { get; set; } = _ => Veredicto.Si;

    public ValueTask<Veredicto> ValidateAsync(Miembro author, JsonElement payload, CancellationToken cancellationToken)
    {
        Validated.Enqueue(payload.GetRawText());
        return ValueTask.FromResult(Decide(payload));
    }

    public void Apply(Ordenado ordered) => Applied.Enqueue(ordered.Seq);

    public void Rejected(Rechazo rejection) => RejectedReasons.Enqueue(rejection.Motivo);
}

public sealed class RoomServiceTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [SalaFact]
    public async Task El_anfitrion_abre_la_sala_y_el_invitado_entra_cuando_lo_aprueban()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        Assert.Equal(RoomPhase.Hosting, host.State.Phase);
        Assert.Equal(RoomPhase.Connected, guest.State.Phase);
        Assert.True(host.State.IsHost);
        Assert.False(guest.State.IsHost);
        await CollabHarness.Until(() => host.State.Members.Count == 2 && guest.State.Members.Count == 2, "dos miembros en los dos lados");
        Assert.Equal(["Ana", "Luis"], host.State.Members.Select(m => m.Alias));
        Assert.True(host.State.Members[0].IsHost && host.State.Members[0].IsMe);
        Assert.True(guest.State.Members.Single(m => m.Alias == "Luis").IsMe);
        Assert.Empty(host.State.Requests);
        Assert.Equal(4, guest.State.Members[0].Phrase.Split('-').Length);
        Assert.Equal(host.State.Members[0].ColorIndex, guest.State.Members.Single(m => m.IsHost).ColorIndex);
    }

    [SalaFact]
    public async Task La_solicitud_trae_el_nombre_y_las_cuatro_palabras_y_rechazar_deja_fuera()
    {
        await using RoomService host = new(CollabHarness.Env());
        await using RoomService guest = new(CollabHarness.Env());
        await host.HostAsync("Ana", CollabHarness.TempFolder());
        CollabInvitation invite = await host.CreateInviteAsync(TimeSpan.FromMinutes(10));
        Task joining = guest.JoinAsync(invite.Link, "  Luis  ", CollabHarness.TempFolder());
        await CollabHarness.Until(() => host.State.Requests.Count == 1, "la solicitud");
        RoomRequest request = host.State.Requests[0];
        Assert.Equal("Luis", request.Alias);
        Assert.Equal(4, request.Phrase.Split('-').Length);

        await host.RejectAsync(request.Key);
        CollabException ex = await Assert.ThrowsAsync<CollabException>(() => joining);
        Assert.Contains("no te dejó entrar", ex.Message, StringComparison.Ordinal);
        Assert.Equal(RoomPhase.Closed, guest.State.Phase);
        Assert.Empty(host.State.Requests);
    }

    [SalaFact]
    public async Task Una_invitacion_de_un_solo_uso_no_sirve_dos_veces()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        await guest.LeaveAsync();
        CollabInvitation again = await host.CreateInviteAsync(TimeSpan.FromMinutes(5), oneUse: true);
        await using RoomService second = new(CollabHarness.Env());
        Task join1 = second.JoinAsync(again.Link, "Eva", CollabHarness.TempFolder());
        await CollabHarness.Until(() => host.State.Requests.Count == 1, "la solicitud de Eva");
        await host.ApproveAsync(host.State.Requests[0].Key, Rol.Editar);
        await join1.WaitAsync(TimeSpan.FromSeconds(20));

        await using RoomService third = new(CollabHarness.Env());
        CollabException ex = await Assert.ThrowsAsync<CollabException>(() => third.JoinAsync(again.Link, "Tomás", CollabHarness.TempFolder()));
        Assert.Contains("invitación", ex.Message, StringComparison.Ordinal);
        Assert.Equal(RoomPhase.Closed, third.State.Phase);
    }

    [SalaFact]
    public async Task Una_version_distinta_no_entra_y_el_texto_lo_dice()
    {
        await using RoomService host = new(CollabHarness.Env("1.0.4"));
        await using RoomService guest = new(CollabHarness.Env("1.0.5"));
        await host.HostAsync("Ana", CollabHarness.TempFolder());
        CollabInvitation invite = await host.CreateInviteAsync(TimeSpan.FromMinutes(10));
        CollabException ex = await Assert.ThrowsAsync<CollabException>(() => guest.JoinAsync(invite.Link, "Luis", CollabHarness.TempFolder()));
        Assert.Contains("1.0.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(RoomPhase.Closed, guest.State.Phase);
        Assert.Empty(host.State.Requests);
    }

    [SalaFact]
    public async Task Las_cargas_pasan_por_el_manejador_y_llegan_a_todos_en_orden()
    {
        (RoomService host, RoomService guest, RecordingHandler hh, RecordingHandler gh) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        for (int i = 0; i < 5; i++)
        {
            await guest.SendAsync(Json($$"""{"n":{{i}}}"""));
        }

        await CollabHarness.Until(() => hh.Applied.Count == 5 && gh.Applied.Count == 5, "las cinco cargas en los dos lados");
        Assert.Equal([1L, 2, 3, 4, 5], hh.Applied);
        Assert.Equal([1L, 2, 3, 4, 5], gh.Applied);
        Assert.Equal(5, hh.Validated.Count);
        Assert.Empty(gh.Validated);
    }

    [SalaFact]
    public async Task Si_el_anfitrion_no_acepta_una_carga_el_invitado_lo_sabe_con_un_texto_claro()
    {
        (RoomService host, RoomService guest, RecordingHandler hh, RecordingHandler gh) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        hh.Decide = _ => Veredicto.No("no-valido", "El cambio no es válido.");
        ConcurrentQueue<RoomNotice> notices = new();
        guest.Noticed += notices.Enqueue;
        await guest.SendAsync(Json("""{"n":1}"""));
        await CollabHarness.Until(() => !gh.RejectedReasons.IsEmpty, "el rechazo");
        Assert.Equal("no-valido", gh.RejectedReasons.Single());
        await CollabHarness.Until(() => notices.Any(n => n.IsProblem), "el aviso");
        Assert.Empty(gh.Applied);
    }

    [SalaFact]
    public async Task Quien_solo_mira_no_puede_mandar_y_el_texto_dice_que_hacer()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync(Rol.Mirar);
        await using RoomService h = host;
        await using RoomService g = guest;
        Assert.Equal(Rol.Mirar, guest.State.MyRole);
        Assert.False(guest.State.CanEdit);
        CollabException ex = await Assert.ThrowsAsync<CollabException>(() => guest.SendAsync(Json("""{"n":1}""")));
        Assert.Contains("permiso", ex.Message, StringComparison.Ordinal);
    }

    [SalaFact]
    public async Task Cambiar_el_rol_y_expulsar_llegan_al_invitado()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        string luis = host.State.Members.Single(m => !m.IsMe).Key;

        await host.SetRoleAsync(luis, Rol.Mirar);
        await CollabHarness.Until(() => guest.State.MyRole == Rol.Mirar, "el rol de solo mirar");
        Assert.Equal(Rol.Mirar, host.State.Members.Single(m => m.Key == luis).Role);

        await host.KickAsync(luis);
        await CollabHarness.Until(() => guest.State.Phase == RoomPhase.Ended, "la expulsión");
        Assert.Contains("sacaron", guest.State.Problem, StringComparison.Ordinal);
        await CollabHarness.Until(() => host.State.Members.Count == 1, "el anfitrión solo");
    }

    [SalaFact]
    public async Task Tras_pasar_el_anfitrion_las_dos_vistas_coinciden_en_quien_esta_y_quien_es_suplente()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService ana = host;
        await using RoomService luis = guest;
        string anaKey = ana.State.MyKey, luisKey = luis.State.MyKey;

        await ana.HandOverAsync(luisKey);

        static bool Coherent(RoomState s, string hostKey, string standbyKey) =>
            s.Members.Count == 2 && s.Members.All(m => m.Online) && s.OnlineCount == 2
            && s.Members.Single(m => m.IsHost).Key == hostKey && s.StandbyKey == standbyKey;
        await CollabHarness.Until(() => luis.State.IsHost && ana.State.Phase == RoomPhase.Connected, "el traspaso hecho");
        await CollabHarness.Until(() => Coherent(ana.State, luisKey, anaKey) && Coherent(luis.State, luisKey, anaKey),
            "las dos vistas con Luis hospedando, las dos personas conectadas y Ana de suplente");
    }

    [SalaFact]
    public async Task Quien_vuelve_tras_cerrar_el_anfitrion_la_sala_no_necesita_aprobacion_de_nuevo()
    {
        RecordingHandler hh = new();
        RoomService host = new(CollabHarness.Env(), hh);
        string folder = CollabHarness.TempFolder();
        await host.HostAsync("Ana", folder);
        CollabInvitation invite = await host.CreateInviteAsync(TimeSpan.FromMinutes(10), oneUse: true);
        await using RoomService guest = new(CollabHarness.Env());
        Task joining = guest.JoinAsync(invite.Link, "Luis", CollabHarness.TempFolder());
        await CollabHarness.Until(() => host.State.Requests.Count == 1, "la solicitud");
        await host.ApproveAsync(host.State.Requests[0].Key, Rol.Mirar);
        await joining.WaitAsync(TimeSpan.FromSeconds(20));
        await CollabHarness.Until(() => File.Exists(Path.Combine(folder, AnchoredStore.FileName)), "el archivo de anclados");
        IReadOnlyList<Miembro> saved = AnchoredStore.Load(folder);
        Assert.Equal("Luis", Assert.Single(saved).Alias);
        Assert.Equal(Rol.Mirar, saved[0].Rol);

        await host.LeaveAsync();
        await host.DisposeAsync();
        await CollabHarness.Until(() => guest.State.Phase == RoomPhase.Ended, "el aviso de que el anfitrión se fue");
    }

    [SalaFact]
    public async Task Si_el_anfitrion_cierra_la_sala_el_invitado_termina_y_no_reintenta()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        await CollabHarness.Until(() => guest.State.Members.Any(m => m is { IsHost: true, Alias: "Ana" }), "el nombre del anfitrión en el invitado");
        string folder = guest.RoomFolder;

        await host.LeaveAsync();

        // Sin el directo «sala-cerrada» el invitado se quedaba reintentando la conexión para siempre.
        await CollabHarness.Until(() => guest.State.Phase == RoomPhase.Ended, "la sala terminada en el invitado");
        Assert.Contains("Ana cerró la sala", guest.State.Problem, StringComparison.Ordinal);
        Assert.Contains(folder, guest.State.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void El_diagnostico_usa_el_alias_del_miembro_cuando_el_par_no_lo_trae()
    {
        // Al invitado el proceso de red le da el par sin alias: sin el relleno, «Versión: de …» enseñaba sus palabras.
        string clave = new('a', 64);
        DiagnosticoPar par = new(clave, "", Camino.Directo, null, TimeSpan.FromMilliseconds(12), 0.5, "1.0.4", 3, null, DateTimeOffset.UtcNow, 0, 0, 0);
        Diagnostico d = new(new('b', 64), false, "1.0.4", new DiagnosticoRed("ninguno", null, false, true, true), [par], null, 0, null, null);

        Diagnostico filled = RoomService.FillMemberAliases(d, [new Miembro(clave, "Ana", Rol.Editar)]);

        Assert.Equal("Ana", filled.Pares[0].Alias);
        Assert.Contains("de Ana 1.0.4", filled.ParaTelefono(), StringComparison.Ordinal);
        Assert.DoesNotContain(par.Huella, filled.ParaTelefono(), StringComparison.Ordinal);
        Assert.Same(d, RoomService.FillMemberAliases(d, []));
    }

    [Fact]
    public void El_texto_del_cierre_avisado_lleva_el_nombre_y_la_carpeta()
    {
        Assert.Equal("Ana cerró la sala. Tu copia del proyecto queda guardada en D:\\Cosas\\sala.",
            CollabMessages.ForHostClosed("Ana", "D:\\Cosas\\sala"));
        Assert.Equal("Quien hospedaba la sala la cerró. Tu copia del proyecto sigue en este equipo.",
            CollabMessages.ForHostClosed(null, ""));
    }

    [SalaFact]
    public async Task Los_fallos_no_dejan_la_sala_a_medias_y_se_puede_empezar_otra()
    {
        await using RoomService svc = new(CollabHarness.Env());
        CollabException none = await Assert.ThrowsAsync<CollabException>(() => svc.JoinAsync("", "Luis", CollabHarness.TempFolder()));
        Assert.Contains("invitación", none.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<CollabException>(() => svc.HostAsync("   ", CollabHarness.TempFolder()));
        await Assert.ThrowsAsync<CollabException>(() => svc.CreateInviteAsync(TimeSpan.FromMinutes(5)));
        await svc.HostAsync("Ana", CollabHarness.TempFolder());
        CollabException twice = await Assert.ThrowsAsync<CollabException>(() => svc.HostAsync("Ana", CollabHarness.TempFolder()));
        Assert.Contains("Ya estás en una sala", twice.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<CollabException>(() => svc.CreateInviteAsync(TimeSpan.FromSeconds(5)));
        await svc.LeaveAsync();
        Assert.Equal(RoomPhase.Closed, svc.State.Phase);
        await svc.HostAsync("Ana", CollabHarness.TempFolder());
        Assert.Equal(RoomPhase.Hosting, svc.State.Phase);
    }

    [Fact]
    public async Task Sin_el_componente_de_red_dice_que_falta_en_vez_de_lanzar_otra_cosa()
    {
        CollabEnvironment env = new(Path.Combine(CollabHarness.TempFolder(), "no-existe.exe"), Path.Combine(CollabHarness.TempFolder(), "id.bin"), "1.0.4");
        await using RoomService svc = new(env);
        CollabException ex = await Assert.ThrowsAsync<CollabException>(() => svc.HostAsync("Ana", CollabHarness.TempFolder()));
        Assert.Contains("componente", ex.Message, StringComparison.Ordinal);
        Assert.Equal(RoomPhase.Closed, svc.State.Phase);
    }
}
