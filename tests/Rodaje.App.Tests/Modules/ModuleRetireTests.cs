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

    /// <summary>
    /// Tope anticuelgue de las esperas sobre el pool en <c>ReserveVram_espera_en_vez_de_girar…</c>: no mide nada, solo
    /// corta si algo se cuelga. Con el pool topado y ocupado, el trabajo de esas esperas puede tardar más de un minuto en
    /// tener hilo sin que el código falle.
    /// </summary>
    private static readonly TimeSpan AntiHang = TimeSpan.FromMinutes(5);

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
        ModuleJob job = manager.Jobs.Jobs[0]; // La más nueva va primero.
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
            // Un eco sano que tarda en contestar no debe darse por colgado (en la CI cargada un ping tardó 2 s): la prueba
            // no mide tiempo, solo necesita que el vigilante mate al suspendido.
            PingTimeout = TimeSpan.FromSeconds(3),
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
        ModuleJob job = manager.Jobs.Jobs[0]; // La más nueva va primero.
        await WaitUntil(() => manager.ProcessIds.Count == 1 && job.Progress > 0);
        int second = manager.ProcessIds[0];
        Assert.NotEqual(first, second);
        EchoModules.Suspend(second);

        // Tope contra cuelgues, no cota de tiempo: sin vigilante, la llamada no termina nunca.
        await Assert.ThrowsAsync<ModuleHungException>(() => call.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.True(await EchoModules.HasExitedAsync(second));
        stillWatching.SetResult();
    }

    /// <summary>
    /// Una llamada de un módulo de GPU sigue en curso cuando su proceso muere: el aviso de cierre (OnClosed) no suelta la
    /// reserva, que es de la llamada, y la suelta ella al terminar porque no quedó proceso.
    /// </summary>
    [Fact]
    public async Task Con_una_llamada_en_curso_la_VRAM_del_proceso_muerto_se_suelta_al_terminar_la_llamada()
    {
        using EchoModules gpu = new EchoModules().Add("vision", vramMb: 1500).Add("voz-gpu", vramMb: 1200);
        await using ModuleManager manager = gpu.Manager(new ModuleManagerOptions { DedicatedVideoMemory = () => 4L << 30 });
        var (started, finish, call) = HeldCall(manager);
        await started;
        object connection = Connection(manager, "vision");

        KillCurrentProcess(manager);
        await Retired(connection);
        Assert.Equal(["vision"], manager.Budget.Resident);
        Assert.False(manager.Budget.TryReserve("voz-gpu", 1200), "El aviso de cierre soltó la VRAM de una llamada en curso.");

        finish.SetResult(0);
        await call;
        Assert.Empty(manager.Budget.Resident);
    }

    /// <summary>
    /// Dos llamadas en curso cuando el proceso muere: la primera que termina no suelta la reserva (la otra sigue), la
    /// segunda sí.
    /// </summary>
    [Fact]
    public async Task Con_dos_llamadas_en_curso_la_VRAM_del_proceso_muerto_la_suelta_la_ultima()
    {
        using EchoModules gpu = new EchoModules().Add("vision", vramMb: 1500).Add("voz-gpu", vramMb: 1200);
        await using ModuleManager manager = gpu.Manager(new ModuleManagerOptions { DedicatedVideoMemory = () => 4L << 30 });
        var (firstStarted, finishFirst, first) = HeldCall(manager);
        var (secondStarted, finishSecond, second) = HeldCall(manager);
        await Task.WhenAll(firstStarted, secondStarted);
        object connection = Connection(manager, "vision");

        KillCurrentProcess(manager);
        await Retired(connection);

        finishFirst.SetResult(0);
        await first;
        Assert.Equal(["vision"], manager.Budget.Resident);
        Assert.False(manager.Budget.TryReserve("voz-gpu", 1200), "La primera llamada soltó la VRAM con la otra todavía en curso.");

        finishSecond.SetResult(0);
        await second;
        Assert.Empty(manager.Budget.Resident);
    }

    /// <summary>
    /// El aviso de cierre de un proceso muerto salía enseguida, antes de que el hilo propio lo matara y lo soltara: sin
    /// llamadas, la VRAM quedaba libre con el proceso todavía en la GPU y otro módulo pesado cargaba encima.
    /// </summary>
    [Fact]
    public async Task La_VRAM_del_proceso_muerto_no_se_suelta_hasta_que_termina_de_morir()
    {
        using EchoModules gpu = new EchoModules().Add("vision", vramMb: 1500).Add("voz-gpu", vramMb: 1200);
        await using ModuleManager manager = gpu.Manager(new ModuleManagerOptions { DedicatedVideoMemory = () => 4L << 30 });
        await manager.InvokeAsync("vision", "eco", null, "Abrir");
        object connection = Connection(manager, "vision");
        int callerThread = Environment.CurrentManagedThreadId;
        using var release = new ManualResetEventSlim();
        SetProperty(connection, "KillProcess", (Action<ModuleProcess>)(p =>
        {
            // Un árbol lento de matar. En el hilo del llamador no se espera: la prueba se colgaría en vez de fallar.
            if (Environment.CurrentManagedThreadId != callerThread)
            {
                release.Wait();
            }

            p.Kill();
        }));

        try
        {
            // El aviso de salida del proceso, en este hilo para saber cuándo volvió.
            Invoke(connection, "OnExited", GetField(connection, "_process") ?? throw new InvalidOperationException("Sin proceso."));
            Assert.Equal(["vision"], manager.Budget.Resident);
            Assert.False(manager.Budget.TryReserve("voz-gpu", 1200), "Otro módulo pesado reservó mientras el muerto seguía muriendo.");
        }
        finally
        {
            release.Set();
        }

        await Retired(connection);
        Assert.Empty(manager.Budget.Resident);
    }

    /// <summary>
    /// El aviso de cierre del proceso retirado en EnsureStartedAsync llega cuando terminó de morir: para entonces la
    /// llamada que lo reemplazó pudo terminar y dejar abierto el nuevo, que es dueño de la reserva.
    /// </summary>
    [Fact]
    public async Task El_aviso_tardio_del_proceso_retirado_no_suelta_la_VRAM_del_que_lo_reemplazo()
    {
        using EchoModules gpu = new EchoModules().Add("vision", vramMb: 1500).Add("voz-gpu", vramMb: 1200);
        await using ModuleManager manager = gpu.Manager(new ModuleManagerOptions { DedicatedVideoMemory = () => 4L << 30 });
        await manager.InvokeAsync("vision", "eco", null, "Abrir");
        object connection = Connection(manager, "vision");
        MarkDeadWithoutNotice(connection);
        int callerThread = Environment.CurrentManagedThreadId;
        using var release = new ManualResetEventSlim();
        SetProperty(connection, "KillProcess", (Action<ModuleProcess>)(p =>
        {
            // El retiro corre en este hilo hasta abrir el nuevo: si matara aquí, la prueba se colgaría en vez de fallar.
            if (Environment.CurrentManagedThreadId != callerThread)
            {
                release.Wait();
            }

            p.Kill();
        }));

        try
        {
            await manager.InvokeAsync("vision", "eco", null, "Eco");
            Assert.Single(manager.ProcessIds);
        }
        finally
        {
            release.Set();
        }

        await Retired(connection);
        Assert.Equal(["vision"], manager.Budget.Resident);
        Assert.False(manager.Budget.TryReserve("voz-gpu", 1200), "El aviso del proceso retirado soltó la VRAM del que lo reemplazó.");
    }

    /// <summary>
    /// <c>CloseIfIdleAsync</c> da <c>true</c> solo si cerró un proceso. Con un muerto que todavía se retira (la costura
    /// <c>KillProcess</c> bloqueada) no hay proceso que cerrar: daba <c>true</c> y ReserveVramAsync reintentaba en vacío en
    /// vez de esperar a que el retiro soltara la VRAM.
    /// </summary>
    [Fact]
    public async Task CloseIfIdle_da_false_mientras_el_proceso_muerto_se_retira()
    {
        // Sin cierre por ocio: el temporizador no debe adelantarse a la prueba.
        await using ModuleManager manager = _modules.Manager(new ModuleManagerOptions { IdleTimeout = Timeout.InfiniteTimeSpan });
        await manager.InvokeAsync("eco-a", "eco", null, "Abrir");
        object connection = Connection(manager, "eco-a");
        int callerThread = Environment.CurrentManagedThreadId;
        using var release = new ManualResetEventSlim();
        SetProperty(connection, "KillProcess", (Action<ModuleProcess>)(p =>
        {
            // Un árbol lento de matar. En el hilo del llamador no se espera: la prueba se colgaría en vez de fallar.
            if (Environment.CurrentManagedThreadId != callerThread)
            {
                release.Wait();
            }

            p.Kill();
        }));

        try
        {
            Invoke(connection, "OnExited", GetField(connection, "_process") ?? throw new InvalidOperationException("Sin proceso."));
            Task retiring = (Task)GetField(connection, "_retiring")!;
            Assert.False(retiring.IsCompleted, "El retiro terminó con la costura KillProcess bloqueada.");

            Assert.False(await CloseIfIdle(connection), "CloseIfIdleAsync dijo que cerró un proceso que no había.");
            Assert.False(retiring.IsCompleted, "El retiro terminó con la costura KillProcess bloqueada.");
        }
        finally
        {
            release.Set();
        }

        await Retired(connection);
    }

    /// <summary>Con un proceso abierto y sin llamadas, la primera vez lo cierra (<c>true</c>) y la segunda ya no hay nada (<c>false</c>).</summary>
    [Fact]
    public async Task CloseIfIdle_da_true_al_cerrar_el_proceso_ocioso_y_false_despues()
    {
        await using ModuleManager manager = _modules.Manager(new ModuleManagerOptions { IdleTimeout = Timeout.InfiniteTimeSpan });
        await manager.InvokeAsync("eco-a", "eco", null, "Abrir");
        object connection = Connection(manager, "eco-a");
        Assert.Single(manager.ProcessIds);

        Assert.True(await CloseIfIdle(connection), "CloseIfIdleAsync no cerró el proceso ocioso.");
        Assert.Empty(manager.ProcessIds);
        Assert.False(await CloseIfIdle(connection), "CloseIfIdleAsync dijo que cerró un proceso que no había.");
    }

    /// <summary>
    /// Con una llamada en curso no cierra nada (<c>false</c>) y el proceso sigue abierto. Fija el contrato de la guarda
    /// <c>_active &gt; 0</c>, que ya estaba antes de #169: no detecta la reversión de ese arreglo (para eso,
    /// <see cref="CloseIfIdle_da_false_mientras_el_proceso_muerto_se_retira"/> y
    /// <see cref="ReserveVram_espera_en_vez_de_girar_mientras_el_proceso_muerto_se_retira"/>).
    /// </summary>
    [Fact]
    public async Task CloseIfIdle_da_false_con_una_llamada_en_curso()
    {
        using EchoModules gpu = new EchoModules().Add("vision", vramMb: 1500);
        await using ModuleManager manager = gpu.Manager(new ModuleManagerOptions
        {
            IdleTimeout = Timeout.InfiniteTimeSpan,
            DedicatedVideoMemory = () => 4L << 30,
        });
        var (started, finish, call) = HeldCall(manager);
        await started;
        object connection = Connection(manager, "vision");
        int process = Assert.Single(manager.ProcessIds);

        Assert.False(await CloseIfIdle(connection), "CloseIfIdleAsync cerró el proceso de una llamada en curso.");
        Assert.Equal(process, Assert.Single(manager.ProcessIds));

        finish.SetResult(0);
        await call;
    }

    /// <summary>
    /// Otro módulo pesado espera VRAM mientras el muerto de «vision» se retira (la costura <c>KillProcess</c> bloqueada):
    /// no hay proceso que cerrar, <c>CloseIfIdleAsync</c> da <c>false</c> y ReserveVramAsync espera <c>Budget.Changed</c>
    /// o la pausa de reintento (100 ms) antes de la vuelta siguiente. Con el contrato de antes de #169 (<c>true</c> aunque
    /// no cerrara nada) giraba sin pausa y en línea. Cada vuelta avisa una vez en <c>Jobs.Changed</c> (SetWaiting; sin
    /// contexto, en el mismo hilo), así que ahí se cuentan. La cota sale del tiempo que de verdad pasó: una máquina lenta
    /// solo la agranda; la pausa de medio segundo solo le da tiempo a girar al fallo.
    /// </summary>
    [Fact]
    public async Task ReserveVram_espera_en_vez_de_girar_mientras_el_proceso_muerto_se_retira()
    {
        using EchoModules gpu = new EchoModules().Add("vision", vramMb: 1500).Add("voz-gpu", vramMb: 1200);
        await using ModuleManager manager = gpu.Manager(new ModuleManagerOptions
        {
            IdleTimeout = Timeout.InfiniteTimeSpan,
            DedicatedVideoMemory = () => 4L << 30,
        });
        await manager.InvokeAsync("vision", "eco", null, "Abrir");
        object connection = Connection(manager, "vision");
        int callerThread = Environment.CurrentManagedThreadId;
        using var release = new ManualResetEventSlim();
        SetProperty(connection, "KillProcess", (Action<ModuleProcess>)(p =>
        {
            // Un árbol lento de matar. En el hilo del llamador no se espera: la prueba se colgaría en vez de fallar.
            if (Environment.CurrentManagedThreadId != callerThread)
            {
                release.Wait();
            }

            p.Kill();
        }));

        Task<ModuleResult> call;
        try
        {
            Invoke(connection, "OnExited", GetField(connection, "_process") ?? throw new InvalidOperationException("Sin proceso."));
            Assert.Equal(["vision"], manager.Budget.Resident);
            int notices = 0;
            manager.Jobs.Changed += (_, _) => Interlocked.Increment(ref notices);
            Stopwatch watch = Stopwatch.StartNew();
            // En otro hilo: con el contrato de antes las vueltas son síncronas y la llamada no devolvería el control.
            call = Task.Run(() => manager.InvokeAsync("voz-gpu", "eco", null, "Eco"));
            await WaitUntil(() => manager.Jobs.Jobs.Any(j => j.Title == "Eco" && j.Stage == "Esperando memoria de video"), AntiHang);
            await Task.Delay(500);
            int seen = Volatile.Read(ref notices);
            long elapsed = watch.ElapsedMilliseconds;

            // Una vuelta cada 100 ms como mucho, más el alta del trabajo: el doble de margen por la resolución del reloj.
            Assert.True(seen <= (elapsed / 50) + 5, $"ReserveVramAsync dio {seen} vueltas en {elapsed} ms: reintenta en vacío.");
            Assert.Equal(["vision"], manager.Budget.Resident);
        }
        finally
        {
            release.Set();
        }

        // Al terminar de morir, el aviso de cierre suelta la VRAM de «vision» y la llamada que esperaba entra.
        await Retired(connection, AntiHang);
        await call.WaitAsync(AntiHang);
        Assert.Equal(["voz-gpu"], manager.Budget.Resident);
    }

    private static Task<bool> CloseIfIdle(object connection) =>
        (Task<bool>)(connection.GetType().GetMethod("CloseIfIdleAsync", BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes)
            ?? throw new MissingMethodException(connection.GetType().Name, "CloseIfIdleAsync")).Invoke(connection, null)!;

    /// <summary>Una llamada a «vision» que trabaja hasta que la prueba la deja terminar (sin usar el proceso).</summary>
    private static (Task Started, TaskCompletionSource<int> Finish, Task<int> Call) HeldCall(ModuleManager manager)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> call = manager.RunAsync("vision", "Largo", async _ =>
        {
            started.SetResult();
            return await finish.Task;
        });
        return (started.Task, finish, call);
    }

    private static void KillCurrentProcess(ModuleManager manager)
    {
        using Process process = Process.GetProcessById(Assert.Single(manager.ProcessIds));
        process.Kill();
    }

    /// <summary>Espera a que el proceso muerto termine de retirarse (matado, soltado y avisado el cierre).</summary>
    private static Task Retired(object connection) => Retired(connection, DefaultTimeout);

    /// <summary>Igual, con otro tope para esperar a que empiece el retiro.</summary>
    private static async Task Retired(object connection, TimeSpan timeout)
    {
        await WaitUntil(() => GetField(connection, "_retiring") is not null, timeout);
        await (Task)GetField(connection, "_retiring")!;
    }

    private static void Invoke(object target, string name, object argument) =>
        (target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(target.GetType().Name, name))
            .Invoke(target, [argument]);

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
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    private static Task WaitUntil(Func<bool> condition) => WaitUntil(condition, DefaultTimeout);

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < timeout, $"No se cumplió en {timeout.TotalSeconds:0} s. {UiThread.PoolState()}");
            await Task.Delay(10);
        }
    }
}
