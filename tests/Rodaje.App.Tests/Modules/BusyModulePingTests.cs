using System.Collections;
using System.IO.Pipes;
using System.Reflection;
using System.Threading.Channels;
using Rodaje.App.Services.Modules;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Tests.Modules;

/// <summary>
/// El vigilante de <c>ping</c> distingue un módulo ocupado de uno colgado. En la CI de Windows, con el runner cargado,
/// «RAW de cine» revelaba CinemaDNG y avisaba su avance pero su <c>pong</c> llegaba después del tope: se mataba un módulo
/// sano a mitad de trabajo (<see cref="ModuleHungException"/>). Ahora cualquier mensaje del módulo durante la espera cuenta
/// como señal de vida; solo una ventana entera sin <c>pong</c> ni ningún otro mensaje lo da por colgado.
/// </summary>
/// <remarks>
/// Sin reloj: las ventanas de espera (<c>PingTimeout</c>) las abre y las vence la prueba. <c>ModuleConnection</c> es
/// interno de Rodaje.App, que no expone sus internos a las pruebas: <c>PingRoundAsync</c> se toma por reflexión. Las dos
/// pruebas del cableado (el vigilante de verdad con el módulo eco) arrancan procesos: la clase corre con
/// <see cref="ModuleProcessGroup"/>.
/// </remarks>
[Collection(ModuleProcessGroup.Name)]
public sealed class BusyModulePingTests : IDisposable
{
    private const string LongCount = """{"pasos":100000,"msPorPaso":10}""";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly EchoModules _modules = new EchoModules().Add("eco-a");

    private static readonly Func<Func<Task>, Func<long>, Func<Task>, Task<bool>> PingRound =
        (typeof(ModuleManager).Assembly.GetType("Rodaje.App.Services.Modules.ModuleConnection", throwOnError: true)!
            .GetMethod("PingRoundAsync", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException("ModuleConnection", "PingRoundAsync"))
        .CreateDelegate<Func<Func<Task>, Func<long>, Func<Task>, Task<bool>>>();

    public void Dispose() => _modules.Dispose();

    [Fact]
    public async Task Un_modulo_que_avisa_avance_sin_contestar_el_ping_a_tiempo_no_se_mata()
    {
        Watch watch = new();
        Task<bool> round = watch.Start();

        // Cinco ventanas vencidas sin pong, pero en cada una llegó un aviso de avance: sigue vivo.
        for (int i = 0; i < 5; i++)
        {
            TaskCompletionSource window = await watch.NextWindowAsync();
            watch.Activity();
            window.SetResult();
        }

        await watch.NextWindowAsync();
        Assert.False(round.IsCompleted, "Se dio por colgado a un módulo que seguía avisando su avance.");

        // El pong atrasado llega al fin: la vuelta termina bien.
        watch.Pong.SetResult();
        Assert.True(await round.WaitAsync(Guard));
        Assert.Equal(6, watch.Windows); // Las cinco vencidas y la última: con el pong no se abre otra.
    }

    [Fact]
    public async Task Un_modulo_que_no_avanza_ni_contesta_se_da_por_colgado()
    {
        Watch watch = new();
        Task<bool> round = watch.Start();
        (await watch.NextWindowAsync()).SetResult();
        Assert.False(await round.WaitAsync(Guard));
    }

    [Fact]
    public async Task Un_modulo_que_avanzo_y_despues_se_quedo_quieto_se_da_por_colgado_en_la_ventana_siguiente()
    {
        Watch watch = new();
        Task<bool> round = watch.Start();
        TaskCompletionSource first = await watch.NextWindowAsync();
        watch.Activity();
        first.SetResult();
        (await watch.NextWindowAsync()).SetResult();
        Assert.False(await round.WaitAsync(Guard));
    }

    [Fact]
    public async Task Un_pong_a_tiempo_no_espera_la_ventana()
    {
        Watch watch = new();
        Task<bool> round = watch.Start();
        await watch.NextWindowAsync();
        watch.Pong.SetResult();
        Assert.True(await round.WaitAsync(Guard));
        Assert.Equal(1, watch.Windows); // La ventana abierta no venció y no se abrió otra.
    }

    [Fact]
    public async Task Si_la_conexion_se_corta_el_ping_falla_y_no_se_toma_por_colgado()
    {
        Watch watch = new();
        Task<bool> round = watch.Start();
        await watch.NextWindowAsync();
        watch.Pong.SetException(new ModuleDisconnectedException("El módulo cerró la conexión."));
        await Assert.ThrowsAsync<ModuleDisconnectedException>(() => round.WaitAsync(Guard));
    }

    /// <summary>La señal de vida de verdad: cada aviso de avance que manda el módulo suma en <see cref="ModuleClient.ReceivedMessages"/>.</summary>
    [Fact]
    public async Task Los_avisos_de_avance_cuentan_como_mensajes_recibidos()
    {
        ModuleHost host = new(new ModuleInfo("prueba-ocupado", "1.0.0", "Prueba ocupado", [new FunctionInfo("trabajar", "Trabajar")]));
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Register("trabajar", async (call, ct) =>
        {
            await call.ReportProgressAsync(0.25, "uno", ct);
            await call.ReportProgressAsync(0.5, "dos", ct);
            await finish.Task.WaitAsync(ct);
            return new ModuleOutput();
        });

        string name = "Rodaje.Mod.prueba." + Guid.NewGuid().ToString("N");
        NamedPipeServerStream editor = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        NamedPipeClientStream module = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Task.WhenAll(editor.WaitForConnectionAsync(), module.ConnectAsync());
        Task<bool> run = host.RunAsync(module);
        ModuleClient client = await ModuleClient.ConnectAsync(editor);
        try
        {
            long before = client.ReceivedMessages;
            Assert.Equal(0, before);
            TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<ModuleResult> call = client.InvokeAsync("trabajar", progress: new Signal(p =>
            {
                if (p.Stage == "dos")
                {
                    second.TrySetResult();
                }
            }));

            // El contador sube antes de despachar el mensaje: al ver el segundo aviso ya cuenta los dos.
            await second.Task.WaitAsync(Guard);
            Assert.True(client.ReceivedMessages >= 2, $"{client.ReceivedMessages} mensajes");

            finish.SetResult();
            await call.WaitAsync(Guard);
            Assert.True(client.ReceivedMessages >= 3, $"{client.ReceivedMessages} mensajes");
        }
        finally
        {
            await client.DisposeAsync();
            await run.WaitAsync(Guard);
            await module.DisposeAsync();
        }
    }

    /// <summary>
    /// El cableado de verdad (<c>WatchAsync</c>): el vigilante cuenta como señal de vida los mensajes que llegan al cliente
    /// (<see cref="ModuleClient.ReceivedMessages"/>) y espera por ventanas de <c>PingTimeout</c>. El módulo eco avisa su
    /// avance cada 10 ms y la prueba retiene su <c>pong</c> hasta que vencieron al menos dos ventanas: si la actividad
    /// fuera una constante, la primera ventana vencida (antes que el <c>pong</c>, así que <c>WhenAny</c> la elige a ella sea
    /// cual sea el orden en que despierte el vigilante) lo daría por colgado.
    /// </summary>
    [Fact]
    public async Task El_vigilante_no_mata_a_un_modulo_que_avisa_avance_aunque_su_pong_se_atrase_varias_ventanas()
    {
        TimeSpan pingTimeout = TimeSpan.FromSeconds(2);
        await using ModuleManager manager = _modules.Manager(new ModuleManagerOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(50),
            // Holgado: con avances cada 10 ms, ninguna ventana debería quedarse sin mensajes ni con el runner cargado.
            PingTimeout = pingTimeout,
        });
        await manager.InvokeAsync("eco-a", "eco", null, "Abrir");
        int pid = Assert.Single(manager.ProcessIds);
        object connection = Connection(manager, "eco-a");

        TaskCompletionSource firstPing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondPing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource pong = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int pings = 0;
        SetProperty(connection, "SendPing", (Func<ModuleClient, Task>)(client =>
        {
            switch (Interlocked.Increment(ref pings))
            {
                case 1:
                    firstPing.SetResult();
                    return HeldPingAsync(client, pong.Task);
                case 2:
                    secondPing.SetResult();
                    break;
            }

            return client.PingAsync();
        }));

        Task<ModuleResult> call = manager.InvokeAsync("eco-a", "contar", EchoModules.Json(LongCount), "Contar");
        ModuleJob job = manager.Jobs.Jobs[0]; // La más nueva va primero.
        await firstPing.Task.WaitAsync(Guard);

        // Espera mínima, no cota: empieza después de mandar el ping, así que al menos dos ventanas vencen sin pong.
        await Task.Delay(pingTimeout * 2.5);
        pong.SetResult();

        // Puerta: el vigilante solo manda el segundo ping si la primera vuelta terminó bien. Si lo dio por colgado, la
        // llamada termina antes (con ModuleHungException) y el segundo ping no llega nunca.
        Task first = await Task.WhenAny(secondPing.Task, call).WaitAsync(TimeSpan.FromSeconds(60));
        if (first == call)
        {
            await call; // Muestra el fallo.
            Assert.Fail("La llamada terminó sola antes del segundo ping.");
        }

        Assert.Null(GetField(connection, "_hungProcess"));
        job.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Guard));
        Assert.Null(GetField(connection, "_hungProcess"));
        Assert.Equal(pid, Assert.Single(manager.ProcessIds));
    }

    /// <summary>
    /// La gemela: un módulo que no manda nada («colgar») y cuyo <c>pong</c> no llega nunca sí se da por colgado y se mata.
    /// No depende del reloj: basta con que venza una ventana (el tope es contra cuelgues de la prueba).
    /// </summary>
    [Fact]
    public async Task El_vigilante_mata_a_un_modulo_que_no_manda_nada_ni_contesta_el_ping()
    {
        await using ModuleManager manager = _modules.Manager(new ModuleManagerOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(50),
            PingTimeout = TimeSpan.FromMilliseconds(500),
        });
        await manager.InvokeAsync("eco-a", "eco", null, "Abrir");
        int pid = Assert.Single(manager.ProcessIds);
        object connection = Connection(manager, "eco-a");
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SetProperty(connection, "SendPing", (Func<ModuleClient, Task>)(_ => never.Task));

        Task<ModuleResult> call = manager.InvokeAsync("eco-a", "colgar", null, "Colgar");
        await Assert.ThrowsAsync<ModuleHungException>(() => call.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.NotNull(GetField(connection, "_hungProcess"));
        Assert.True(await EchoModules.HasExitedAsync(pid, TimeSpan.FromSeconds(30)));
    }

    /// <summary>Un <c>ping</c> que sale cuando la prueba suelta <paramref name="release"/>: el <c>pong</c> llega después.</summary>
    private static async Task HeldPingAsync(ModuleClient client, Task release)
    {
        await release.ConfigureAwait(false);
        await client.PingAsync().ConfigureAwait(false);
    }

    private static object Connection(ModuleManager manager, string moduleId) =>
        ((IDictionary)GetField(manager, "_connections")!)[moduleId]!;

    private static object? GetField(object target, string name) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(target.GetType().Name, name))
            .GetValue(target);

    private static void SetProperty(object target, string name, object value) =>
        (target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMemberException(target.GetType().Name, name))
            .SetValue(target, value);

    /// <summary>Un vigilante con el <c>pong</c>, la actividad y las ventanas en manos de la prueba.</summary>
    private sealed class Watch
    {
        private readonly Channel<TaskCompletionSource> _windows = Channel.CreateUnbounded<TaskCompletionSource>();
        private long _activity;
        private int _opened;

        public TaskCompletionSource Pong { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Start() => PingRound(() => Pong.Task, () => Interlocked.Read(ref _activity), () =>
        {
            TaskCompletionSource window = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref _opened);
            Assert.True(_windows.Writer.TryWrite(window));
            return window.Task;
        });

        /// <summary>Las ventanas que abrió el vigilante.</summary>
        public int Windows => Volatile.Read(ref _opened);

        public void Activity() => Interlocked.Increment(ref _activity);

        /// <summary>La ventana que el vigilante acaba de abrir (espera a que la abra).</summary>
        public async Task<TaskCompletionSource> NextWindowAsync() => await _windows.Reader.ReadAsync().AsTask().WaitAsync(Guard);
    }

    /// <summary>Un <see cref="IProgress{T}"/> que avisa en el mismo hilo (<see cref="Progress{T}"/> lo manda al contexto).</summary>
    private sealed class Signal(Action<ModuleProgress> report) : IProgress<ModuleProgress>
    {
        public void Report(ModuleProgress value) => report(value);
    }
}
