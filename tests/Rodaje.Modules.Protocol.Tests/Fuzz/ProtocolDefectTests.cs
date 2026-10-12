using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Defectos de <c>src/Rodaje.Modules.Protocol</c> que encontró el fuzz (tarea 25). Cada prueba reproduce uno y dice lo
/// que debería pasar; las de los defectos sin corregir están omitidas. La del defecto 6 ya es de regresión. Todas tienen tope de
/// tiempo: al quitar el <c>Skip</c> con el defecto aún presente, fallan en vez de colgarse.
/// </summary>
[Collection(FuzzGroup.Name)]
public sealed class ProtocolDefectTests
{
    private const string Hello = """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""";

    /// <summary>
    /// <c>ModuleHost.RunAsync</c> atiende <c>cancel</c> con <c>active.TryGetValue</c> y luego <c>call.Cts.CancelAsync()</c>,
    /// pero la llamada, al terminar, hace <c>active.TryRemove</c> y <c>cts.Dispose()</c> en su propio hilo. Si el
    /// <c>cancel</c> llega justo cuando la llamada termina, <c>CancelAsync</c> lanza <see cref="ObjectDisposedException"/>,
    /// que <c>RunAsync</c> toma como «el editor cerró»: devuelve <c>true</c> y corta la conexión con todas las llamadas en
    /// curso. Le puede pasar a un <see cref="ModuleClient"/> normal que cancela una llamada que estaba por terminar.
    /// Se reprodujo en Linux en 3 a 12 de cada 200 rondas.
    /// </summary>
    [Fact(Skip = "Defecto: ModuleHost cierra la conexión si un cancel llega mientras la llamada termina (CancelAsync sobre un CancellationTokenSource ya desechado).")]
    public async Task Cancel_racing_the_end_of_a_call_does_not_close_the_connection()
    {
        for (int round = 0; round < 200; round++)
        {
            (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
            using (editor)
            using (module)
            {
                Task<bool> host = Support.EchoHost(maxConcurrent: 64).RunAsync(module);
                await editor.WriteAsync(Support.Frame(Hello));
                await FuzzSupport.ReadFrameAsync(editor);
                Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
                List<byte> burst = [];
                for (int i = 1; i <= 200; i++)
                {
                    burst.AddRange(Support.Frame($$"""{"type":"invoke","id":{{i}},"function":"eco"}"""));
                    burst.AddRange(Support.Frame($$"""{"type":"cancel","id":{{i}}}"""));
                }
                await editor.WriteAsync(burst.ToArray());
                await Task.Delay(20);
                Assert.False(host.IsCompleted, $"Ronda {round}: el módulo cortó la conexión sin que el editor cerrara.");
                editor.Dispose();
                await FuzzSupport.Capped(host, "RunAsync");
                await FuzzSupport.Capped(drain, "la lectura");
            }
        }
    }

    /// <summary>
    /// Al terminar (por <c>shutdown</c> o porque el editor cerró), el <c>finally</c> de <c>ModuleHost.RunAsync</c> recorre
    /// <c>active</c> y llama a <c>CancelAsync</c> de cada llamada; si una acaba de terminar y ya desechó su
    /// <c>CancellationTokenSource</c>, la <see cref="ObjectDisposedException"/> sale de <c>RunAsync</c> (y en un módulo de
    /// verdad, de <c>RunAsync(string[])</c>: el proceso termina con una excepción no controlada en vez de con 0). Además
    /// se salta la espera de las demás llamadas y la liberación de las regiones. Se reprodujo en Linux en unas 6 de cada
    /// 300 rondas.
    /// </summary>
    [Fact(Skip = "Defecto: ModuleHost.RunAsync lanza ObjectDisposedException al cerrar si una llamada termina a la vez (CancelAsync en el finally).")]
    public async Task Shutdown_with_calls_finishing_ends_the_host_without_throwing()
    {
        for (int round = 0; round < 300; round++)
        {
            (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
            using (editor)
            using (module)
            {
                Task<bool> host = Support.EchoHost(maxConcurrent: 64).RunAsync(module);
                await editor.WriteAsync(Support.Frame(Hello));
                await FuzzSupport.ReadFrameAsync(editor);
                Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
                List<byte> burst = [];
                for (int i = 1; i <= 60; i++) burst.AddRange(Support.Frame($$"""{"type":"invoke","id":{{i}},"function":"eco"}"""));
                burst.AddRange(Support.Frame("""{"type":"shutdown"}"""));
                await editor.WriteAsync(burst.ToArray());
                Assert.True(await FuzzSupport.Capped(host, $"RunAsync (ronda {round})"));
                editor.Dispose();
                await FuzzSupport.Capped(drain, "la lectura");
            }
        }
    }

    /// <summary>
    /// Si la respuesta de una función no cabe en un marco (más de 1 MiB una vez escrita), <c>MessageChannel.SendAsync</c>
    /// lanza <see cref="ModuleProtocolException"/> y <c>ModuleHost.TrySendAsync</c> la traga: no sale ni <c>result</c>
    /// ni <c>error</c>. La llamada del editor espera para siempre (o hasta que la cancele) aunque la conexión siga viva.
    /// Debería llegar un <c>error</c> (por ejemplo <c>internal</c>, «resultado demasiado grande») para esa llamada.
    /// </summary>
    [Fact(Skip = "Defecto: ModuleHost pierde en silencio una respuesta de más de 1 MiB; la llamada del editor no termina nunca.")]
    public async Task A_result_over_the_frame_limit_is_answered_with_an_error()
    {
        ModuleHost host = new(new ModuleInfo("grande", "1.0.0", "Grande", [new FunctionInfo("grande", "Grande")]));
        host.Register("grande", (_, _) => ValueTask.FromResult(new ModuleOutput(Support.Json("\"" + new string('a', 2_000_000) + "\""))));
        (ModuleClient client, _) = await Support.ConnectInProcessAsync(host);
        await using (client)
        {
            ModuleCallException ex = await Assert.ThrowsAsync<ModuleCallException>(() => client.InvokeAsync("grande").WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(ErrorCodes.Internal, ex.Code);
            Assert.True((await client.PingAsync().WaitAsync(TimeSpan.FromSeconds(5))) < TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// <c>MessageChannel.Parse</c> acepta un sustituto UTF-16 suelto escapado (<c>"\ud800"</c>) dentro de <c>parameters</c>
    /// o <c>value</c>, que se guardan como <see cref="JsonElement"/> sin revisar su texto (D-031 pide UTF-8 estricto). Ese
    /// mensaje no se puede volver a escribir: <c>JsonSerializer</c> lanza <see cref="JsonException"/>. Con la función
    /// <c>eco</c>, la respuesta falla al escribirse, la excepción no está entre las que traga <c>TrySendAsync</c>, la
    /// tarea de la llamada termina con error y el editor no recibe ni <c>result</c> ni <c>error</c>. Si el editor cierra
    /// mientras esa llamada sigue en <c>active</c>, la <see cref="JsonException"/> sale además de <c>RunAsync</c> por el
    /// <c>Task.WhenAll</c> del <c>finally</c> (lo halló la corrida larga del fuzz, semilla 22262352). Del lado
    /// del editor, un módulo puede colar el mismo valor en <c>result.value</c> y la app lo recibe como válido. Lo que
    /// debería pasar: <c>Parse</c> lo rechaza como malformado.
    /// </summary>
    [Fact(Skip = "Defecto: Parse acepta un sustituto UTF-16 suelto (\\ud800) en parameters/value; la respuesta de eco se pierde.")]
    public async Task A_lone_surrogate_in_parameters_is_rejected()
    {
        byte[] invoke = Encoding.UTF8.GetBytes("""{"type":"invoke","id":1,"function":"eco","parameters":"\ud800"}""");
        Assert.Throws<ModuleProtocolException>(() => MessageChannel.Parse(invoke));
        byte[] result = Encoding.UTF8.GetBytes("""{"type":"result","id":1,"value":{"x":["\udc00"]}}""");
        Assert.Throws<ModuleProtocolException>(() => MessageChannel.Parse(result));

        // Y el módulo nunca deja una llamada sin contestar: corta por malformado o responde.
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            Task<bool> host = Support.EchoHost().RunAsync(module);
            await editor.WriteAsync(Support.Frame(Hello));
            await FuzzSupport.ReadFrameAsync(editor);
            await editor.WriteAsync(FuzzSupport.Frame(invoke));
            byte[]? reply = await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "la respuesta a la llamada 1");
            Assert.NotNull(reply);
            Assert.Contains("\"error\"", Encoding.UTF8.GetString(reply), StringComparison.Ordinal);
            editor.Dispose();
            await FuzzSupport.Capped(host, "RunAsync");
        }
    }

    /// <summary>
    /// Si el módulo deja de leer la tubería (colgado), las escrituras del editor se bloquean cuando se llena el búfer y
    /// nada las destraba: <c>ModuleClient.SendAsync</c> escribe con <c>_stop.Token</c>, no con el token del llamador, y
    /// nadie llama a <c>Fail</c>. <c>InvokeAsync</c> ignora su cancelación, las demás llamadas esperan el candado de
    /// escritura y <c>DisposeAsync</c> se queda en el <c>shutdown</c>; <c>ModuleProcess.DisposeAsync</c> espera a
    /// <c>Client.DisposeAsync</c> antes de matar el proceso, así que tampoco termina. Se reprodujo en Linux; en Windows el
    /// búfer de la tubería es de 64 KB (<c>ModuleProcess.PipeBufferBytes</c>), así que pasa antes.
    /// </summary>
    [Fact(Skip = "Defecto: un módulo que deja de leer cuelga InvokeAsync (ignora la cancelación) y DisposeAsync del editor.")]
    public async Task A_module_that_stops_reading_does_not_hang_the_editor()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (module)
        {
            Task greet = Task.Run(async () =>
            {
                await FuzzSupport.ReadFrameAsync(module);
                await module.WriteAsync(Support.Frame(Support.WelcomeJson));
            });
            ModuleClient client = await ModuleClient.ConnectAsync(editor);
            await greet;
            JsonElement big = Support.Json("\"" + new string('a', 900_000) + "\"");
            List<Task> calls = [];
            List<CancellationTokenSource> tokens = [];
            for (int i = 0; i < 8; i++)
            {
                tokens.Add(new CancellationTokenSource(500));
                calls.Add(client.InvokeAsync("eco", big, cancellationToken: tokens[^1].Token));
            }
            try
            {
                await FuzzSupport.Capped(Task.WhenAll(calls), "las llamadas canceladas a los 500 ms");
            }
            catch (Exception ex) when (ex is OperationCanceledException or ModuleException)
            {
            }
            foreach (Task call in calls)
                Assert.True(call.Exception?.InnerException is null or OperationCanceledException or ModuleException, call.Exception?.ToString());
            await FuzzSupport.Capped(client.DisposeAsync().AsTask(), "DisposeAsync");
            foreach (CancellationTokenSource cts in tokens) cts.Dispose();
        }
    }

    /// <summary>
    /// Regresión del defecto 6, corregido en <c>MessageChannel.Dispose</c>. Antes, al fallar el cliente, <c>Fail</c>
    /// cancelaba <c>_stop</c> y enseguida desechaba el <see cref="SemaphoreSlim"/> de las escrituras; una llamada o un ping
    /// que esperaba ese candado procesaba su cancelación en otro hilo, el semáforo ya desechado no la sacaba de la cola y
    /// su tarea no terminaba nunca (CI de Windows del PR #27; en Linux, con esta tubería de escrituras detenidas, 45 y 46
    /// de 50 rondas colgadas). Ahora toda operación termina con error tras el fallo.
    /// </summary>
    [Fact]
    public async Task Operations_waiting_to_write_finish_when_the_client_fails()
    {
        for (int round = 0; round < 50; round++)
        {
            (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
            using (module)
            {
                Task greet = Task.Run(async () =>
                {
                    await FuzzSupport.ReadFrameAsync(module);
                    await module.WriteAsync(Support.Frame(Support.WelcomeJson));
                });
                StalledWrites stream = new(editor);
                ModuleClient client = await ModuleClient.ConnectAsync(stream);
                await greet;
                stream.Stall = true;
                using CancellationTokenSource callCts = new(300), pingCts = new(300);
                List<Task> operations = [client.InvokeAsync("eco", cancellationToken: callCts.Token)];
                for (int i = 0; i < 8; i++) operations.Add(client.PingAsync(pingCts.Token));
                await Task.Delay(50);
                // El módulo manda basura: el cliente falla con las operaciones esperando el candado.
                await module.WriteAsync(Support.Frame("[basura"));
                Task all = Task.WhenAll(operations);
                await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(3)));
                Assert.True(all.IsCompleted, $"Ronda {round}: {operations.Count(o => !o.IsCompleted)} operaciones colgadas tras fallar el cliente.");
                foreach (Task operation in operations)
                    Assert.True(operation.Exception?.InnerException is null or OperationCanceledException or ModuleException, operation.Exception?.ToString());
                await FuzzSupport.Capped(client.DisposeAsync().AsTask(), "DisposeAsync");
            }
        }
    }

    /// <summary>Una tubería cuyo búfer de escritura está lleno: las escrituras esperan hasta que se cancelen.</summary>
    private sealed class StalledWrites(Stream inner) : Stream
    {
        public volatile bool Stall;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Stall) await Task.Delay(Timeout.Infinite, cancellationToken);
            await inner.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
