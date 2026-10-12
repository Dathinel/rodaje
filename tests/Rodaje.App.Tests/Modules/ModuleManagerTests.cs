using System.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Serialization;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Tests.Modules;

/// <summary>El ModuleManager con el módulo eco como proceso de verdad.</summary>
/// <remarks>
/// Corren solas (<see cref="ModuleProcessGroup"/>): miden el ping en décimas de segundo y arrancan procesos, y en la CI
/// de Windows, con las demás clases del proyecto en paralelo, el pool tardaba en atender la respuesta y un módulo sano se
/// daba por colgado, o el arranque pasaba de 10 s.
/// </remarks>
[Collection(ModuleProcessGroup.Name)]
public sealed class ModuleManagerTests : IDisposable
{
    private const string LongCount = """{"pasos":100000,"msPorPaso":10}""";
    private readonly EchoModules _modules = new EchoModules().Add("eco-a").Add("eco-b");

    public void Dispose() => _modules.Dispose();

    [Fact]
    public async Task Abre_al_primer_uso_reutiliza_el_proceso_y_lo_cierra_al_quedar_ocioso()
    {
        await using ModuleManager manager = _modules.Manager(new ModuleManagerOptions { IdleTimeout = TimeSpan.FromMilliseconds(300) });
        Assert.Empty(manager.ProcessIds);
        Assert.True(manager.KillsOnClose);

        ModuleResult first = await manager.InvokeAsync("eco-a", "eco", EchoModules.Json("""{"a":1}"""), "Eco");
        Assert.Equal("""{"a":1}""", first.Value!.Value.GetRawText());
        int pid = Assert.Single(manager.ProcessIds);
        await manager.InvokeAsync("eco-a", "eco", null, "Eco otra vez");
        Assert.Equal(pid, Assert.Single(manager.ProcessIds));

        // Se cierra solo tras 300 ms ocioso; con el runner cargado, salir le llevó más de los 3 s de HasExited.
        await WaitUntil(() => EchoModules.HasExited(pid));
        await WaitUntil(() => manager.ProcessIds.Count == 0);
        Assert.All(manager.Jobs.Jobs, j => Assert.Equal(ModuleJobState.Completed, j.State));
    }

    [Fact]
    public async Task El_avance_llega_a_la_fila_de_Trabajos_y_cancelar_la_fila_cancela_la_llamada()
    {
        await using ModuleManager manager = _modules.Manager();
        Task<ModuleResult> call = manager.InvokeAsync("eco-a", "contar", EchoModules.Json(LongCount), "Contar");
        ModuleJob job = Assert.Single(manager.Jobs.Jobs);
        await WaitUntil(() => job.Progress > 0);
        Assert.Equal(ModuleJobState.Running, job.State);
        Assert.Equal("contando", job.Stage);

        job.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(ModuleJobState.Cancelled, job.State);

        // El módulo confirmó la cancelación y sigue abierto.
        int pid = Assert.Single(manager.ProcessIds);
        await manager.InvokeAsync("eco-a", "eco", null, "Eco");
        Assert.Equal(pid, Assert.Single(manager.ProcessIds));
        Assert.True(manager.Jobs.ClearFinished());
        Assert.Empty(manager.Jobs.Jobs);
    }

    [Fact]
    public void Matar_el_modulo_a_mitad_falla_solo_su_trabajo_y_deja_el_proyecto_intacto() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        var proposals = new ModuleProposals(session);
        string before = ProjectSerializer.Serialize(session.Project);
        int steps = session.History.Count;
        await using ModuleManager manager = _modules.Manager();
        ModuleFailure? failure = null;
        manager.Failed += (_, f) => failure = f;

        Task<ModuleResult> call = manager.InvokeAsync("eco-a", "contar", EchoModules.Json(LongCount), "Contar");
        await WaitUntil(() => manager.ProcessIds.Count == 1 && manager.Jobs.Jobs[0].Progress > 0);
        int pid = manager.ProcessIds[0];
        Process.GetProcessById(pid).Kill();

        await Assert.ThrowsAnyAsync<ModuleExitedException>(() => call.WaitAsync(TimeSpan.FromSeconds(3)));
        ModuleJob job = Assert.Single(manager.Jobs.Jobs);
        Assert.Equal(ModuleJobState.Failed, job.State);
        Assert.Contains("El proyecto no cambió", job.Error, StringComparison.Ordinal);
        await WaitUntil(() => failure is not null);
        Assert.Null(proposals.Pending);
        Assert.Equal(steps, session.History.Count);
        Assert.Equal(before, ProjectSerializer.Serialize(session.Project));

        // La próxima llamada abre otro proceso.
        await manager.InvokeAsync("eco-a", "eco", null, "Eco");
        Assert.NotEqual(pid, Assert.Single(manager.ProcessIds));
    });

    [Fact]
    public async Task Un_modulo_que_muere_solo_falla_rapido_con_su_codigo()
    {
        await using ModuleManager manager = _modules.Manager();
        await manager.InvokeAsync("eco-a", "eco", null, "Abrir");
        Stopwatch watch = Stopwatch.StartNew();
        ModuleExitedException error = await Assert.ThrowsAsync<ModuleExitedException>(() => manager.InvokeAsync("eco-a", "morir", EchoModules.Json("""{"codigo":7}"""), "Morir"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(7, error.ExitCode);
        Assert.Contains("código 7", manager.Jobs.Jobs[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Un_modulo_colgado_que_no_contesta_el_ping_se_mata_y_se_avisa()
    {
        await using ModuleManager manager = _modules.Manager(new ModuleManagerOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(100),
            PingTimeout = TimeSpan.FromMilliseconds(400),
        });
        Task<ModuleResult> call = manager.InvokeAsync("eco-a", "contar", EchoModules.Json(LongCount), "Contar");
        await WaitUntil(() => manager.ProcessIds.Count == 1 && manager.Jobs.Jobs[0].Progress > 0);
        int pid = manager.ProcessIds[0];
        Stopwatch watch = Stopwatch.StartNew();
        EchoModules.Suspend(pid);

        // El tope de la espera es contra cuelgues; lo que se exige es la cota de abajo.
        ModuleHungException error = await Assert.ThrowsAsync<ModuleHungException>(() => call.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"{watch.ElapsedMilliseconds} ms");
        Assert.True(EchoModules.HasExited(pid));
        Assert.Contains("dejó de responder", manager.Jobs.Jobs[0].Error, StringComparison.Ordinal);
        Assert.Contains("dejó de responder", ModuleManager.Friendly(manager.Find("eco-a")!, error), StringComparison.Ordinal);

        await manager.InvokeAsync("eco-a", "eco", null, "Eco");
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task Una_llamada_larga_que_contesta_el_ping_no_se_toma_por_colgada()
    {
        // La llamada (unos 3 s) dura más que el tope del ping (1,5 s): solo sigue viva si el módulo contesta cada ping.
        // Con 500 ms de tope, una respuesta que el pool del runner cargado atendía tarde mataba a un módulo sano, y con
        // 1,5 s siguió pasando en la CI (#36, #37, #41, #44). Exige contestar cada ping en 1,5 s de reloj: es TiempoReal.
        await using ModuleManager manager = _modules.Manager(new ModuleManagerOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(50),
            PingTimeout = TimeSpan.FromMilliseconds(1500),
        });
        ModuleResult result = await manager.InvokeAsync("eco-a", "contar", EchoModules.Json("""{"pasos":300,"msPorPaso":10}"""), "Contar");
        Assert.Equal(300, result.Value!.Value.GetProperty("pasos").GetInt32());
    }

    [Fact]
    public async Task Un_error_de_funcion_no_cierra_el_modulo()
    {
        await using ModuleManager manager = _modules.Manager();
        await Assert.ThrowsAsync<ModuleCallException>(() => manager.InvokeAsync("eco-a", "fallar", null, "Fallar"));
        int pid = Assert.Single(manager.ProcessIds);
        await manager.InvokeAsync("eco-a", "eco", null, "Eco");
        Assert.Equal(pid, Assert.Single(manager.ProcessIds));
    }

    [Fact]
    public async Task Al_cerrar_no_queda_ningun_proceso_de_modulo()
    {
        ModuleManager manager = _modules.Manager();
        Task<ModuleResult> busy = manager.InvokeAsync("eco-a", "contar", EchoModules.Json(LongCount), "Contar");
        await manager.InvokeAsync("eco-b", "eco", null, "Eco");
        await WaitUntil(() => manager.ProcessIds.Count == 2);
        int[] pids = [.. manager.ProcessIds];

        await manager.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => busy);
        Assert.All(pids, pid => Assert.True(EchoModules.HasExited(pid)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.InvokeAsync("eco-a", "eco", null, "Eco"));
    }

    [Fact]
    public async Task Si_Rodaje_muere_el_Job_Object_mata_a_los_modulos()
    {
        using var job = new ModuleJobObject();
        using Process process = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Rodaje.Module.Echo.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        })!;
        Assert.True(job.TryAssign(process));
        Assert.False(process.HasExited);

        // Cerrar el último handle es lo que hace Windows cuando el proceso de Rodaje termina, aunque sea de golpe.
        job.Dispose();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Un_modulo_de_GPU_baja_la_vista_previa_mientras_corre_y_uno_pesado_cierra_al_otro_ocioso()
    {
        using EchoModules gpu = new EchoModules().Add("vision", vramMb: 1500).Add("voz-gpu", vramMb: 1200).Add("cpu");
        var preview = new CountingPreview();
        await using ModuleManager manager = gpu.Manager(new ModuleManagerOptions { DedicatedVideoMemory = () => 4L << 30 }, preview);

        Task<ModuleResult> call = manager.InvokeAsync("vision", "contar", EchoModules.Json("""{"pasos":20,"msPorPaso":10}"""), "Quitar fondo");
        await WaitUntil(() => preview.Active == 1);
        await call;
        Assert.Equal(0, preview.Active);
        Assert.Equal(["vision"], manager.Budget.Resident);
        int visionPid = Assert.Single(manager.ProcessIds);

        await manager.InvokeAsync("cpu", "eco", null, "CPU");
        Assert.Equal(1, preview.Lowered);

        // Dos pesados no caben a la vez: el ocioso se cierra para dejar lugar.
        await manager.InvokeAsync("voz-gpu", "eco", null, "Voz");
        Assert.True(EchoModules.HasExited(visionPid));
        Assert.Equal(["voz-gpu"], manager.Budget.Resident);
        Assert.Equal(2, preview.Lowered);
    }

    [Fact]
    public async Task Un_modulo_pesado_espera_a_que_termine_el_otro()
    {
        using EchoModules gpu = new EchoModules().Add("vision", vramMb: 1500).Add("voz-gpu", vramMb: 1200);
        await using ModuleManager manager = gpu.Manager(new ModuleManagerOptions { DedicatedVideoMemory = () => 4L << 30 });

        Task<ModuleResult> first = manager.InvokeAsync("vision", "contar", EchoModules.Json("""{"pasos":30,"msPorPaso":10}"""), "Primero");
        await WaitUntil(() => manager.ProcessIds.Count == 1);
        Task<ModuleResult> second = manager.InvokeAsync("voz-gpu", "eco", null, "Segundo");
        await WaitUntil(() => manager.Jobs.Jobs.Any(j => j.Stage == "Esperando memoria de video"));
        Assert.False(second.IsCompleted);

        await first;

        // Tope contra cuelgues: el segundo tiene que esperar a que se cierre el primero y arrancar su proceso, y con el
        // runner cargado eso pasó de 5 s (PR #49). Arrancar tiene su propio tope de 30 s (StartTimeout).
        await second.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(["voz-gpu"], manager.Budget.Resident);
    }

    [Fact]
    public async Task Un_modulo_no_instalado_falla_con_un_mensaje_claro()
    {
        await using ModuleManager manager = _modules.Manager();
        ModuleException error = await Assert.ThrowsAsync<ModuleException>(() => manager.InvokeAsync("no-esta", "eco", null, "Eco"));
        Assert.Equal("not-installed", error.Code);
    }

    [Fact]
    public async Task Las_regiones_de_la_llamada_llevan_datos_al_modulo_y_se_liberan_al_terminar()
    {
        await using ModuleManager manager = _modules.Manager();
        SharedMemoryRegion? kept = null;
        byte[] copied = await manager.RunAsync("eco-a", "Copiar", async call =>
        {
            SharedMemoryRegion region = kept = call.CreateRegion(2048);
            region.GetSpan(0, 1024).Fill(42);
            SharedBufferRef input = region.Describe(0, 1024);
            SharedBufferRef output = region.Describe(1024, 1024);
            ModuleResult result = await call.InvokeAsync("copiar", null, [input, output]);
            return region.GetSpan(Assert.Single(result.Buffers)).ToArray();
        });
        Assert.All(copied, b => Assert.Equal(42, b));
        Assert.True(kept!.IsDisposed);
    }

    // Tope contra cuelgues: casi siempre se espera a que un proceso arranque, y el arranque puede tardar hasta
    // ModuleManagerOptions.StartTimeout (30 s).
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 45_000)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.ElapsedMilliseconds < timeoutMs, $"No se cumplió en {timeoutMs / 1000} s. {UiThread.PoolState()}");
            await Task.Delay(10);
        }
    }
}
