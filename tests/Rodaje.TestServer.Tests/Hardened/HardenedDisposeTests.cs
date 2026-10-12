using System.Diagnostics;

namespace Rodaje.TestServer.Tests.Hardened;

/// <summary>
/// Versión endurecida de <c>TestHttpServerTests.Dispose_ends_a_cut_that_is_never_released</c>: allí la lectura
/// pendiente podía fallar con cualquier excepción (<c>ThrowsAnyAsync&lt;Exception&gt;</c>), así que un
/// <see cref="NullReferenceException"/> o un <see cref="ObjectDisposedException"/> del propio servidor, o el plazo del
/// cliente (<see cref="TaskCanceledException"/>), también pasaban. Aquí tiene que ser un corte de red, rápido y sin fallos
/// registrados en el servidor (el cierre no es un fallo de la respuesta configurada).
/// </summary>
public sealed class HardenedDisposeTests
{
    private const string FilePath = "/releases/download/v1/a.paquete";

    [Fact]
    public async Task Dispose_ends_a_never_released_cut_as_a_network_failure()
    {
        var server = TestHttpServer.Start();
        var cut = new CutSignal();
        server.Map(FilePath, TestResponse.File(TestPayloads.Deterministic(1000)) with { CutAfterBytes = 100, CutSignal = cut });
#pragma warning disable CA2000 // El HttpClient se queda con el manejador y lo libera.
        using var client = new HttpClient(new HttpClientHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
#pragma warning restore CA2000
        using var response = await client.GetAsync(server.UrlFor(FilePath), HttpCompletionOption.ResponseHeadersRead);
        Task<byte[]> read = response.Content.ReadAsByteArrayAsync();
        await cut.Reached;

        var clock = Stopwatch.StartNew();
        await server.DisposeAsync();
        Exception? error = await Record.ExceptionAsync(() => read);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {clock.Elapsed.TotalMilliseconds} ms");
        Assert.NotNull(error);
        Assert.False(error is OperationCanceledException, $"Falló por el plazo del cliente, no por el corte: {error}");
        Assert.True(error is HttpRequestException or IOException, $"Se esperaba un corte de red y llegó {error.GetType().Name}: {error}");
        Assert.Empty(server.Faults);
    }
}
