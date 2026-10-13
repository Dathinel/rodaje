using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// <see cref="ModuleProcess.Kill()"/> ya no mata en línea (tarea 137): vuelve enseguida y el árbol de procesos muere en un
/// hilo propio. Que no bloquea lo prueba sin reloj <see cref="KillContinuationTests"/>; aquí, con el módulo de eco de
/// verdad, que llamado desde un hilo del pool (como el vigilante de ping de la app) el módulo muere igual y la llamada
/// en curso falla.
/// </summary>
public sealed class ProcessKillTests
{
    [Fact]
    public async Task Kill_from_a_pool_thread_kills_the_module_and_fails_the_running_call()
    {
        await using ModuleProcess module = await ModuleProcess.StartAsync(Support.EchoExe);
        Task call = module.Client.InvokeAsync("contar", Support.Json("""{"pasos":10000,"msPorPaso":10}"""));

        await Task.Run(module.Kill).WaitAsync(Support.CiBound);

        await Assert.ThrowsAnyAsync<ModuleDisconnectedException>(() => call.WaitAsync(Support.CiBound));
        await module.Exited.WaitAsync(Support.CiBound);
    }
}
