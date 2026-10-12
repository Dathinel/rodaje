namespace Rodaje.TestServer.Tests;

/// <summary>
/// Números que no caben en <see cref="long"/>: antes se ignoraba la cabecera y salía el archivo entero con 200;
/// ahora es un rango imposible (416).
/// </summary>
public sealed class ByteRangeOverflowTests
{
    private const string TooLarge = "99999999999999999999";

    [Theory]
    [InlineData("bytes=" + TooLarge + "-")]
    [InlineData("bytes=" + TooLarge + "-" + TooLarge)]
    [InlineData("bytes=0-" + TooLarge)]
    [InlineData("bytes=10-" + TooLarge)]
    [InlineData("bytes=-" + TooLarge)]
    [InlineData("bytes=9223372036854775808-")]
    public void A_number_beyond_long_is_unsatisfiable(string header)
    {
        Assert.Equal(ByteRange.ParseResult.Unsatisfiable, ByteRange.TryParse(header, 100, out _));
    }

    [Fact]
    public void The_largest_long_still_parses()
    {
        Assert.Equal(ByteRange.ParseResult.Unsatisfiable, ByteRange.TryParse("bytes=9223372036854775807-", 100, out _));
        Assert.Equal(ByteRange.ParseResult.Satisfiable, ByteRange.TryParse("bytes=0-9223372036854775807", 100, out var range));
        Assert.Equal(new ByteRange(0, 99), range);
    }

    [Theory]
    [InlineData("bytes=" + TooLarge + "x-")]
    [InlineData("bytes=0-" + TooLarge + "x")]
    [InlineData("bytes=-" + TooLarge + "x")]
    public void Digits_followed_by_garbage_are_still_ignored(string header)
    {
        Assert.Equal(ByteRange.ParseResult.Ignore, ByteRange.TryParse(header, 100, out _));
    }

    [Fact]
    public async Task The_server_answers_416_to_a_range_beyond_long()
    {
        await using var server = TestHttpServer.Start();
        var content = TestPayloads.Deterministic(1000);
        server.Map("/a", TestResponse.File(content));
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.UrlFor("/a"));
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-" + TooLarge);

        using var response = await http.SendAsync(request);

        // En Windows, HTTP.sys rechaza la cabecera con 400 antes de que llegue al servidor; en los dos casos el
        // cliente no recibe el archivo entero, que es lo que pasaba antes.
        if (OperatingSystem.IsWindows() && response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            Assert.Empty(server.Requests);
            return;
        }

        Assert.Equal(System.Net.HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */1000", response.Content.Headers.GetValues("Content-Range").Single());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }
}
