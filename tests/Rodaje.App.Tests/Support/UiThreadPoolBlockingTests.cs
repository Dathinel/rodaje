using System.Reflection;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Support;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiThreadPoolBlockingGroup
{
    public const string Name = "Pool de hilos sin otras pruebas a la vez";
}

/// <summary>
/// Gemela sin reloj de <see cref="UiThreadPoolTests"/>, para la CI: no mide cuánto tardan en empezar, sino si el pool
/// se entera de que <see cref="UiThread.Run(Func{Task})"/> bloquea su hilo. Esperando una Task desde un hilo del pool,
/// <c>Task.Wait</c> avisa al pool (<c>ThreadPool.NotifyThreadBlocked</c>), que suma uno a su cuenta de hilos bloqueados
/// y pone otro en su lugar; con <c>Thread.Join</c> el pool no sabe nada y esa cuenta no se mueve.
/// </summary>
/// <remarks>
/// <para>
/// Lee esa cuenta (<c>PortableThreadPool._numBlockedThreads</c>, privada del runtime) por reflexión. Antes (#135)
/// esperaba el evento <c>ThreadPoolWorkerThreadAdjustmentAdjustment</c> con motivo «bloqueo cooperativo» (8), y eso
/// fallaba en Windows aunque el pool sí contara los bloqueados: el pool solo publica ese ajuste si su meta de hilos está
/// por debajo de «mínimo + bloqueados», y la meta es interna (<c>ThreadPool.ThreadCount</c> son los hilos que existen,
/// no la meta). Si la subida de colina o la inanición la habían dejado más alta, no hay nada que subir y el evento no
/// llega («Ajustes: ninguno» con 12 hilos y mínimo 32); y si un hilo ocioso caduca, la meta baja justo a «mínimo +
/// bloqueados» con el motivo 7, «hilo caducado» («Ajustes: 40 (motivo 7)» con mínimo 32 y 8 bloqueados: el pool sí los
/// contaba). Además <c>HillClimbing.ForceChange</c> no publica nada si su último valor ya coincide con la meta nueva.
/// </para>
/// <para>
/// Si un cambio del runtime quita o renombra el campo, la prueba falla diciéndolo (no pasa en falso). Corre sin otras
/// pruebas a la vez (las de su colección van al final, solas): una prueba ajena que espere una Task también sumaría
/// bloqueados y taparía la vuelta a <c>Thread.Join</c>.
/// </para>
/// </remarks>
[Collection(UiThreadPoolBlockingGroup.Name)]
public sealed class UiThreadPoolBlockingTests(ITestOutputHelper output)
{
    private const int Tests = 8;

    [Fact]
    public async Task El_pool_cuenta_como_bloqueado_el_hilo_que_espera_a_la_prueba_de_interfaz()
    {
        Func<int> blockedThreads = BlockedThreadsReader();
        int before = blockedThreads();
        int started = 0;
        TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] runs = [.. Enumerable.Range(0, Tests).Select(_ => Task.Run(() => UiThread.Run(async () =>
        {
            if (Interlocked.Increment(ref started) == Tests)
            {
                allStarted.TrySetResult();
            }

            await release.Task;
        })))];
        int most = before;
        bool counted;
        try
        {
            // Topes contra cuelgues, no medidas: en verde sale en cuanto los hilos que esperan pasan de girar a bloquearse
            // (Task.Wait gira un poco antes de avisar al pool). Con Thread.Join la cuenta no sube y agota el tope.
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(120));
            using var cap = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                most = Math.Max(most, blockedThreads());
                counted = most >= before + Tests;
                if (counted || cap.IsCancellationRequested)
                {
                    break;
                }

                await Task.Delay(1);
            }
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(120));
        output.WriteLine($"{Tests} pruebas de interfaz bloqueadas; el pool contaba {most} hilos bloqueados (antes, {before}). {UiThread.PoolState()}");
        Assert.True(counted,
            $"Con {Tests} pruebas de interfaz bloqueadas el pool contaba {most} hilos bloqueados (antes, {before}): "
            + $"UiThread.Run bloquea sin que el pool lo sepa (¿volvió Thread.Join?). {UiThread.PoolState()}");
    }

    /// <summary>
    /// Lector de la cuenta de hilos bloqueados del pool portátil de .NET (el de Windows y Linux mientras no se active
    /// <c>System.Threading.ThreadPool.UseWindowsThreadPool</c>), que suben <c>NotifyThreadBlocked</c> y bajan
    /// <c>NotifyThreadUnblocked</c>.
    /// </summary>
    private static Func<int> BlockedThreadsReader()
    {
        string? notPortable = NonPortablePoolReason();
        Assert.True(notPortable is null,
            $"La cuenta de hilos bloqueados no sirve aquí: {notPortable}. Con ese interruptor nadie suma a "
            + "PortableThreadPool._numBlockedThreads y la prueba culparía a Thread.Join en falso; quítalo para correrla.");
        const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
        Type? pool = typeof(ThreadPool).Assembly.GetType("System.Threading.PortableThreadPool");
        object? instance = pool?.GetField("ThreadPoolInstance", Static)?.GetValue(null);
        FieldInfo? blocked = pool?.GetField("_numBlockedThreads", Instance);
        Assert.True(instance is not null && blocked is not null,
            "El runtime ya no tiene PortableThreadPool.ThreadPoolInstance._numBlockedThreads (tipo: "
            + $"{(pool is null ? "no está" : "está")}; instancia: {(instance is null ? "no está" : "está")}; campo: "
            + $"{(blocked is null ? "no está" : "está")}): hay que buscar otra forma de ver si el pool cuenta los hilos "
            + $"bloqueados (runtime {Environment.Version}).");
        return () => Convert.ToInt32(blocked!.GetValue(instance), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Por qué el pool no llevaría la cuenta de bloqueados, o <see langword="null"/> si la lleva. Con el pool de Windows
    /// (<c>System.Threading.ThreadPool.UseWindowsThreadPool</c> o <c>DOTNET_ThreadPool_UseWindowsThreadPool</c>) el pool
    /// portátil existe pero no trabaja; con <c>System.Threading.ThreadPool.Blocking.CooperativeBlocking</c> en falso,
    /// <c>NotifyThreadBlocked</c> sale sin sumar. En los dos casos la cuenta se queda quieta aunque se use Task.Wait.
    /// </summary>
    private static string? NonPortablePoolReason()
    {
        const string WindowsPool = "System.Threading.ThreadPool.UseWindowsThreadPool";
        const string WindowsPoolVariable = "DOTNET_ThreadPool_UseWindowsThreadPool";
        const string Cooperative = "System.Threading.ThreadPool.Blocking.CooperativeBlocking";
        string? variable = Environment.GetEnvironmentVariable(WindowsPoolVariable);
        if ((AppContext.TryGetSwitch(WindowsPool, out bool windows) && windows)
            || variable == "1" || string.Equals(variable, "true", StringComparison.OrdinalIgnoreCase))
        {
            return $"usa el pool de hilos de Windows ({WindowsPool} o {WindowsPoolVariable})";
        }

        return AppContext.TryGetSwitch(Cooperative, out bool cooperative) && !cooperative
            ? $"el bloqueo cooperativo está apagado ({Cooperative} = false)"
            : null;
    }
}
