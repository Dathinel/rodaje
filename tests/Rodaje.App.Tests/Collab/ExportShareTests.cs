using System.Text.Json;
using Dathinel.Sala;
using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Operations;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;

namespace Rodaje.App.Tests.Collab;

public sealed class ExportShareTests
{
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static ExportSettings Settings() => ExportPresets.YouTube1080p.Settings;

    private static Task<ConfirmedExportProject> Confirmed(CancellationToken _) =>
        Task.FromResult(new ConfirmedExportProject(ModelSamples.ValidProject(), null));

    private static Task<string> AskAsync(RoomService guest, string name = "Prueba.mp4", ExportRange? range = null,
        OperationProgress? progress = null, CancellationToken cancellationToken = default) =>
        ExportShareGuest.RequestAsync(guest, "s1", name, Settings(), range, progress, cancellationToken);

    /// <summary>Un «exportar-pedir» armado a mano (o roto a mano): el anfitrión lo trata como hostil igualmente.</summary>
    private static JsonElement AskMessage(string id, string extra = "", string? settings = null, string? range = null,
        string nombre = "\"Prueba.mp4\"")
    {
        string ajustes = settings ?? JsonSerializer.Serialize(Settings(), Wire);
        string rango = range is null ? "" : $",\"rango\":{range}";
        return JsonDocument.Parse(
            $"{{\"tipo\":\"exportar-pedir\",\"id\":\"{id}\",\"nombre\":{nombre},\"secuencia\":\"s1\",\"ajustes\":{ajustes}{rango}{(extra.Length > 0 ? "," + extra : "")}}}")
            .RootElement.Clone();
    }

    /// <summary>Contesta el directo que llega del anfitrión con ese tipo e id; hay que suscribirse antes de mandar.</summary>
    private static Task<JsonElement> ReplyAsync(RoomService guest, string type, string id)
    {
        TaskCompletionSource<JsonElement> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string _, JsonElement message)
        {
            if (message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("tipo", out JsonElement t) && t.GetString() == type
                && message.TryGetProperty("id", out JsonElement i) && i.GetString() == id)
            {
                reply.TrySetResult(message.Clone());
            }
        }

        guest.DirectReceived += Handler;
        return reply.Task.ContinueWith(t => { guest.DirectReceived -= Handler; return t.Result; });
    }

    private static string ErrorText(JsonElement reply) => reply.GetProperty("texto").GetString() ?? "";
    private static string ErrorMotivo(JsonElement reply) => reply.GetProperty("motivo").GetString() ?? "";

    private static async Task<RoomService> JoinGuestAsync(RoomService host, string alias)
    {
        CollabInvitation invite = await host.CreateInviteAsync(TimeSpan.FromMinutes(10));
        RoomService guest = new(CollabHarness.Env(), new RecordingHandler());
        Task joining = guest.JoinAsync(invite.Link, alias, CollabHarness.TempFolder());
        await CollabHarness.Until(() => host.State.Requests.Any(r => r.Alias == alias), "la solicitud de " + alias);
        await host.ApproveAsync(host.State.Requests.First(r => r.Alias == alias).Key, Rol.Editar);
        await joining.WaitAsync(TimeSpan.FromSeconds(20));
        return guest;
    }

    // ---------- sin red: modelo ----------

    [Fact]
    public void Un_medio_sin_original_en_este_equipo_cuenta_como_que_falta()
    {
        var project = ModelSamples.ValidProject();
        Assert.False(MediaPlaceholders.MissingOriginals(project));

        project.Media[0].Path = "";
        Assert.True(MediaPlaceholders.MissingOriginals(project));

        project.Media[0].Path = MediaPlaceholders.PlaceholderFor(project.Media[0], 0);
        Assert.True(MediaPlaceholders.MissingOriginals(project));

        using var dir = new TempDirectory();
        string real = dir.File("toma 1.mp4");
        File.WriteAllText(real, "x");
        project.Media[0].Path = real;
        project.Media[1].Path = Path.Combine(dir.Path, "no-existe.wav");
        Assert.True(MediaPlaceholders.MissingOriginals(project));

        project.Media[1].Path = real;
        Assert.False(MediaPlaceholders.MissingOriginals(project));
    }

    [Fact]
    public void El_invitado_sin_originales_no_exporta_aqui_pero_si_en_el_anfitrion()
    {
        var project = ModelSamples.ValidProject();
        var model = new ExportDialogModel(new ExportPreferences(), project.Sequences[0], [], SimulatedEncoders.All,
            project.Name, null) { RoomGuest = true, CanAskHostExport = true, MissingOriginals = true };

        Assert.False(model.CanExport);
        Assert.Contains(model.Problems, p => p.Contains("Exportar en el anfitrión"));
        Assert.True(model.CanExportOnHost);
    }

    [Fact]
    public void El_invitado_con_originales_puede_exportar_de_las_dos_formas_y_sin_sala_solo_aqui()
    {
        using var dir = new TempDirectory();
        var project = ModelSamples.ValidProject();
        var model = new ExportDialogModel(new ExportPreferences(), project.Sequences[0], [], SimulatedEncoders.All,
            project.Name, dir.Path) { RoomGuest = true, CanAskHostExport = true };
        Assert.True(model.CanExport);
        Assert.True(model.CanExportOnHost);

        var alone = new ExportDialogModel(new ExportPreferences(), project.Sequences[0], [], SimulatedEncoders.All,
            project.Name, dir.Path);
        Assert.False(alone.CanExportOnHost);
    }

    // ---------- red real ----------

    [SalaFact]
    public async Task El_invitado_pide_exportar_y_sale_en_el_equipo_del_anfitrion_tras_aceptar()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        SimulatedExportRunner runner = new() { FramesPerSecond = 480, Step = TimeSpan.FromMilliseconds(30) };
        using ExportJobList jobs = new(runner, null);
        List<HostExportApproval> approvals = [];
        List<string> phases = [];
        guest.DirectReceived += (_, m) =>
        {
            if (m.TryGetProperty("tipo", out JsonElement t) && t.GetString() == "exportar-progreso"
                && m.TryGetProperty("fase", out JsonElement f))
            {
                lock (phases)
                {
                    phases.Add(f.GetString() ?? "");
                }
            }
        };
        using ExportShareHost share = new(host, jobs, Confirmed, host.RoomFolder)
        {
            Approve = ask => { approvals.Add(ask); return Task.FromResult(true); },
        };

        var progress = new OperationProgress();
        string file = await AskAsync(guest, "Cumple.mp4", new ExportRange(Time.Zero, Time.FromSeconds(10)), progress)
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("Cumple.mp4", file);
        var approval = Assert.Single(approvals);
        Assert.Equal("Luis", approval.Alias);
        Assert.Contains("Cumple.mp4", approval.Detail);
        Assert.Contains("MP4", approval.Detail);
        (ExportRequest request, bool _) = Assert.Single(runner.Started);
        Assert.Equal("s1", request.SequenceId);
        Assert.StartsWith(Path.Combine(host.RoomFolder, "exportaciones"), request.OutputPath);
        Assert.Equal("Cumple.mp4", Path.GetFileName(request.OutputPath));
        Assert.Equal(new ExportRange(Time.Zero, Time.FromSeconds(10)), request.Range);
        Assert.Contains("toma 1.mp4", request.Project.Media.Select(m => m.Path));
        lock (phases)
        {
            Assert.NotEmpty(phases);
        }

        Assert.Equal(1.0, progress.Snapshot().Fraction);
        // Aceptada la exportación, la fase ya no es «pidiendo permiso».
        Assert.Equal("Exportando en el equipo de quien hospeda…", progress.Snapshot().PhaseText);
    }

    [SalaFact]
    public async Task El_anfitrion_puede_rechazar_el_pedido_a_mano()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        SimulatedExportRunner runner = new();
        using ExportJobList jobs = new(runner, null);
        List<HostExportApproval> approvals = [];
        using ExportShareHost share = new(host, jobs, Confirmed, host.RoomFolder)
        {
            Approve = ask => { approvals.Add(ask); return Task.FromResult(false); },
        };

        CollabException error = await Assert.ThrowsAsync<CollabException>(() => AskAsync(guest));

        Assert.Single(approvals);
        Assert.Contains("no aceptó", error.Message);
        Assert.Empty(runner.Started);
    }

    [SalaFact]
    public async Task Quien_solo_mira_no_puede_pedir_exportaciones()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync(Rol.Mirar);
        await using RoomService h = host;
        await using RoomService g = guest;
        SimulatedExportRunner runner = new();
        using ExportJobList jobs = new(runner, null);
        using ExportShareHost share = new(host, jobs, Confirmed, host.RoomFolder)
        {
            Approve = _ => Task.FromResult(true),
        };

        CollabException error = await Assert.ThrowsAsync<CollabException>(() => AskAsync(guest));

        Assert.Contains("mirar", error.Message);
        Assert.Empty(runner.Started);
    }

    [SalaFact]
    public async Task Un_miembro_no_puede_tener_dos_pedidos_a_la_vez()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        SimulatedExportRunner runner = new();
        using ExportJobList jobs = new(runner, null);
        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int approvals = 0;
        using ExportShareHost share = new(host, jobs, Confirmed, host.RoomFolder)
        {
            Approve = _ => { Interlocked.Increment(ref approvals); return gate.Task; },
        };

        Task<string> first = AskAsync(guest, "Uno.mp4");
        await CollabHarness.Until(() => approvals == 1, "la aprobación del primer pedido");
        CollabException error = await Assert.ThrowsAsync<CollabException>(() => AskAsync(guest, "Dos.mp4"));
        Assert.Contains("Ya tienes", error.Message);
        gate.SetResult(true);
        Assert.Equal("Uno.mp4", await first.WaitAsync(TimeSpan.FromSeconds(60)));
    }

    [SalaFact]
    public async Task La_cola_de_pedidos_del_anfitrion_esta_acotada()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        await using RoomService otro = await JoinGuestAsync(host, "Mara");
        SimulatedExportRunner runner = new();
        using ExportJobList jobs = new(runner, null);
        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int approvals = 0;
        using ExportShareHost share = new(host, jobs, Confirmed, host.RoomFolder)
        {
            MaxActive = 1,
            Approve = _ => { Interlocked.Increment(ref approvals); return gate.Task; },
        };

        Task<string> first = AskAsync(guest, "Uno.mp4");
        await CollabHarness.Until(() => approvals == 1, "la aprobación del primer pedido");
        CollabException error = await Assert.ThrowsAsync<CollabException>(() => AskAsync(otro, "Otra.mp4"));
        Assert.Contains("esperando su turno", error.Message);
        gate.SetResult(true);
        Assert.Equal("Uno.mp4", await first.WaitAsync(TimeSpan.FromSeconds(60)));
    }

    [SalaFact]
    public async Task El_invitado_cancela_la_exportacion_que_pidio()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        SimulatedExportRunner runner = new() { FramesPerSecond = 2, Step = TimeSpan.FromMilliseconds(400) };
        using ExportJobList jobs = new(runner, null);
        using ExportShareHost share = new(host, jobs, Confirmed, host.RoomFolder)
        {
            Approve = _ => Task.FromResult(true),
        };

        var progress = new OperationProgress();
        using CancellationTokenSource cancel = new();
        Task<string> asking = AskAsync(guest, "Lenta.mp4", new ExportRange(Time.Zero, Time.FromSeconds(4)), progress, cancel.Token);
        await CollabHarness.Until(() => runner.Started.Count == 1, "el trabajo en la cola del anfitrión");
        await CollabHarness.Until(() => progress.Snapshot().Fraction is > 0, "el primer avance");
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        await CollabHarness.Until(() => jobs.Entries.Count == 0 || jobs.Entries[0].IsFinished, "que termine el trabajo");
        Assert.True(jobs.Entries.Count == 0 || jobs.Entries[0].Progress.State == ExportJobState.Canceled);
    }

    [SalaFact]
    public async Task El_anfitrion_cancela_la_exportacion_desde_su_cola()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        SimulatedExportRunner runner = new() { FramesPerSecond = 2, Step = TimeSpan.FromMilliseconds(400) };
        using ExportJobList jobs = new(runner, null);
        using ExportShareHost share = new(host, jobs, Confirmed, host.RoomFolder)
        {
            Approve = _ => Task.FromResult(true),
        };

        Task<string> asking = AskAsync(guest, "Lenta.mp4", new ExportRange(Time.Zero, Time.FromSeconds(4)));
        await CollabHarness.Until(() => runner.Started.Count == 1, "el trabajo en la cola del anfitrión");
        jobs.Cancel(jobs.Entries[0]);

        CollabException error = await Assert.ThrowsAsync<CollabException>(() => asking);
        Assert.Contains("canceló", error.Message);
    }

    [SalaFact]
    public async Task Un_pedido_con_datos_que_no_se_esperan_se_rechaza()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        using ExportJobList jobs = new(new SimulatedExportRunner(), null);
        using ExportShareHost share = new(host, jobs, Confirmed, host.RoomFolder)
        {
            Approve = _ => Task.FromResult(true),
        };

        string id = Guid.NewGuid().ToString("N");
        Task<JsonElement> reply = ReplyAsync(guest, "exportar-error", id);
        await guest.SendDirectAsync(AskMessage(id, "\"carpeta\":\"C:\\\\malicioso\""));
        JsonElement error = await reply.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("invalida", ErrorMotivo(error));
        Assert.Contains("no espera", ErrorText(error));
    }

    [SalaFact]
    public async Task Un_pedido_demasiado_grande_se_rechaza()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        using ExportJobList jobs = new(new SimulatedExportRunner(), null);
        using ExportShareHost share = new(host, jobs, Confirmed, host.RoomFolder)
        {
            Approve = _ => Task.FromResult(true),
        };

        string id = Guid.NewGuid().ToString("N");
        Task<JsonElement> reply = ReplyAsync(guest, "exportar-error", id);
        await guest.SendDirectAsync(AskMessage(id, nombre: "\"" + new string('a', 300_000) + "\""));
        JsonElement error = await reply.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("invalida", ErrorMotivo(error));
        Assert.Contains("grande", ErrorText(error));
    }

    [SalaFact]
    public async Task Un_pedido_con_rango_imposible_o_ajustes_rotos_se_rechaza()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        using ExportJobList jobs = new(new SimulatedExportRunner(), null);
        using ExportShareHost share = new(host, jobs, Confirmed, host.RoomFolder)
        {
            Approve = _ => Task.FromResult(true),
        };

        string id = Guid.NewGuid().ToString("N");
        Task<JsonElement> reply = ReplyAsync(guest, "exportar-error", id);
        await guest.SendDirectAsync(AskMessage(id, range: "{\"inicio\":1000,\"fin\":500}"));
        JsonElement error = await reply.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("invalida", ErrorMotivo(error));
        Assert.Contains("rango", ErrorText(error));

        string id2 = Guid.NewGuid().ToString("N");
        Task<JsonElement> reply2 = ReplyAsync(guest, "exportar-error", id2);
        await guest.SendDirectAsync(AskMessage(id2, settings: "\"no son ajustes\""));
        JsonElement error2 = await reply2.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("invalida", ErrorMotivo(error2));
    }

    [SalaFact]
    public async Task El_nombre_sugerido_se_limpia_de_carpetas_y_el_destino_lo_elige_el_anfitrion()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        SimulatedExportRunner runner = new() { FramesPerSecond = 480, Step = TimeSpan.FromMilliseconds(30) };
        using ExportJobList jobs = new(runner, null);
        using ExportShareHost share = new(host, jobs, Confirmed, host.RoomFolder)
        {
            Approve = _ => Task.FromResult(true),
        };

        string file = await AskAsync(guest, "..\\..\\Documents\\secreto.mp4", progress: null)
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("secreto.mp4", file);
        (ExportRequest request, bool _) = Assert.Single(runner.Started);
        Assert.Equal(Path.Combine(host.RoomFolder, "exportaciones"), Path.GetDirectoryName(request.OutputPath));
        Assert.Equal("secreto.mp4", Path.GetFileName(request.OutputPath));
    }

    [SalaFact]
    public async Task El_pedido_no_lleva_ninguna_ruta_del_invitado()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        List<string> asks = [];
        host.DirectReceived += (_, m) =>
        {
            if (m.TryGetProperty("tipo", out JsonElement t) && t.GetString() == "exportar-pedir")
            {
                asks.Add(m.GetRawText());
            }
        };
        SimulatedExportRunner runner = new() { FramesPerSecond = 480, Step = TimeSpan.FromMilliseconds(30) };
        using ExportJobList jobs = new(runner, null);
        using ExportShareHost share = new(host, jobs, Confirmed, host.RoomFolder)
        {
            Approve = _ => Task.FromResult(true),
        };

        await AskAsync(guest, "D:\\Videos\\mi video.mp4").WaitAsync(TimeSpan.FromSeconds(60));

        string raw = Assert.Single(asks);
        Assert.DoesNotContain("D:\\\\", raw);
        Assert.DoesNotContain("outputPath", raw);
        Assert.DoesNotContain("carpeta", raw);
    }
}
