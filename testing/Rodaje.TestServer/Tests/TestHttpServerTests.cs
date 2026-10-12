using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Rodaje.TestServer.Tests;

public sealed class TestHttpServerTests
{
    private const string FilePath = "/releases/download/v1/a.paquete";

    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Serves_the_whole_file()
    {
        await using var server = TestHttpServer.Start();
        var content = TestPayloads.Deterministic(300_000);
        server.Map(FilePath, TestResponse.File(content, "\"v1\""));
        using var client = NewClient();

        using var response = await client.GetAsync(server.UrlFor(FilePath));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, body);
        Assert.Equal(content.Length, response.Content.Headers.ContentLength);
        Assert.Equal("\"v1\"", response.Headers.ETag?.ToString());
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.True(server.BaseAddress.IsLoopback);
    }

    [Fact]
    public async Task Head_reports_the_length_without_a_body()
    {
        await using var server = TestHttpServer.Start();
        server.Map(FilePath, TestResponse.File(TestPayloads.Deterministic(5000)));
        using var client = NewClient();

        using var request = new HttpRequestMessage(HttpMethod.Head, server.UrlFor(FilePath));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5000, response.Content.Headers.ContentLength);
        Assert.Equal("HEAD", Assert.Single(server.Requests).Method);
    }

    [Theory]
    [InlineData(1000L, 1999L, 1000L, 1999L)]
    [InlineData(1000L, null, 1000L, 9999L)]
    [InlineData(null, 100L, 9900L, 9999L)]
    public async Task Serves_ranges_with_206(long? from, long? to, long first, long last)
    {
        await using var server = TestHttpServer.Start();
        var content = TestPayloads.Deterministic(10_000);
        server.Map(FilePath, TestResponse.File(content));
        using var client = NewClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.UrlFor(FilePath));
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(content.AsSpan((int)first, (int)(last - first + 1)).ToArray(), body);
        var contentRange = response.Content.Headers.ContentRange;
        Assert.NotNull(contentRange);
        Assert.Equal(first, contentRange.From);
        Assert.Equal(last, contentRange.To);
        Assert.Equal(content.Length, contentRange.Length);
    }

    [Fact]
    public async Task Range_past_the_end_gives_416()
    {
        await using var server = TestHttpServer.Start();
        server.Map(FilePath, TestResponse.File(TestPayloads.Deterministic(100)));
        using var client = NewClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.UrlFor(FilePath));
        request.Headers.Range = new RangeHeaderValue(100, null);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal(100, response.Content.Headers.ContentRange?.Length);
        Assert.Equal(416, Assert.Single(server.Requests).StatusCode);
    }

    [Fact]
    public async Task Without_range_support_the_whole_file_comes_back_with_200()
    {
        await using var server = TestHttpServer.Start();
        var content = TestPayloads.Deterministic(10_000);
        server.Map(FilePath, TestResponse.File(content) with { AcceptRanges = false });
        using var client = NewClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.UrlFor(FilePath));
        request.Headers.Range = new RangeHeaderValue(5000, null);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.AcceptRanges);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("\"v1\"", HttpStatusCode.PartialContent)]
    [InlineData("\"v0\"", HttpStatusCode.OK)]
    public async Task If_range_resumes_only_when_the_etag_matches(string ifRange, HttpStatusCode expected)
    {
        await using var server = TestHttpServer.Start();
        server.Map(FilePath, TestResponse.File(TestPayloads.Deterministic(1000), "\"v1\""));
        using var client = NewClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.UrlFor(FilePath));
        request.Headers.Range = new RangeHeaderValue(500, null);
        request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(ifRange));
        using var response = await client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        var recorded = Assert.Single(server.Requests);
        Assert.Equal("bytes=500-", recorded.Range);
        Assert.Equal(ifRange, recorded.IfRange);
    }

    [Fact]
    public async Task Unmapped_paths_give_404()
    {
        await using var server = TestHttpServer.Start();
        using var client = NewClient();

        using var response = await client.GetAsync(server.UrlFor("/no/existe"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Responses_are_used_in_order_and_the_last_repeats()
    {
        await using var server = TestHttpServer.Start();
        var content = TestPayloads.Deterministic(1000);
        server.Map(FilePath, TestResponse.Status(500, "falla"), TestResponse.Status(503), TestResponse.File(content));
        using var client = NewClient();
        var url = server.UrlFor(FilePath);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await client.GetAsync(url);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, HttpStatusCode.OK], statuses);
        Assert.Equal(4, server.RequestCount(FilePath));
        Assert.Equal(4, server.RequestCount(FilePath.TrimStart('/')));
    }

    [Fact]
    public async Task Status_responses_carry_their_text()
    {
        await using var server = TestHttpServer.Start();
        server.Map("/x", TestResponse.Status(500, "falla del servidor"));
        using var client = NewClient();

        using var response = await client.GetAsync(server.UrlFor("/x"));

        Assert.Equal("falla del servidor", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Follows_redirects_to_another_host()
    {
        await using var github = TestHttpServer.Start();
        await using var storage = TestHttpServer.Start();
        var content = TestPayloads.Deterministic(20_000);
        storage.Map("/objeto", TestResponse.File(content));
        github.Map(FilePath, TestResponse.Redirect(storage.UrlFor("/objeto?firma=abc").ToString()));
        using var client = NewClient();

        using var response = await client.GetAsync(github.UrlFor(FilePath));

        Assert.NotEqual(github.BaseAddress.Port, storage.BaseAddress.Port);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(storage.UrlFor("/objeto?firma=abc"), response.RequestMessage?.RequestUri);
        Assert.Equal(302, Assert.Single(github.Requests).StatusCode);
        Assert.Equal("/objeto", Assert.Single(storage.Requests).Path);
    }

    [Fact]
    public async Task Redirect_without_following_exposes_the_location()
    {
        await using var server = TestHttpServer.Start();
        server.Map("/a", TestResponse.Redirect("/b", 307));
        using var client = NewClient(allowRedirects: false);

        using var response = await client.GetAsync(server.UrlFor("/a"));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("/b", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Redirect_loops_make_the_client_give_up()
    {
        await using var server = TestHttpServer.Start();
        server.Map("/a", TestResponse.Redirect(server.UrlFor("/b").ToString()));
        server.Map("/b", TestResponse.Redirect(server.UrlFor("/a").ToString()));
        using var handler = new HttpClientHandler { MaxAutomaticRedirections = 5 };
        using var client = new HttpClient(handler) { Timeout = ClientTimeout };

        using var response = await client.GetAsync(server.UrlFor("/a"));

        // SocketsHttpHandler deja de seguir al pasar el máximo y devuelve la última redirección.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(6, server.Requests.Count);
    }

    [Fact]
    public async Task A_cut_breaks_the_body_and_a_range_request_resumes_it()
    {
        await using var server = TestHttpServer.Start();
        var content = TestPayloads.Deterministic(200_000);
        var cut = new CutSignal();
        server.Map(
            FilePath,
            TestResponse.File(content, "\"v1\"") with { CutAfterBytes = 70_000, ChunkSize = 10_000, CutSignal = cut },
            TestResponse.File(content, "\"v1\""));
        using var client = NewClient();

        var received = new MemoryStream();
        using (var first = await client.GetAsync(server.UrlFor(FilePath), HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(content.Length, first.Content.Headers.ContentLength);
            var stream = await first.Content.ReadAsStreamAsync();
            var buffer = new byte[70_000];
            await stream.ReadExactlyAsync(buffer);
            received.Write(buffer);
            await cut.Reached;
            cut.Release();
            await Assert.ThrowsAnyAsync<IOException>(() => stream.CopyToAsync(received));
        }

        Assert.Equal(70_000, received.Length);

        using var resume = new HttpRequestMessage(HttpMethod.Get, server.UrlFor(FilePath));
        resume.Headers.Range = new RangeHeaderValue(received.Length, null);
        resume.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"v1\""));
        using var second = await client.SendAsync(resume);
        Assert.Equal(HttpStatusCode.PartialContent, second.StatusCode);
        await (await second.Content.ReadAsStreamAsync()).CopyToAsync(received);

        Assert.Equal(TestPayloads.Sha256Hex(content), TestPayloads.Sha256Hex(received.ToArray()));
    }

    [Fact]
    public async Task An_immediate_cut_delivers_a_prefix_that_resumes()
    {
        // Sin CutSignal, como lo verá un descargador que no controla sus lecturas: en Linux llegan los 70 000
        // bytes; en Windows pueden llegar menos (HTTP.sys corta con RST). Solo se exige un prefijo y que reanude.
        await using var server = TestHttpServer.Start();
        var content = TestPayloads.Deterministic(200_000);
        server.Map(
            FilePath,
            TestResponse.File(content, "\"v1\"") with { CutAfterBytes = 70_000, ChunkSize = 10_000 },
            TestResponse.File(content, "\"v1\""));
        using var client = NewClient();

        var received = new MemoryStream();
        using (var first = await client.GetAsync(server.UrlFor(FilePath), HttpCompletionOption.ResponseHeadersRead))
        {
            var stream = await first.Content.ReadAsStreamAsync();
            await Assert.ThrowsAnyAsync<IOException>(() => stream.CopyToAsync(received));
        }

        Assert.InRange(received.Length, 0, 70_000);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(70_000, received.Length);
        }

        Assert.Equal(content.AsSpan(0, (int)received.Length).ToArray(), received.ToArray());

        using var resume = new HttpRequestMessage(HttpMethod.Get, server.UrlFor(FilePath));
        resume.Headers.Range = new RangeHeaderValue(received.Length, null);
        using var second = await client.SendAsync(resume);
        await (await second.Content.ReadAsStreamAsync()).CopyToAsync(received);

        Assert.Equal(TestPayloads.Sha256Hex(content), TestPayloads.Sha256Hex(received.ToArray()));
    }

    [Fact]
    public async Task A_cut_inside_a_range_counts_from_the_range_start()
    {
        await using var server = TestHttpServer.Start();
        var content = TestPayloads.Deterministic(10_000);
        var cut = new CutSignal();
        server.Map(FilePath, TestResponse.File(content) with { CutAfterBytes = 10, CutSignal = cut });
        using var client = NewClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.UrlFor(FilePath));
        request.Headers.Range = new RangeHeaderValue(1000, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        var received = new byte[10];
        await stream.ReadExactlyAsync(received);
        cut.Release();
        await Assert.ThrowsAnyAsync<IOException>(async () => await stream.ReadExactlyAsync(new byte[1]));

        Assert.Equal(content.AsSpan(1000, 10).ToArray(), received);
    }

    [Fact]
    public async Task A_waiting_cut_holds_the_connection_until_released()
    {
        await using var server = TestHttpServer.Start();
        var cut = new CutSignal();
        server.Map(FilePath, TestResponse.File(TestPayloads.Deterministic(1000)) with { CutAfterBytes = 100, CutSignal = cut });
        using var client = NewClient();

        using var response = await client.GetAsync(server.UrlFor(FilePath), HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        await stream.ReadExactlyAsync(new byte[100]);
        await cut.Reached;
        var next = stream.ReadExactlyAsync(new byte[1]).AsTask();
        await Task.Delay(100);

        Assert.False(next.IsCompleted);
        Assert.False(cut.IsReleased);
        cut.Release();
        await Assert.ThrowsAnyAsync<IOException>(() => next);
    }

    [Fact]
    public async Task Dispose_ends_a_cut_that_is_never_released()
    {
        var server = TestHttpServer.Start();
        var cut = new CutSignal();
        server.Map(FilePath, TestResponse.File(TestPayloads.Deterministic(1000)) with { CutAfterBytes = 100, CutSignal = cut });
        using var client = NewClient();
        using var response = await client.GetAsync(server.UrlFor(FilePath), HttpCompletionOption.ResponseHeadersRead);
        var read = response.Content.ReadAsByteArrayAsync();
        await cut.Reached;

        var clock = Stopwatch.StartNew();
        await server.DisposeAsync();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {clock.Elapsed.TotalMilliseconds} ms");
        await Assert.ThrowsAnyAsync<Exception>(() => read);
    }

    [Fact]
    public async Task A_cut_at_zero_bytes_fails_before_the_headers()
    {
        await using var server = TestHttpServer.Start();
        server.Map(FilePath, TestResponse.File(TestPayloads.Deterministic(1000)) with { CutAfterBytes = 0 });
        using var client = NewClient();

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(server.UrlFor(FilePath)));
    }

    [Fact]
    public async Task A_corrupt_file_is_served_byte_for_byte()
    {
        await using var server = TestHttpServer.Start();
        var good = TestPayloads.Deterministic(4096);
        var bad = TestPayloads.Corrupt(good, 2000);
        server.Map(FilePath, TestResponse.File(bad));
        using var client = NewClient();

        var body = await client.GetByteArrayAsync(server.UrlFor(FilePath));

        Assert.Equal(bad, body);
        Assert.NotEqual(TestPayloads.Sha256Hex(good), TestPayloads.Sha256Hex(body));
    }

    [Fact]
    public async Task Serves_a_signed_index_and_a_bad_signature()
    {
        await using var server = TestHttpServer.Start();
        using var signer = new TestIndexSigner();
        var index = "{ \"formatVersion\": 1, \"entries\": [] }"u8.ToArray();
        server.Map("/indice.json", TestResponse.File(index) with { ContentType = "application/json" });
        server.Map(
            "/indice.json.sig",
            TestResponse.File(System.Text.Encoding.ASCII.GetBytes(signer.Sign(index))),
            TestResponse.File(System.Text.Encoding.ASCII.GetBytes(signer.SignOtherData(index))));
        using var client = NewClient();

        var served = await client.GetByteArrayAsync(server.UrlFor("/indice.json"));
        var goodSig = await client.GetStringAsync(server.UrlFor("/indice.json.sig"));
        var badSig = await client.GetStringAsync(server.UrlFor("/indice.json.sig"));

        Assert.True(TestIndexSigner.Verify(signer.PublicKey, served, goodSig));
        Assert.False(TestIndexSigner.Verify(signer.PublicKey, served, badSig));
    }

    [Fact]
    public async Task Slow_responses_take_at_least_their_delays()
    {
        await using var server = TestHttpServer.Start();
        var content = TestPayloads.Deterministic(4000);
        server.Map(FilePath, TestResponse.File(content) with
        {
            HeaderDelay = TimeSpan.FromMilliseconds(100),
            ChunkSize = 1000,
            ChunkDelay = TimeSpan.FromMilliseconds(50),
        });
        using var client = NewClient();

        var clock = Stopwatch.StartNew();
        var body = await client.GetByteArrayAsync(server.UrlFor(FilePath));
        clock.Stop();

        // Solo cota inferior: 100 ms + 3 esperas de 50 ms entre los 4 trozos. Sin cota superior para no fallar
        // en una máquina cargada.
        Assert.Equal(content, body);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(240), $"Tardó {clock.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task A_stalled_response_trips_the_client_timeout()
    {
        await using var server = TestHttpServer.Start();
        server.Map(FilePath, TestResponse.File(TestPayloads.Deterministic(10_000)) with { StallAfterBytes = 100 });
        using var client = NewClient();

        using var response = await client.GetAsync(server.UrlFor(FilePath), HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var received = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.CopyToAsync(received, timeout.Token));
        Assert.Equal(100, received.Length);
    }

    [Fact]
    public async Task Dispose_ends_stalled_responses_quickly()
    {
        var server = TestHttpServer.Start();

        // Con 0 bytes HttpListener tampoco manda las cabeceras: el cliente se queda esperando la respuesta.
        server.Map(FilePath, TestResponse.File(TestPayloads.Deterministic(10_000)) with { StallAfterBytes = 0 });
        using var client = NewClient();
        var download = client.GetByteArrayAsync(server.UrlFor(FilePath));
        var waited = Stopwatch.StartNew();
        while (server.RequestCount(FilePath) == 0 && waited.Elapsed < ClientTimeout)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, server.RequestCount(FilePath));
        Assert.False(download.IsCompleted);

        var clock = Stopwatch.StartNew();
        await server.DisposeAsync();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {clock.Elapsed.TotalMilliseconds} ms");
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => download);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Records_request_headers_and_sends_extra_headers()
    {
        await using var server = TestHttpServer.Start();
        server.Map(FilePath, TestResponse.File(TestPayloads.Deterministic(10)) with
        {
            Headers = new Dictionary<string, string> { ["X-Rodaje-Prueba"] = "1" },
        });
        using var client = NewClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Rodaje/0.7");

        using var response = await client.GetAsync(server.UrlFor(FilePath + "?descarga=1"));

        Assert.Equal("1", Assert.Single(response.Headers.GetValues("X-Rodaje-Prueba")));
        var recorded = Assert.Single(server.Requests);
        Assert.Equal("GET", recorded.Method);
        Assert.Equal(FilePath, recorded.Path);
        Assert.Null(recorded.Range);
        Assert.Equal("Rodaje/0.7", recorded.Headers["user-agent"]);
        Assert.Equal(200, recorded.StatusCode);
    }

    [Fact]
    public async Task Serves_concurrent_downloads()
    {
        await using var server = TestHttpServer.Start();
        var files = Enumerable.Range(0, 8).Select(i => TestPayloads.Deterministic(50_000, i + 1)).ToArray();
        for (var i = 0; i < files.Length; i++)
        {
            server.Map($"/f{i}", TestResponse.File(files[i]) with { ChunkSize = 5000, ChunkDelay = TimeSpan.FromMilliseconds(5) });
        }

        using var client = NewClient();

        var bodies = await Task.WhenAll(files.Select((_, i) => client.GetByteArrayAsync(server.UrlFor($"/f{i}"))));

        for (var i = 0; i < files.Length; i++)
        {
            Assert.Equal(files[i], bodies[i]);
        }
    }

    [Fact]
    public async Task Map_rejects_an_empty_list()
    {
        await using var server = TestHttpServer.Start();

        Assert.Throws<ArgumentException>(() => server.Map("/x"));
    }

    private static HttpClient NewClient(bool allowRedirects = true)
    {
#pragma warning disable CA2000 // El HttpClient se queda con el manejador y lo libera.
        var handler = new HttpClientHandler { AllowAutoRedirect = allowRedirects };
#pragma warning restore CA2000
        return new HttpClient(handler, disposeHandler: true) { Timeout = ClientTimeout };
    }
}
