using System.Collections.Concurrent;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Tarea 200. Desde la tarea 191, tras <c>shutdown</c> el fin de flujo cierra <see cref="ModuleClient.Completion"/> sin
/// excepción, así que un módulo que sale mal durante la gracia, o al que hay que matar, solo dejaba el código en
/// <see cref="ModuleProcess.Exited"/>, que nadie mira al cerrar. Ahora <see cref="ModuleProcess.DisposeAsync"/> deja una
/// línea en el registro. Las pruebas con costura (<see cref="ModuleProcess.CloseAsync"/>) no lanzan nada y corren en
/// cualquier sistema; las del módulo de eco de verdad, solo donde corre el <c>.exe</c>.
/// </summary>
public sealed class ShutdownReportTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Exiting_with_3_after_shutdown_is_reported()
    {
        List<string> lines = [];
        await ModuleProcess.CloseAsync(() => ValueTask.CompletedTask, () => true, Task.FromResult(3),
            () => throw new InvalidOperationException("No hay que matarlo."), Long, "modulo-x", lines.Add);

        string line = Assert.Single(lines);
        Assert.Contains("modulo-x", line, StringComparison.Ordinal);
        Assert.Contains("código 3", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exiting_with_0_after_shutdown_is_not_reported()
    {
        List<string> lines = [];
        await ModuleProcess.CloseAsync(() => ValueTask.CompletedTask, () => true, Task.FromResult(0),
            () => throw new InvalidOperationException("No hay que matarlo."), Long, "modulo-x", lines.Add);

        Assert.Empty(lines);
    }

    [Fact]
    public async Task Having_to_kill_it_after_shutdown_is_reported()
    {
        List<string> lines = [];
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int kills = 0;
        // La gracia corta solo acorta la prueba: el proceso no sale hasta que lo matan, así que siempre vence.
        await ModuleProcess.CloseAsync(() => ValueTask.CompletedTask, () => true, exited.Task, () =>
        {
            kills++;
            exited.SetResult(-1);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(10), "modulo-x", lines.Add).WaitAsync(Support.CiBound);

        Assert.Equal(1, kills);
        string line = Assert.Single(lines);
        Assert.Contains("modulo-x", line, StringComparison.Ordinal);
        Assert.Contains("se mató", line, StringComparison.Ordinal);
        Assert.Contains("código -1", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_module_that_died_before_shutdown_is_left_to_whoever_saw_it_die()
    {
        // Si «shutdown» no llegó a salir, el cliente ya había fallado con ModuleExitedException: no es cosa del cierre.
        List<string> lines = [];
        await ModuleProcess.CloseAsync(() => ValueTask.CompletedTask, () => false, Task.FromResult(3),
            () => throw new InvalidOperationException("No hay que matarlo."), Long, "modulo-x", lines.Add);

        Assert.Empty(lines);
    }

    [Fact]
    public void The_line_says_when_the_killed_module_was_not_seen_exiting()
    {
        string? line = ModuleProcess.DescribeClose("modulo-x", killed: true, new TaskCompletionSource<int>().Task, TimeSpan.FromSeconds(2));

        Assert.Equal("Rodaje: el módulo modulo-x no salió en 2 s tras «shutdown»; se mató y no se lo vio salir.", line);
    }

    [Fact]
    public async Task A_failed_exit_task_is_reported_without_throwing()
    {
        // Tarea 242: sin matarlo, DescribeClose leía exited.Result y relanzaba la falla de la tarea en el cierre.
        List<string> lines = [];
        await ModuleProcess.CloseAsync(() => ValueTask.CompletedTask, () => true,
            Task.FromException<int>(new InvalidOperationException("Sin código.")),
            () => throw new InvalidOperationException("No hay que matarlo."), Long, "modulo-x", lines.Add);

        Assert.Equal("Rodaje: el módulo modulo-x se cerró tras «shutdown» sin que se supiera con qué código.", Assert.Single(lines));
    }

    [Fact]
    public void A_canceled_exit_task_is_described_without_throwing()
    {
        string? line = ModuleProcess.DescribeClose("modulo-x", killed: false, Task.FromCanceled<int>(new CancellationToken(true)), TimeSpan.FromSeconds(2));

        Assert.Equal("Rodaje: el módulo modulo-x se cerró tras «shutdown» sin que se supiera con qué código.", line);
    }

    [Fact]
    public async Task Echo_module_exiting_with_3_after_shutdown_is_reported()
    {
        string line = Assert.Single(await CloseEchoAsync("""{"codigo":3}""", expectedExit: 3));
        Assert.Contains("rodaje-eco", line, StringComparison.Ordinal);
        Assert.Contains("código 3", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Echo_module_exiting_with_0_after_shutdown_is_not_reported() =>
        Assert.Empty(await CloseEchoAsync("""{"codigo":0}""", expectedExit: 0));

    [Fact]
    public async Task Echo_module_that_has_to_be_killed_after_shutdown_is_reported()
    {
        string line = Assert.Single(await CloseEchoAsync("""{"colgar":true}""", expectedExit: null));
        Assert.Contains("rodaje-eco", line, StringComparison.Ordinal);
        Assert.Contains("se mató", line, StringComparison.Ordinal);
    }

    private static async Task<string[]> CloseEchoAsync(string onClose, int? expectedExit)
    {
        ConcurrentQueue<string> lines = new();
        ModuleProcess module = await ModuleProcess.StartAsync(Support.EchoExe).WaitAsync(Long);
        module.Report = lines.Enqueue;
        await module.Client.InvokeAsync("al-cerrar", Support.Json(onClose)).WaitAsync(Long);
        await module.DisposeAsync().AsTask().WaitAsync(Long);
        int exit = await module.Exited.WaitAsync(Long);
        if (expectedExit is { } code) Assert.Equal(code, exit);
        return [.. lines];
    }
}
