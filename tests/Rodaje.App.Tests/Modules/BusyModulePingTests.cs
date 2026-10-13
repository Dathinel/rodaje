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
/// interno de Rodaje.App, que no expone sus internos a las pruebas: <c>PingRoundAsync</c> se toma por reflexión.
/// </remarks>
public sealed class BusyModulePingTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static readonly Func<Func<Task>, Func<long>, Func<Task>, Task<bool>> PingRound =
        (typeof(ModuleManager).Assembly.GetType("Rodaje.App.Services.Modules.ModuleConnection", throwOnError: true)!
            .GetMethod("PingRoundAsync", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException("ModuleConnection", "PingRoundAsync"))
        .CreateDelegate<Func<Func<Task>, Func<long>, Func<Task>, Task<bool>>>();

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

        TaskCompletionSource last = await watch.NextWindowAsync();
        Assert.False(round.IsCompleted, "Se dio por colgado a un módulo que seguía avisando su avance.");

        // El pong atrasado llega al fin: la vuelta termina bien.
        watch.Pong.SetResult();
        Assert.True(await round.WaitAsync(Guard));
        Assert.False(last.Task.IsCompleted);
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
        TaskCompletionSource window = await watch.NextWindowAsync();
        watch.Pong.SetResult();
        Assert.True(await round.WaitAsync(Guard));
        Assert.False(window.Task.IsCompleted);
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

    /// <summary>Un vigilante con el <c>pong</c>, la actividad y las ventanas en manos de la prueba.</summary>
    private sealed class Watch
    {
        private readonly Channel<TaskCompletionSource> _windows = Channel.CreateUnbounded<TaskCompletionSource>();
        private long _activity;

        public TaskCompletionSource Pong { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Start() => PingRound(() => Pong.Task, () => Interlocked.Read(ref _activity), () =>
        {
            TaskCompletionSource window = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_windows.Writer.TryWrite(window));
            return window.Task;
        });

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
