using System.Diagnostics;
using System.Runtime.InteropServices;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Tarea 197: <see cref="ModuleProcess.DisposeBudget"/> es lo más que tarda el cierre con un módulo que no cierra tras
/// <c>shutdown</c>, y la ventana espera eso más <see cref="ModuleProcess.DisposeMargin"/> (<c>ModuleManager.CloseTimeout</c>).
/// Aquí, que el presupuesto suma de verdad los plazos del cierre y que un módulo congelado (no lee el <c>shutdown</c> ni
/// sale) queda muerto cuando <see cref="ModuleProcess.DisposeAsync"/> vuelve, dentro del presupuesto.
/// </summary>
public sealed partial class ShutdownBudgetTests
{
    [Fact]
    public void The_budget_adds_the_shutdown_grace_and_both_exit_waits()
    {
        ModuleClientOptions defaults = new();
        Assert.Equal(defaults.ShutdownGrace + ModuleProcess.ExitWait + ModuleProcess.ExitWait, ModuleProcess.DisposeBudget(defaults));

        // Sigue a la gracia configurada: quien la alargue alarga también la espera de la ventana.
        ModuleClientOptions longer = new() { ShutdownGrace = TimeSpan.FromSeconds(10) };
        Assert.Equal(TimeSpan.FromSeconds(10) + ModuleProcess.ExitWait + ModuleProcess.ExitWait, ModuleProcess.DisposeBudget(longer));
        Assert.True(ModuleProcess.DisposeMargin > TimeSpan.Zero);
    }

    /// <remarks>
    /// Con una gracia corta para que la prueba no tarde, y la cota holgada de <see cref="Support.CiBound"/> sobre el
    /// presupuesto. La cota fina, con los plazos por omisión, la mide
    /// <see cref="A_frozen_module_is_dead_within_the_default_budget_plus_margin"/> (TiempoReal).
    /// </remarks>
    [Fact]
    public async Task A_frozen_module_is_dead_when_dispose_returns()
    {
        ModuleClientOptions options = new() { ShutdownGrace = TimeSpan.FromMilliseconds(500) };
        ModuleProcess module = await ModuleProcess.StartAsync(Support.EchoExe, options: options).WaitAsync(TimeSpan.FromSeconds(60));
        int pid = module.ProcessId;
        try
        {
            Freeze(pid);
            Stopwatch watch = Stopwatch.StartNew();
            await module.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            TimeSpan elapsed = watch.Elapsed;

            int exitCode = await module.Exited.WaitAsync(Support.CiBound);
            // Con 100 ms de tolerancia: un temporizador puede saltar un tic antes.
            Assert.True(elapsed >= options.ShutdownGrace + ModuleProcess.ExitWait - TimeSpan.FromMilliseconds(100), $"no esperó la gracia ni la salida: {elapsed.TotalMilliseconds:0} ms");
            Assert.True(elapsed < ModuleProcess.DisposeBudget(options) + Support.CiBound, $"el cierre tardó {elapsed.TotalMilliseconds:0} ms");
            Assert.NotEqual(0, exitCode);
        }
        finally
        {
            KillIfAlive(pid);
        }
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task A_frozen_module_is_dead_within_the_default_budget_plus_margin()
    {
        ModuleClientOptions options = new();
        ModuleProcess module = await ModuleProcess.StartAsync(Support.EchoExe, options: options).WaitAsync(TimeSpan.FromSeconds(60));
        int pid = module.ProcessId;
        try
        {
            Freeze(pid);
            Stopwatch watch = Stopwatch.StartNew();
            await module.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            TimeSpan elapsed = watch.Elapsed;

            Assert.True(module.Exited.IsCompleted, $"el módulo seguía vivo al volver el cierre ({elapsed.TotalMilliseconds:0} ms)");
            Assert.True(elapsed < ModuleProcess.DisposeBudget(options) + ModuleProcess.DisposeMargin, $"el cierre tardó {elapsed.TotalMilliseconds:0} ms");
        }
        finally
        {
            KillIfAlive(pid);
        }
    }

    /// <summary>
    /// Congela el proceso del módulo: no lee el <c>shutdown</c>, no cierra la tubería ni sale, como uno colgado. Matarlo
    /// funciona igual (TerminateProcess en Windows, SIGKILL en Linux).
    /// </summary>
    private static void Freeze(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            using Process process = Process.GetProcessById(pid);
            int status = NtSuspendProcess(process.Handle);
            Assert.True(status == 0, $"NtSuspendProcess devolvió 0x{status:X8}");
            return;
        }

        Assert.True(OperatingSystem.IsLinux(), "Solo se congela en Windows y en Linux.");
        Assert.Equal(0, SysKill(pid, SigStop));
        // La señal llega de forma asíncrona: hasta que /proc diga «T» el módulo todavía podría leer el shutdown.
        Stopwatch wait = Stopwatch.StartNew();
        while (LinuxState(pid) != 'T')
        {
            Assert.True(wait.Elapsed < Support.CiBound, "el módulo no se detuvo");
            Thread.Sleep(10);
        }
    }

    private static char LinuxState(int pid)
    {
        string stat = File.ReadAllText($"/proc/{pid}/stat");
        // «pid (nombre) estado ...»: el nombre puede tener espacios o paréntesis; el estado va tras el último «)».
        return stat[stat.LastIndexOf(')') + 2];
    }

    private static void KillIfAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Ya murió.
        }
    }

    private const int SigStop = 19;

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(IntPtr processHandle);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SysKill(int pid, int signal);
}
