using System.Buffers.Binary;
using System.IO.Pipes;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Tarea 260, regresión de #233 (tarea 235): el hilo propio de cada llamada se arrancaba en un trabajo del pool, y
/// <see cref="Thread.Start()"/> retiene al hilo que lo pide hasta que el hilo nuevo arranca. En la CI de Windows, con la CPU
/// llena, <c>EditorCloseTests</c> se colgó («RunAsync tras cerrar el editor… 46 hilos y 249 trabajos en cola») con
/// <c>active</c> y <c>unanswered</c> en 64: el pool estaba lleno de arranques en espera y detrás, en la cola, quedaban la
/// continuación de la lectura de la tubería (el fin de flujo del editor) y el plazo de 2 s del cierre. Aquí la costura hace
/// de arranques que no terminan, con más llamadas que hilos tiene el pool. El lado del editor usa E/S síncrona y la espera
/// del anfitrión es bloqueante a propósito: así la prueba informa el cuelgue aunque el pool esté sin hilos.
/// </summary>
[Collection(FuzzGroup.Name)]
public sealed class CallStartPoolTests
{
    private const string Hello = """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""";

    /// <summary>Muchas más que los hilos del pool (32 como mínimo, véase el .csproj), así que no depende de cuántos inyecte.</summary>
    private const int Calls = 120;

    [Fact]
    public void Shutdown_ends_the_host_while_call_threads_are_slow_to_start() => Run(byShutdown: true);

    [Fact]
    public void The_editor_closing_ends_the_host_while_call_threads_are_slow_to_start() => Run(byShutdown: false);

    private static void Run(bool byShutdown)
    {
        StartGate gate = new();
        ModuleHost host = new(Support.EchoInfo)
        {
            MaxConcurrentCalls = Calls,
            // Como Thread.Start esperando a un hilo que no arranca: retiene al hilo que lo pide.
            BeforeCallThreadStartsForTests = _ => gate.Wait(),
        };
        host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));

        (NamedPipeServerStream editor, NamedPipeClientStream module) = Support.PipePairAsync().GetAwaiter().GetResult();
        using (editor)
        using (module)
        {
            try
            {
                Task<bool> run = host.RunAsync(module);
                Write(editor, Support.Frame(Hello));
                ReadFrame(editor);
                List<byte> burst = [];
                for (int i = 1; i <= Calls; i++) burst.AddRange(Support.Frame($$"""{"type":"invoke","id":{{i}},"function":"eco"}"""));
                if (byShutdown) burst.AddRange(Support.Frame("""{"type":"shutdown"}"""));
                Write(editor, burst.ToArray());
                if (!byShutdown)
                {
                    // Que el módulo lea la ráfaga antes del cierre: el fin de flujo llega con las llamadas ya registradas.
                    Thread.Sleep(200);
                    MessageChannel.Close(editor);
                }

                // Espera bloqueante y no Task.WaitAsync: su plazo tampoco corre con el pool sin hilos.
                string what = byShutdown ? "tras «shutdown»" : "tras cerrar el editor";
                Assert.True(run.Wait(FuzzSupport.CaseCap),
                    $"Colgado: RunAsync {what} no terminó en {FuzzSupport.CaseCap.TotalSeconds:0} s con los hilos de las llamadas sin arrancar. Espera pendiente:{PendingAwaits.Describe(run)}");
                Assert.True(run.Result);
            }
            finally
            {
                gate.Open();
            }
        }
    }

    private static void Write(Stream stream, byte[] data)
    {
        stream.Write(data);
        stream.Flush();
    }

    /// <summary>Lee un marco con E/S síncrona; el <c>welcome</c> llega antes de que haya ninguna llamada.</summary>
    private static void ReadFrame(Stream stream)
    {
        byte[] header = new byte[4];
        stream.ReadExactly(header);
        byte[] payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        stream.ReadExactly(payload);
    }
}

/// <summary>
/// Una puerta para la costura de arranque: el hilo que la cruza espera hasta que la prueba la abre. Sin nada que desechar,
/// porque el hilo de arranque del anfitrión puede cruzarla después de que la prueba termine.
/// </summary>
internal sealed class StartGate
{
    private readonly object _lock = new();
    private bool _open;

    public void Wait()
    {
        lock (_lock)
        {
            // Con plazo: si algo sale mal, el hilo de arranque no queda esperando para siempre.
            while (!_open && Monitor.Wait(_lock, TimeSpan.FromSeconds(30)))
            {
            }
        }
    }

    public void Open()
    {
        lock (_lock)
        {
            _open = true;
            Monitor.PulseAll(_lock);
        }
    }
}
