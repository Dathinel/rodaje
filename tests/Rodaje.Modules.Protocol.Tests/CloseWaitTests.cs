using System.Diagnostics;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Tarea 206: la ventana espera el cierre de los módulos con <see cref="ModuleProcess.WaitForCloseAsync(Task, TimeSpan)"/>
/// (a través de <c>ModuleManager.CloseOnExitAsync</c>). Con el tope <c>CloseTimeout</c> no bastaba: su holgura
/// (<see cref="ModuleProcess.DisposeMargin"/>, 1 s) decía cubrir matar el árbol de procesos, que con la CPU llena tarda
/// segundos, y la ventana seguía con el módulo vivo. Ahora, si el tope vence mientras el cierre mata, se espera esa muerte.
/// Sin reloj: el tope es cero y la muerte la retiene una <see cref="TaskCompletionSource"/>, así que «más allá del
/// margen» es seguro y el orden lo marcan las pruebas.
/// </summary>
public sealed class CloseWaitTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_close_that_ends_in_time_is_true()
    {
        Assert.True(await ModuleProcess.WaitForCloseAsync(Task.CompletedTask, TimeSpan.Zero, Unexpected, Long).WaitAsync(Long));
    }

    [Fact]
    public async Task A_failed_close_is_rethrown()
    {
        Task close = Task.FromException(new InvalidOperationException("cierre roto"));
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ModuleProcess.WaitForCloseAsync(close, Long, Unexpected, Long).WaitAsync(Long));
        Assert.Equal("cierre roto", error.Message);
    }

    [Fact]
    public async Task Without_a_kill_in_flight_the_timeout_still_bounds_the_wait()
    {
        TaskCompletionSource close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(await ModuleProcess.WaitForCloseAsync(close.Task, TimeSpan.Zero, () => Task.CompletedTask, Long).WaitAsync(Long));
    }

    /// <summary>
    /// La muerte retenida pasa del tope (cero) y de cualquier margen: la espera no vuelve hasta que la muerte termina y el
    /// cierre con ella. Con la espera de antes (solo el tope) daría <c>false</c> sin mirar las muertes, y
    /// <c>asked</c> no llegaría nunca.
    /// </summary>
    [Fact]
    public async Task A_kill_held_beyond_the_timeout_is_waited_for()
    {
        TaskCompletionSource close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource kill = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> wait = ModuleProcess.WaitForCloseAsync(close.Task, TimeSpan.Zero, () =>
        {
            asked.TrySetResult();
            return kill.Task;
        }, Long);

        await asked.Task.WaitAsync(Long);
        Assert.False(wait.IsCompleted, "la espera volvió con la muerte todavía en curso");

        kill.SetResult();
        close.SetResult();
        Assert.True(await wait.WaitAsync(Long), "la espera no dio el cierre por terminado tras la muerte");
    }

    [Fact]
    public async Task A_kill_that_outlasts_its_own_cap_gives_up()
    {
        TaskCompletionSource close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource kill = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(await ModuleProcess.WaitForCloseAsync(close.Task, TimeSpan.Zero, () => kill.Task, TimeSpan.Zero).WaitAsync(Long));
    }

    /// <summary>
    /// De punta a punta con el cierre de verdad (<see cref="ModuleProcess.CloseAsync"/>) y la espera de la ventana tal
    /// cual (<see cref="ModuleProcess.WaitForCloseAsync(Task, TimeSpan)"/>, que mira <see cref="ModuleProcess.PendingCloseKills"/>):
    /// un módulo que no sale, una muerte retenida y un tope cero. Si el cierre no deja su muerte a la vista, la espera
    /// da <c>false</c> (y la prueba no ve la muerte en curso).
    /// </summary>
    [Fact]
    public async Task The_window_wait_covers_the_kill_of_the_real_close()
    {
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource kill = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource killing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> lines = [];

        Task close = ModuleProcess.CloseAsync(() => ValueTask.CompletedTask, () => true, exited.Task, () =>
        {
            killing.TrySetResult();
            return kill.Task;
        }, TimeSpan.Zero, "modulo-colgado", lines.Add);

        await killing.Task.WaitAsync(Long);
        // La muerte se anota justo después de pedirla, en el hilo del cierre: se espera a verla, con cota holgada.
        Stopwatch watch = Stopwatch.StartNew();
        while (!ModuleProcess.IsCloseKillPending(kill.Task))
        {
            Assert.True(watch.Elapsed < Long, "el cierre no dejó a la vista la muerte en curso");
            await Task.Delay(10);
        }

        Task<bool> wait = ModuleProcess.WaitForCloseAsync(close, TimeSpan.Zero);
        Assert.False(wait.IsCompleted, "la espera de la ventana volvió con la muerte todavía en curso");

        kill.SetResult();
        exited.SetResult(-1);
        Assert.True(await wait.WaitAsync(Long), "la espera de la ventana no cubrió la muerte del módulo");
        Assert.False(ModuleProcess.IsCloseKillPending(kill.Task), "la muerte terminada sigue anotada");
        Assert.Contains("modulo-colgado", Assert.Single(lines), StringComparison.Ordinal);
    }

    private static Task Unexpected() => throw new InvalidOperationException("No hay que mirar las muertes.");
}
