using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Rodaje.TestServer;

/// <summary>
/// Servidor HTTP en proceso para probar descargas sin red, sobre <see cref="HttpListener"/> en
/// <c>http://127.0.0.1:&lt;puerto libre&gt;/</c> (<c>localhost</c> en Windows). Cada prueba arranca el suyo con <see cref="Start"/>, registra
/// rutas con <see cref="Map"/> y lo cierra con <see cref="DisposeAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Cada ruta tiene una lista de respuestas que se van usando en orden, una por petición; la última se repite.
/// Así una prueba de reanudación registra «cortar a mitad» y luego «servir bien», y una de reintentos «500» y
/// luego el archivo. Las rutas sin registrar contestan 404.
/// </para>
/// <para>
/// Para redirecciones a otro host basta con arrancar dos servidores: cada uno tiene su puerto y por tanto su
/// origen. Todas las peticiones quedan en <see cref="Requests"/>.
/// </para>
/// </remarks>
public sealed class TestHttpServer : IAsyncDisposable
{
    private const int PortAttempts = 20;

    /// <summary>
    /// En Windows, HTTP.sys deja registrar <c>localhost</c> sin permisos de administrador pero no <c>127.0.0.1</c>
    /// (pediría <c>netsh http add urlacl</c>). En Linux la implementación administrada acepta <c>127.0.0.1</c>.
    /// </summary>
    private static readonly string LoopbackHost = OperatingSystem.IsWindows() ? "localhost" : "127.0.0.1";

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private readonly List<RecordedRequest> _requests = [];
    private readonly HashSet<Task> _handlers = [];
    private readonly Task _acceptLoop;
    private int _disposed;

    private TestHttpServer(HttpListener listener, Uri baseAddress)
    {
        _listener = listener;
        BaseAddress = baseAddress;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Dirección base con la barra final, p. ej. <c>http://127.0.0.1:41234/</c> (<c>localhost</c> en Windows).</summary>
    public Uri BaseAddress { get; }

    /// <summary>Copia de las peticiones recibidas hasta ahora, en orden de llegada.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Arranca un servidor en un puerto libre del bucle local.</summary>
    public static TestHttpServer Start()
    {
        HttpListenerException? last = null;
        for (var attempt = 0; attempt < PortAttempts; attempt++)
        {
            var port = FindFreePort();
            var baseAddress = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{LoopbackHost}:{port}/"));
            var listener = new HttpListener();
            listener.Prefixes.Add(baseAddress.ToString());
            try
            {
                listener.Start();
                return new TestHttpServer(listener, baseAddress);
            }
            catch (HttpListenerException ex)
            {
                // Otro proceso tomó el puerto entre que lo vimos libre y lo abrimos: se prueba otro.
                last = ex;
                listener.Close();
            }
        }

        throw new InvalidOperationException("No se encontró un puerto libre para el servidor de pruebas.", last);
    }

    /// <summary>URL absoluta de una ruta de este servidor.</summary>
    public Uri UrlFor(string path) => new(BaseAddress, path.TrimStart('/'));

    /// <summary>
    /// Registra (o sustituye) las respuestas de una ruta. Se usan en orden, una por petición, y la última se
    /// repite para siempre. La ruta se compara sin la consulta y distingue mayúsculas.
    /// </summary>
    public void Map(string path, params TestResponse[] responses)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(responses);
        if (responses.Length == 0)
        {
            throw new ArgumentException("Hace falta al menos una respuesta.", nameof(responses));
        }

        var key = NormalizePath(path);
        lock (_gate)
        {
            _routes[key] = new Route([.. responses]);
        }
    }

    /// <summary>Peticiones recibidas en una ruta (cualquier método).</summary>
    public int RequestCount(string path)
    {
        var key = NormalizePath(path);
        lock (_gate)
        {
            return _requests.Count(r => r.Path == key);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Close();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        Task[] pending;
        lock (_gate)
        {
            pending = [.. _handlers];
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Las respuestas a medio mandar se cortan al cerrar: no es un fallo de la prueba.
        }

        _stopping.Dispose();
    }

    private static string NormalizePath(string path) => path.StartsWith('/') ? path : "/" + path;

    private static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            var handler = Task.Run(() => HandleAsync(context));
            lock (_gate)
            {
                _handlers.Add(handler);
            }

            _ = handler.ContinueWith(
                t =>
                {
                    lock (_gate)
                    {
                        _handlers.Remove(t);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "/";

        TestResponse? configured;
        lock (_gate)
        {
            configured = _routes.TryGetValue(path, out var route) ? route.Next() : null;
        }

        configured ??= TestResponse.Status(404, "No encontrado");

        try
        {
            await RespondAsync(request, response, path, configured).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // El cliente cortó o el servidor se está cerrando.
            Abort(response);
        }
    }

    private async Task RespondAsync(HttpListenerRequest request, HttpListenerResponse response, string path, TestResponse configured)
    {
        var token = _stopping.Token;
        var rangeHeader = request.Headers["Range"];
        var ifRange = request.Headers["If-Range"];
        var body = configured.Body;
        var length = (long)body.Length;

        var status = configured.StatusCode;
        long offset = 0;
        var count = length;

        if (status == 200 && configured.AcceptRanges && IfRangeAllows(ifRange, configured.ETag))
        {
            switch (ByteRange.TryParse(rangeHeader, length, out var range))
            {
                case ByteRange.ParseResult.Satisfiable:
                    status = 206;
                    offset = range.First;
                    count = range.Length;
                    response.AddHeader("Content-Range", string.Create(CultureInfo.InvariantCulture, $"bytes {range.First}-{range.Last}/{length}"));
                    break;
                case ByteRange.ParseResult.Unsatisfiable:
                    status = 416;
                    count = 0;
                    response.AddHeader("Content-Range", string.Create(CultureInfo.InvariantCulture, $"bytes */{length}"));
                    break;
                case ByteRange.ParseResult.Ignore:
                default:
                    break;
            }
        }

        Record(request, path, rangeHeader, ifRange, status);

        if (configured.HeaderDelay > TimeSpan.Zero)
        {
            await Task.Delay(configured.HeaderDelay, token).ConfigureAwait(false);
        }

        response.StatusCode = status;
        response.ContentType = configured.ContentType;
        if (configured.AcceptRanges)
        {
            response.AddHeader("Accept-Ranges", "bytes");
        }

        if (configured.ETag is not null)
        {
            response.AddHeader("ETag", configured.ETag);
        }

        if (configured.Location is not null)
        {
            response.RedirectLocation = configured.Location;
        }

        if (configured.Headers is not null)
        {
            foreach (var (name, value) in configured.Headers)
            {
                response.AddHeader(name, value);
            }
        }

        response.SendChunked = false;
        response.ContentLength64 = count;

        if (string.Equals(request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            response.Close();
            return;
        }

        var limit = count;
        if (configured.CutAfterBytes is { } cut)
        {
            limit = Math.Min(limit, Math.Max(0, cut));
        }

        if (configured.StallAfterBytes is { } stall)
        {
            limit = Math.Min(limit, Math.Max(0, stall));
        }

        var output = response.OutputStream;
        var chunk = Math.Max(1, configured.ChunkSize);
        long sent = 0;
        while (sent < limit)
        {
            var size = (int)Math.Min(chunk, limit - sent);
            await output.WriteAsync(body.Slice((int)(offset + sent), size), token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            sent += size;
            if (configured.ChunkDelay > TimeSpan.Zero && sent < limit)
            {
                await Task.Delay(configured.ChunkDelay, token).ConfigureAwait(false);
            }
        }

        if (sent < count)
        {
            if (configured.StallAfterBytes is not null && (configured.CutAfterBytes is null || configured.StallAfterBytes < configured.CutAfterBytes))
            {
                // Colgado: ni manda ni cierra hasta que el servidor se cierre. Si el cliente se rinde antes, al
                // cerrar el servidor se corta igual.
                try
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            else if (configured.CutSignal is { } signal)
            {
                // Corte controlado por la prueba: espera a que el cliente haya leído lo mandado (ver CutSignal).
                await signal.WaitForReleaseAsync(token).ConfigureAwait(false);
            }

            // Corte a mitad: se resetea la conexión sin completar el Content-Length anunciado.
            Abort(response);
            return;
        }

        response.Close();
    }

    private static bool IfRangeAllows(string? ifRange, string? etag) =>
        string.IsNullOrEmpty(ifRange) || (etag is not null && string.Equals(ifRange.Trim(), etag, StringComparison.Ordinal));

    private static void Abort(HttpListenerResponse response)
    {
        try
        {
            response.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Record(HttpListenerRequest request, string path, string? range, string? ifRange, int status)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in request.Headers.AllKeys)
        {
            if (name is not null)
            {
                headers[name] = request.Headers[name] ?? string.Empty;
            }
        }

        var recorded = new RecordedRequest(request.HttpMethod, path, range, ifRange, headers, status);
        lock (_gate)
        {
            _requests.Add(recorded);
        }
    }

    /// <summary>Respuestas de una ruta y cuál toca.</summary>
    private sealed class Route(TestResponse[] responses)
    {
        private int _next;

        public TestResponse Next()
        {
            var response = responses[Math.Min(_next, responses.Length - 1)];
            if (_next < responses.Length)
            {
                _next++;
            }

            return response;
        }
    }
}
