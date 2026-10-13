using System.Diagnostics;
using Rodaje.App.Services.Modules;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Tests.Modules;

/// <summary>
/// Tarea 197: la ventana espera el cierre de los módulos <see cref="ModuleManager.CloseTimeout"/>, y eso tiene que cubrir
/// todo lo que tarda <see cref="ModuleProcess.DisposeAsync"/> con un módulo que no cierra tras <c>shutdown</c> (la gracia,
/// la espera de la salida, matarlo y la segunda espera). Antes la ventana esperaba 3 s fijos, lo mismo que solo la gracia,
/// y seguía con el módulo colgado vivo. Que el módulo congelado muere dentro de ese presupuesto lo prueba
/// <c>ShutdownBudgetTests</c> en Rodaje.Modules.Protocol.Tests. Tarea 206: la espera de la ventana es
/// <see cref="ModuleManager.CloseOnExitAsync"/>, y aquí se ejerce con un módulo congelado de verdad; que además cubre matar
/// el árbol aunque pase del margen lo prueba <c>CloseWaitTests</c> (sin reloj).
/// </summary>
[Collection(ModuleProcessGroup.Name)]
public sealed class ModuleCloseTimeoutTests
{
    [Fact]
    public async Task The_window_waits_longer_than_a_hung_module_takes_to_die()
    {
        ModuleManagerOptions options = new();
        await using ModuleManager manager = new(options);
        TimeSpan budget = ModuleProcess.DisposeBudget(options.ClientOptions);

        Assert.True(manager.CloseTimeout >= budget + ModuleProcess.DisposeMargin,
            $"la ventana espera {manager.CloseTimeout.TotalSeconds:0.#} s y un módulo colgado tarda hasta {budget.TotalSeconds:0.#} s en morir");
        Assert.True(budget >= options.ClientOptions.ShutdownGrace + ModuleProcess.ExitWait + ModuleProcess.ExitWait);
    }

    [Fact]
    public async Task The_window_wait_follows_a_longer_shutdown_grace()
    {
        ModuleManagerOptions options = new() { ClientOptions = new ModuleClientOptions { ShutdownGrace = TimeSpan.FromSeconds(10) } };
        await using ModuleManager manager = new(options);

        Assert.True(manager.CloseTimeout > TimeSpan.FromSeconds(10) + ModuleProcess.ExitWait + ModuleProcess.ExitWait,
            $"la ventana espera {manager.CloseTimeout.TotalSeconds:0.#} s con una gracia de 10 s");
    }

    /// <summary>
    /// La espera del cierre de la ventana con un módulo congelado (no lee el <c>shutdown</c> ni sale): da <c>true</c> y el
    /// proceso está muerto al volver. Con una gracia de 2 s el cierre tarda al menos 4 s (gracia + espera de la salida), así
    /// que la espera fija de 3 s de antes de la tarea 197 da <c>false</c> y esta prueba cae.
    /// </summary>
    [Fact]
    public async Task The_window_close_waits_until_a_frozen_module_is_dead()
    {
        using var modules = new EchoModules().Add("eco-a");
        ModuleClientOptions client = new() { ShutdownGrace = TimeSpan.FromSeconds(2) };
        await using ModuleManager manager = modules.Manager(new ModuleManagerOptions
        {
            ClientOptions = client,
            // Sin vigilante ni cierre por ocio: lo único que mata al módulo congelado es el cierre.
            PingInterval = Timeout.InfiniteTimeSpan,
            IdleTimeout = Timeout.InfiniteTimeSpan,
        });
        await manager.InvokeAsync("eco-a", "eco", null, "Abrir");
        int pid = Assert.Single(manager.ProcessIds);
        using Process process = Process.GetProcessById(pid);
        EchoModules.Suspend(pid);

        Stopwatch watch = Stopwatch.StartNew();
        // El tope de afuera es contra cuelgues de la prueba: la cota la pone CloseOnExitAsync.
        bool closed = await manager.CloseOnExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        TimeSpan elapsed = watch.Elapsed;

        Assert.True(closed, $"la espera de la ventana se rindió a los {elapsed.TotalSeconds:0.#} s (tope {manager.CloseTimeout.TotalSeconds:0.#} s)");
        Assert.True(process.HasExited, "el módulo congelado seguía vivo al volver la espera de la ventana");
        // Con 100 ms de tolerancia: si no tardó la gracia y la espera de la salida, el módulo no estaba congelado.
        Assert.True(elapsed >= client.ShutdownGrace + ModuleProcess.ExitWait - TimeSpan.FromMilliseconds(100),
            $"el cierre tardó {elapsed.TotalMilliseconds:0} ms: el módulo no estaba congelado");
    }
}
