using Rodaje.App.Services.Modules;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Tests.Modules;

/// <summary>
/// Tarea 197: la ventana espera el cierre de los módulos <see cref="ModuleManager.CloseTimeout"/>, y eso tiene que cubrir
/// todo lo que tarda <see cref="ModuleProcess.DisposeAsync"/> con un módulo que no cierra tras <c>shutdown</c> (la gracia,
/// la espera de la salida, matarlo y la segunda espera). Antes la ventana esperaba 3 s fijos, lo mismo que solo la gracia,
/// y seguía con el módulo colgado vivo. Que el módulo congelado muere dentro de ese presupuesto lo prueba
/// <c>ShutdownBudgetTests</c> en Rodaje.Modules.Protocol.Tests.
/// </summary>
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
}
