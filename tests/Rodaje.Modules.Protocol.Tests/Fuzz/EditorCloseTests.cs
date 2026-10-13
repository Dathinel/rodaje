using System.IO.Pipes;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Regresión del cuelgue de la tarea 137 (<c>Cancel_racing_the_end_of_a_call_does_not_close_the_connection</c> en la CI
/// de Windows: «RunAsync no terminó en 10 s» con la CPU casi parada). El diagnóstico de <see cref="PendingAwaits"/> mostró
/// a <c>ModuleHost.RunAsync</c> esperando una lectura de la tubería que no se completaba nunca, con el asa del editor ya
/// cerrada y la escritura del módulo fallando con «Pipe is broken». Pasa cuando el editor cierra el lado servidor con
/// una lectura propia pendiente (el bucle de lectura de <see cref="ModuleClient"/>) y datos que el módulo aún no leyó.
/// <c>MessageChannel.Abort</c> (el corte por fallo de <see cref="ModuleClient"/>) desconecta antes de cerrar. Sin eso,
/// en la CI de Windows y con la CPU llena colgaba 2 de cada 9 rondas; con eso, ninguna. En Linux la tubería es un socket y no se cuelga: allí la prueba pasa siempre.
/// </summary>
[Collection(FuzzGroup.Name)]
public sealed class EditorCloseTests
{
    private const string Hello = """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""";

    /// <summary>Rondas como mucho; con el arreglo cada una tarda décimas de segundo.</summary>
    private const int Rounds = 40;

    [Fact]
    public async Task The_module_sees_the_end_when_the_editor_closes_with_a_pending_read_and_unread_data()
    {
        // La CPU llena con hilos propios (no del pool), como en la CI cuando corren a la vez otros proyectos: así el
        // cierre cae a menudo con el módulo a medio leer la ráfaga.
        using CancellationTokenSource burn = new();
        List<Thread> burners = [];
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            Thread burner = new(() =>
            {
                while (!burn.IsCancellationRequested) Thread.SpinWait(1000);
            }) { IsBackground = true, Name = "Carga de CPU" };
            burner.Start();
            burners.Add(burner);
        }
        try
        {
            await FuzzSupport.RunCasesAsync(Rounds, RoundAsync, first: 137_000, parallel: 2);
        }
        finally
        {
            await burn.CancelAsync();
            foreach (Thread burner in burners) burner.Join();
        }
    }

    private static async Task RoundAsync(int seed)
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (module)
        {
            Task<bool> host = Support.EchoHost(maxConcurrent: 64).RunAsync(module);
            await editor.WriteAsync(Support.Frame(Hello));
            await FuzzSupport.ReadFrameAsync(editor);

            // El editor como ModuleClient: un bucle de lectura siempre pendiente sobre el canal.
            MessageChannel channel = new(editor);
            Task readLoop = Task.Run(async () =>
            {
                try
                {
                    while (await channel.ReceiveAsync() is not null)
                    {
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or ModuleProtocolException)
                {
                }
            });

            // Más de lo que el módulo alcanza a leer antes del cierre.
            List<byte> burst = [];
            for (int i = 1; i <= 200; i++)
            {
                burst.AddRange(Support.Frame($$"""{"type":"invoke","id":{{i}},"function":"eco"}"""));
                burst.AddRange(Support.Frame($$"""{"type":"cancel","id":{{i}}}"""));
            }
            await editor.WriteAsync(burst.ToArray());
            await Task.Delay(new Random(seed).Next(0, 20));

            channel.Abort();
            await FuzzSupport.Capped(host, $"RunAsync tras cerrar el editor (semilla {seed})");
            await FuzzSupport.Capped(readLoop, $"el bucle de lectura del editor (semilla {seed})");
        }
    }
}
