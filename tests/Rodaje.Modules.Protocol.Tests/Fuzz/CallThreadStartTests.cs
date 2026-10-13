using System.IO.Pipes;
using System.Text;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Tarea 235: <c>ModuleHost</c> corre cada llamada en un hilo propio (tarea 195) y lo arrancaba desde su bucle de lectura.
/// <see cref="Thread.Start()"/> es síncrono: el runtime crea el hilo del sistema y espera, cediendo el procesador, a que el
/// hilo nuevo se dé por arrancado. Mientras, el bucle no leía nada más. La CI de Windows lo vio en
/// <c>ProtocolDefectTests.Shutdown_with_calls_finishing_ends_the_host_without_throwing</c>: «RunAsync (ronda 67) no terminó
/// en 10 s», 0,0 s de CPU, 0 trabajos en cola y la máquina de estados de <c>RunAsync</c> en el estado -1 (corriendo, no en
/// un <c>await</c>) con una llamada en <c>active</c> y en <c>unanswered</c>: justo después de registrarla y antes de que
/// su hilo arrancara. El <c>shutdown</c> que venía detrás no se leía. La costura hace de ese hilo que tarda en arrancar.
/// </summary>
[Collection(FuzzGroup.Name)]
public sealed class CallThreadStartTests
{
    private const string Hello = """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""";

    [Fact]
    public async Task A_call_thread_slow_to_start_does_not_stop_the_loop_nor_the_shutdown()
    {
        using ManualResetEventSlim reached = new();
        using ManualResetEventSlim gate = new();
        ModuleHost host = new(Support.EchoInfo)
        {
            // Como Thread.Start esperando a un hilo que no arranca: bloquea el hilo que lo pide.
            BeforeCallThreadStartsForTests = _ =>
            {
                reached.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
            },
        };
        host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));

        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            try
            {
                Task<bool> run = host.RunAsync(module);
                await editor.WriteAsync(Support.Frame(Hello));
                await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el welcome");
                await editor.WriteAsync((byte[])[.. Support.Frame("""{"type":"invoke","id":1,"function":"eco"}"""), .. Support.Frame("""{"type":"ping","id":7}""")]);
                Assert.True(reached.Wait(FuzzSupport.CaseCap), "La llamada no llegó a arrancar su hilo.");

                // El bucle sigue atendiendo mientras el hilo de la llamada no arranca.
                byte[]? pong = await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el pong con un hilo de llamada sin arrancar");
                Assert.NotNull(pong);
                Assert.Equal(7, Assert.IsType<PongMessage>(MessageChannel.Parse(pong)).Id);

                // Y el «shutdown» termina RunAsync: el cierre espera a la llamada con su plazo y sale.
                await editor.WriteAsync(Support.Frame("""{"type":"shutdown"}"""));
                Assert.True(await FuzzSupport.Capped(run, "RunAsync con un hilo de llamada sin arrancar"));
            }
            finally
            {
                gate.Set();
            }
        }
    }

    /// <summary>Cuando el hilo arranca, la llamada se contesta como siempre.</summary>
    [Fact]
    public async Task A_call_whose_thread_starts_late_is_still_answered()
    {
        using ManualResetEventSlim gate = new();
        ModuleHost host = new(Support.EchoInfo) { BeforeCallThreadStartsForTests = _ => gate.Wait(TimeSpan.FromSeconds(30)) };
        host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));

        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            try
            {
                Task<bool> run = host.RunAsync(module);
                await editor.WriteAsync(Support.Frame(Hello));
                await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el welcome");
                await editor.WriteAsync((byte[])[.. Support.Frame("""{"type":"invoke","id":3,"function":"eco","parameters":"hola"}"""), .. Support.Frame("""{"type":"ping","id":8}""")]);
                byte[]? pong = await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el pong");
                Assert.IsType<PongMessage>(MessageChannel.Parse(pong!));
                gate.Set();
                byte[]? reply = await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "la respuesta de la llamada");
                ResultMessage result = Assert.IsType<ResultMessage>(MessageChannel.Parse(reply!));
                Assert.Equal(3, result.Id);
                Assert.Equal("hola", result.Value?.GetString());
                MessageChannel.Close(editor);
                Assert.True(await FuzzSupport.Capped(run, "RunAsync"));
            }
            finally
            {
                gate.Set();
            }
        }
    }
}
