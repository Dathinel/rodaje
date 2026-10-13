using System.IO.Pipes;
using System.Text;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Tarea 211: una llamada salía de <c>active</c> al terminar su función y después escribía su respuesta. Si en ese
/// hueco llegaba <c>shutdown</c> (o el editor cerraba), <c>ModuleHost.RunAsync</c> solo esperaba a lo que quedaba en
/// <c>active</c>, volvía y desechaba el canal: la respuesta final se perdía en silencio. En la CI de Windows lo destapó
/// <c>HostileMessageTests</c> con la semilla 21261124 («La llamada 2 tuvo 0 respuestas finales»); en Linux, una llamada
/// a «fallar» seguida de <c>shutdown</c> en la misma escritura perdía la respuesta en 133 a 166 de cada 1000 rondas, y
/// en 45 a 162 con el código de antes de la tarea 195 (#193), que solo cambió el hilo en que corre la función.
/// </summary>
[Collection(FuzzGroup.Name)]
public sealed class ReplyRaceTests
{
    private const string Hello = """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""";

    /// <summary>
    /// Determinista: la costura detiene la llamada justo en el hueco (id libre, respuesta sin escribir) y entonces llega
    /// <c>shutdown</c>. El cierre tiene que esperar a la respuesta, y la respuesta tiene que llegar.
    /// </summary>
    [Fact]
    public async Task Shutdown_entre_el_fin_de_la_funcion_y_la_respuesta_no_pierde_la_respuesta()
    {
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModuleHost host = new(Support.EchoInfo) { BeforeReplyForTests = async _ => { reached.TrySetResult(); await gate.Task; } };
        host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));

        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            Task<bool> run = host.RunAsync(module);
            await editor.WriteAsync(Support.Frame(Hello));
            await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el welcome");
            Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
            await editor.WriteAsync(Support.Frame("""{"type":"invoke","id":2,"function":"eco"}"""));
            await FuzzSupport.Capped(reached.Task, "la llamada");
            await editor.WriteAsync(Support.Frame("""{"type":"shutdown"}"""));

            // Sin el arreglo, RunAsync vuelve enseguida y cierra el canal con la respuesta sin escribir.
            await Task.WhenAny(run, Task.Delay(500));
            bool closedEarly = run.IsCompleted;
            gate.SetResult();
            Assert.True(await FuzzSupport.Capped(run, "RunAsync"));
            module.Dispose();
            List<string> replies = [.. (await FuzzSupport.Capped(drain, "la lectura")).Select(r => Encoding.UTF8.GetString(r))];

            Assert.False(closedEarly, "RunAsync terminó sin esperar la respuesta de una llamada que ya había salido de «active».");
            ResultMessage result = Assert.IsType<ResultMessage>(MessageChannel.Parse(Encoding.UTF8.GetBytes(Assert.Single(replies))));
            Assert.Equal(2, result.Id);
        }
    }

    /// <summary>Sin costura: una llamada que termina al instante y <c>shutdown</c> detrás, en la misma escritura.</summary>
    [Fact]
    public async Task Una_llamada_y_shutdown_seguidos_siempre_tienen_respuesta()
    {
        int lost = 0;
        for (int round = 0; round < 300; round++)
        {
            (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
            using (editor)
            using (module)
            {
                Task<bool> run = Support.EchoHost(maxConcurrent: 64).RunAsync(module);
                await editor.WriteAsync(Support.Frame(Hello));
                await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el welcome");
                Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
                byte[] burst = [.. Support.Frame("""{"type":"invoke","id":2,"function":"fallar"}"""), .. Support.Frame("""{"type":"shutdown"}""")];
                await editor.WriteAsync(burst);
                Assert.True(await FuzzSupport.Capped(run, "RunAsync"));
                module.Dispose();
                List<byte[]> replies = await FuzzSupport.Capped(drain, "la lectura");
                if (!replies.Any(r => MessageChannel.Parse(r) is ErrorMessage { Id: 2 })) lost++;
            }
        }
        Assert.True(lost == 0, $"{lost} de 300 llamadas quedaron sin respuesta final.");
    }
}
