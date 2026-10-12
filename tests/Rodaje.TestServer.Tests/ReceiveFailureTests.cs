using System.Net;

namespace Rodaje.TestServer.Tests;

/// <summary>
/// Fallos al recibir peticiones (<see cref="HttpListener.GetContextAsync"/>), simulados con el receptor interno de
/// <see cref="TestHttpServer.Start(Func{HttpListener, Task{HttpListenerContext}})"/>. El tope es de fallos seguidos: los
/// aislados se anotan en <see cref="TestHttpServer.Faults"/> y el servidor sigue escuchando; al llegar al tope se rinde
/// y lo deja dicho en <see cref="TestHttpServer.Faults"/>.
/// </summary>
public sealed class ReceiveFailureTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Isolated_failures_beyond_the_limit_do_not_stop_the_server()
    {
        // Antes de cada recepción buena, la mitad del tope en fallos: en total más del tope, nunca tantos seguidos.
        var failuresBeforeEach = TestHttpServer.MaxReceiveFailures / 2;
        const int Requests = 4;
        var receiver = new ScriptedReceiver(call => call % (failuresBeforeEach + 1) != failuresBeforeEach);
        await using var server = TestHttpServer.Start(receiver.ReceiveAsync);
        var content = TestPayloads.Deterministic(1000);
        server.Map("/a", TestResponse.File(content));
        using var http = new HttpClient { Timeout = Patience };

        for (var i = 0; i < Requests; i++)
        {
            Assert.Equal(content, await http.GetByteArrayAsync(server.UrlFor("/a")));
        }

        Assert.True(Requests * failuresBeforeEach > TestHttpServer.MaxReceiveFailures);
        var faults = server.Faults;
        // Tras la última petición buena el bucle puede haber anotado ya la tanda siguiente de fallos.
        Assert.InRange(faults.Count, Requests * failuresBeforeEach, (Requests + 1) * failuresBeforeEach);
        Assert.All(faults, f => Assert.IsType<SimulatedReceiveException>(f));
    }

    [Fact]
    public async Task Exactly_the_limit_of_consecutive_failures_keeps_the_server_listening()
    {
        var receiver = new ScriptedReceiver(call => call < TestHttpServer.MaxReceiveFailures);
        await using var server = TestHttpServer.Start(receiver.ReceiveAsync);
        var content = TestPayloads.Deterministic(1000);
        server.Map("/a", TestResponse.File(content));
        using var http = new HttpClient { Timeout = Patience };

        Assert.Equal(content, await http.GetByteArrayAsync(server.UrlFor("/a")));
        Assert.Equal(TestHttpServer.MaxReceiveFailures, server.Faults.Count);
        Assert.All(server.Faults, f => Assert.IsType<SimulatedReceiveException>(f));
    }

    [Fact]
    public async Task One_failure_past_the_limit_stops_listening_and_says_so()
    {
        var receiver = new ScriptedReceiver(_ => true);
        await using var server = TestHttpServer.Start(receiver.ReceiveAsync);

        var faults = await WaitForGiveUpAsync(server);

        var simulated = TestHttpServer.MaxReceiveFailures + 1;
        Assert.Equal(simulated + 1, faults.Count);
        Assert.All(faults.Take(simulated), f => Assert.IsType<SimulatedReceiveException>(f));
        var giveUp = Assert.IsType<InvalidOperationException>(faults[^1]);
        Assert.Contains("deja de escuchar", giveUp.Message, StringComparison.Ordinal);
        Assert.IsType<SimulatedReceiveException>(giveUp.InnerException);

        // Rendido, ya no pide más peticiones al listener.
        await Task.Delay(100);
        Assert.Equal(simulated, receiver.Calls);
    }

    [Fact]
    public async Task Dispose_after_giving_up_is_clean_and_quick()
    {
        var server = TestHttpServer.Start(new ScriptedReceiver(_ => true).ReceiveAsync);
        await WaitForGiveUpAsync(server);

        var dispose = server.DisposeAsync().AsTask();
        Assert.Same(dispose, await Task.WhenAny(dispose, Task.Delay(Patience)));
        await dispose;
    }

    private static async Task<IReadOnlyList<Exception>> WaitForGiveUpAsync(TestHttpServer server)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            var faults = server.Faults;
            if (faults.Count > 0 && faults[^1] is InvalidOperationException)
            {
                return faults;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"El servidor no se rindió: {server.Faults.Count} fallos anotados.");
        return [];
    }

    /// <summary>Receptor que falla en las llamadas que diga <c>fails</c> (contadas desde 0) y en las demás recibe de verdad.</summary>
    private sealed class ScriptedReceiver(Func<int, bool> fails)
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<HttpListenerContext> ReceiveAsync(HttpListener listener)
        {
            var call = Interlocked.Increment(ref _calls) - 1;
            return fails(call)
                ? Task.FromException<HttpListenerContext>(new SimulatedReceiveException(call))
                : listener.GetContextAsync();
        }
    }

    private sealed class SimulatedReceiveException(int call) : Exception($"Fallo simulado al recibir (llamada {call}).");
}
