using System.Net;

namespace Rodaje.TestServer.Tests;

/// <summary>
/// Un fallo inesperado del manejador (aquí, un código de estado que <see cref="HttpListener"/> rechaza) corta la conexión
/// enseguida y queda en <see cref="TestHttpServer.Faults"/>, en vez de dejar al cliente esperando una respuesta.
/// </summary>
public sealed class HandlerFaultTests
{
    /// <summary><see cref="HttpListenerResponse.StatusCode"/> lanza <see cref="ProtocolViolationException"/> fuera de 100–999.</summary>
    private static TestResponse BadStatus(TestResponse response) => response with { StatusCode = 1000 };

    [Fact]
    public async Task An_unexpected_exception_aborts_the_connection_and_is_recorded()
    {
        await using var server = TestHttpServer.Start();
        server.Map("/mala", BadStatus(TestResponse.File(TestPayloads.Deterministic(1000))));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync(server.UrlFor("/mala")));

        Assert.IsType<HttpRequestException>(ex);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8), "El cliente esperó al plazo: la conexión quedó colgada.");
        Assert.Single(server.Faults);
        Assert.IsType<ProtocolViolationException>(server.Faults[0]);
    }

    [Fact]
    public async Task The_server_keeps_serving_after_a_fault()
    {
        await using var server = TestHttpServer.Start();
        var content = TestPayloads.Deterministic(5000);
        server.Map("/a", BadStatus(TestResponse.File(content)), TestResponse.File(content));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(server.UrlFor("/a")));
        Assert.Equal(content, await http.GetByteArrayAsync(server.UrlFor("/a")));
        Assert.Single(server.Faults);
    }

    [Fact]
    public async Task A_client_that_hangs_up_is_not_a_fault()
    {
        await using var server = TestHttpServer.Start();
        server.Map("/lenta", TestResponse.File(TestPayloads.Deterministic(1_000_000)) with { ChunkSize = 1000, ChunkDelay = TimeSpan.FromMilliseconds(5) });
        using var http = new HttpClient();

        using (var response = await http.GetAsync(server.UrlFor("/lenta"), HttpCompletionOption.ResponseHeadersRead))
        {
            await using var body = await response.Content.ReadAsStreamAsync();
            await body.ReadExactlyAsync(new byte[1000]);
        }

        await server.DisposeAsync();
        Assert.Empty(server.Faults);
    }
}
