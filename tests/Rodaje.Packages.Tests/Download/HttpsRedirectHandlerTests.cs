using System.Net;
using Rodaje.Packages.Download;

namespace Rodaje.Packages.Tests.Download;

/// <summary>
/// <see cref="HttpsRedirectHandler"/>: sigue redirecciones solo hacia HTTPS, con tope de saltos y sin reenviar
/// credenciales (<c>Authorization</c>, <c>Cookie</c>, <c>Host</c>) al destino.
/// </summary>
public sealed class HttpsRedirectHandlerTests
{
    [Fact]
    public async Task UnaRedireccionAHttp_SeRechaza()
    {
        var stub = new Stub(
            Redirect("http://inseguro.example.com/payload"),
            new HttpResponseMessage(HttpStatusCode.OK));

        using var http = new HttpClient(new HttpsRedirectHandler(stub));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync("https://origen.example.com/paquete"));

        Assert.Single(stub.Seen);
    }

    [Fact]
    public async Task UnaRedireccionAHttps_SeSigueYSinCredenciales()
    {
        var stub = new Stub(
            Redirect("https://cdn.example.com/paquete"),
            new HttpResponseMessage(HttpStatusCode.OK));

        using var http = new HttpClient(new HttpsRedirectHandler(stub));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://origen.example.com/paquete");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secreto");
        request.Headers.TryAddWithoutValidation("Cookie", "sesion=secreta");

        using HttpResponseMessage response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["https://origen.example.com/paquete", "https://cdn.example.com/paquete"],
            stub.Seen.Select(u => u.ToString()));
        Assert.False(stub.Last.Headers.Contains("Authorization"));
        Assert.False(stub.Last.Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task MasRedireccionesQueElTope_SeRechazan()
    {
        var responses = Enumerable.Range(0, HttpsRedirectHandler.MaxRedirects + 2)
            .Select(i => Redirect($"https://cdn.example.com/{i}"))
            .Append(new HttpResponseMessage(HttpStatusCode.OK))
            .ToArray();
        var stub = new Stub(responses);

        using var http = new HttpClient(new HttpsRedirectHandler(stub));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync("https://origen.example.com/paquete"));
    }

    [Fact]
    public async Task UnaRedireccionSinLocation_SeRechaza()
    {
        var stub = new Stub(new HttpResponseMessage(HttpStatusCode.MovedPermanently));

        using var http = new HttpClient(new HttpsRedirectHandler(stub));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync("https://origen.example.com/paquete"));
    }

    [Fact]
    public async Task UnaRespuestaSinRedireccion_PasaTalCual()
    {
        var stub = new Stub(new HttpResponseMessage(HttpStatusCode.NotFound));

        using var http = new HttpClient(new HttpsRedirectHandler(stub));
        using HttpResponseMessage response = await http.GetAsync("https://origen.example.com/paquete");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(stub.Seen);
    }

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };

    /// <summary>Devuelve en orden las respuestas programadas y recuerda lo que le pidieron.</summary>
    private sealed class Stub(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<Uri> Seen { get; } = [];
        public HttpRequestMessage Last { get; private set; } = null!;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);
            Last = request;
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
