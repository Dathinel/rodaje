using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Tests.Modules;

/// <summary>
/// El retiro inmediato de un proceso de módulo muerto cuyo aviso de salida aún no llegó (<c>EnsureStartedAsync</c>): no
/// suelta la VRAM que la llamada nueva acaba de reservar, no mata el árbol de procesos en el hilo del llamador ni dentro
/// del cerrojo de la conexión, y el proceso nuevo tiene su propio vigilante de <c>ping</c>.
/// </summary>
/// <remarks>
/// Ese aviso llega por continuaciones asíncronas y con el pool cargado se atrasa (CI de Windows tras #111); para que las
/// pruebas no dependan del pool, el estado «muerto sin aviso» y «el vigilante anterior aún no terminó» se ponen por
/// reflexión en los campos privados de <c>ModuleConnection</c> (Rodaje.App no expone sus internos a las pruebas).
/// </remarks>
[Collection(ModuleProcessGroup.Name)]
public sealed class ModuleRetireTests : IDisposable
{
    private const string LongCount = """{"pasos":100000,"msPorPaso":10}""";
    private readonly EchoModules _modules = new EchoModules().Add("eco-a");

    public void Dispose() => _modules.Dispose();

    /// <summary>
    /// Un módulo de GPU cuyo proceso quedó muerto sin aviso: la llamada siguiente reserva su VRAM, retira el muerto y abre
    /// otro. Retirarlo soltaba la reserva (closed → OnClosed → Budget.Release) y otro módulo pesado entraba encima mientras
    /// el nuevo trabajaba.
    /// </summary>
    [Fact]
    public async Task Retirar_el_proceso_muerto_no_suelta_la_VRAM_de_la_llamada_que_lo_reemplaza()
    {
        using EchoModules gpu = new EchoModules().Add("vision", vramMb: 1500).Add("voz-gpu", vramMb: 1200);
        await using ModuleManager manager = gpu.Manager(new ModuleManagerOptions { DedicatedVideoMemory = () => 4L << 30 });
        await manager.InvokeAsync("vision", "eco", null, "Abrir");
        Assert.Equal(["vision"], manager.Budget.Resident);
        object connection = Connection(manager, "vision");
        MarkDeadWithoutNotice(connection);

        Task<ModuleResult> call = manager.InvokeAsync("vision", "contar", EchoModules.Json(LongCount), "Contar");
        ModuleJob job = manager.Jobs.Jobs[^1];
        await WaitUntil(() => job.Progress > 0);

        Assert.Equal(["vision"], manager.Budget.Resident);
        Assert.False(manager.Budget.TryReserve("voz-gpu", 1200), "Otro módulo pesado reservó encima del que está trabajando.");

        job.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    /// <summary>
    /// Matar el árbol de procesos tarda segundos con la CPU llena. El retiro inmediato lo hacía en línea: en el hilo que
    /// llamó (el de la interfaz, si todo lo anterior terminó sin esperar) y con el cerrojo de la conexión tomado, así que la
    /// llamada nueva no abría su proceso hasta que el muerto terminara de morir.
    /// </summary>
    [Fact]
    public async Task El_proceso_muerto_se_mata_fuera_del_hilo_que_llama_y_sin_frenar_la_llamada_nueva()
    {
        await using ModuleManager manager = _modules.Manager();
        await manager.InvokeAsync("eco-a", "eco", null, "Abrir");
        object connection = Connection(manager, "eco-a");
        MarkDeadWithoutNotice(connection);

        int callerThread = -1;
        int killThread = -1;
        using var release = new ManualResetEventSlim();
        SetProperty(connection, "KillProcess", (Action<ModuleProcess>)(p =>
        {
            Volatile.Write(ref killThread, Environment.CurrentManagedThreadId);
            // Un árbol lento de matar. En el hilo del llamador no se espera: la prueba se colgaría en vez de fallar.
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref callerThread))
            {
                release.Wait();
            }

            p.Kill();
        }));

        try
        {
            Task<ModuleResult>? call = null;
            var caller = new Thread(() =>
            {
                Volatile.Write(ref callerThread, Environment.CurrentManagedThreadId);
                call = manager.InvokeAsync("eco-a", "eco", null, "Eco");
            });
            caller.Start();
            caller.Join();

            // Con el muerto todavía muriendo, la llamada nueva abre su proceso y termina.
            await call!;
            await WaitUntil(() => Volatile.Read(ref killThread) != -1);
            Assert.NotEqual(callerThread, killThread);
            Assert.Single(manager.ProcessIds);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// El vigilante solo se relanzaba si el anterior ya había terminado. El del proceso viejo sale al despertar y ver que
    /// su proceso ya no es el actual, pero con el pool cargado puede no haber despertado cuando se abre el siguiente: el
    /// nuevo se quedaba sin <c>ping</c> y un cuelgue en esa llamada no se mataba nunca.
    /// </summary>
    [Fact]
    public async Task El_proceso_nuevo_tiene_vigilante_aunque_el_del_anterior_no_haya_terminado()
    {
        await using ModuleManager manager = _modules.Manager(new ModuleManagerOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(100),
            PingTimeout = TimeSpan.FromMilliseconds(400),
        });
        await manager.InvokeAsync("eco-a", "eco", null, "Abrir");
        int first = Assert.Single(manager.ProcessIds);
        object connection = Connection(manager, "eco-a");

        // El vigilante del primer proceso todavía no despertó para ver que su proceso murió.
        var stillWatching = new TaskCompletionSource();
        SetField(connection, "_watchdog", stillWatching.Task);
        using (Process process = Process.GetProcessById(first))
        {
            process.Kill();
        }

        await WaitUntil(() => manager.ProcessIds.Count == 0);

        Task<ModuleResult> call = manager.InvokeAsync("eco-a", "contar", EchoModules.Json(LongCount), "Contar");
        ModuleJob job = manager.Jobs.Jobs[^1];
        await WaitUntil(() => manager.ProcessIds.Count == 1 && job.Progress > 0);
        int second = manager.ProcessIds[0];
        Assert.NotEqual(first, second);
        EchoModules.Suspend(second);

        // Tope contra cuelgues, no cota de tiempo: sin vigilante, la llamada no termina nunca.
        await Assert.ThrowsAsync<ModuleHungException>(() => call.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.True(EchoModules.HasExited(second));
        stillWatching.SetResult();
    }

    private static object Connection(ModuleManager manager, string moduleId)
    {
        var connections = (IDictionary)GetField(manager, "_connections")!;
        return connections[moduleId]!;
    }

    /// <summary>El proceso actual queda dado por muerto (como el que mató el <c>ping</c>) sin que llegue su aviso de salida.</summary>
    private static void MarkDeadWithoutNotice(object connection) =>
        SetField(connection, "_hungProcess", GetField(connection, "_process") ?? throw new InvalidOperationException("Sin proceso."));

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(type.Name, name);

    private static object? GetField(object target, string name) => Field(target.GetType(), name).GetValue(target);

    private static void SetField(object target, string name, object value) => Field(target.GetType(), name).SetValue(target, value);

    private static void SetProperty(object target, string name, object value) =>
        (target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMemberException(target.GetType().Name, name))
            .SetValue(target, value);

    // Tope contra cuelgues: casi siempre se espera a que un proceso arranque (hasta StartTimeout, 30 s).
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
