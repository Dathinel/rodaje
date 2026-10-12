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

    /// <summary>El único byte del cuerpo que se manda antes de cortar una respuesta que falló.</summary>
    private static readonly ReadOnlyMemory<byte> FaultBodyByte = new byte[] { (byte)'!' };

    /// <summary>
    /// Fallos seguidos al recibir peticiones tras los que el bucle se rinde (para no girar sin fin con el listener
    /// roto). El contador vuelve a 0 con cada petición recibida: fallos aislados en un servidor de larga vida no lo apagan.
    /// </summary>
    internal const int MaxReceiveFailures = 100;

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
    private readonly List<Exception> _faults = [];
    private readonly Func<HttpListener, Task<HttpListenerContext>> _receive;
    private readonly Task _acceptLoop;
    private int _disposed;

    private TestHttpServer(HttpListener listener, Uri baseAddress, Func<HttpListener, Task<HttpListenerContext>> receive)
    {
        _listener = listener;
        BaseAddress = baseAddress;
        _receive = receive;
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

    /// <summary>
    /// Excepciones inesperadas de los manejadores (no las de un cliente que corta ni las del cierre), que cortaron su
    /// conexión, y fallos al recibir una petición con el servidor abierto, tras los que se siguió escuchando. Si se
    /// llega a <see cref="MaxReceiveFailures"/> fallos seguidos, la última entrada es una
    /// <see cref="InvalidOperationException"/> que dice que el servidor dejó de escuchar. Una prueba puede exigir que
    /// esté vacía para descartar un fallo del propio servidor.
    /// </summary>
    public IReadOnlyList<Exception> Faults
    {
        get
        {
            lock (_gate)
            {
                return [.. _faults];
            }
        }
    }

    /// <summary>Arranca un servidor en un puerto libre del bucle local.</summary>
    public static TestHttpServer Start() => Start(static listener => listener.GetContextAsync());

    /// <summary>
    /// Como <see cref="Start()"/>, pero cada petición se recibe con <paramref name="receive"/>: las pruebas del propio
    /// servidor lo usan para simular fallos al recibir.
    /// </summary>
    internal static TestHttpServer Start(Func<HttpListener, Task<HttpListenerContext>> receive)
    {
        ArgumentNullException.ThrowIfNull(receive);
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
                return new TestHttpServer(listener, baseAddress, receive);
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

    /// <summary>
    /// Un puerto libre en 127.0.0.1 y, si hay IPv6, también en <c>::1</c>. En Windows el cliente va a
    /// <c>localhost</c>, que resuelve primero a <c>::1</c>: un puerto libre solo en IPv4 podría estar tomado en IPv6
    /// por otro proceso (por ejemplo otro proyecto de pruebas que corre a la vez), que aceptaría la conexión sin
    /// contestar HTTP. Solo se escucha en el bucle local, para que el Firewall de Windows no pregunte.
    /// </summary>
    private static int FindFreePort()
    {
        for (var attempt = 0; attempt < PortAttempts; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                if (IsFreeOnIPv6Loopback(port))
                {
                    return port;
                }
            }
            finally
            {
                probe.Stop();
            }
        }

        throw new InvalidOperationException("No se encontró un puerto libre en IPv4 e IPv6 para el servidor de pruebas.");
    }

    private static bool IsFreeOnIPv6Loopback(int port)
    {
        if (!Socket.OSSupportsIPv6)
        {
            return true;
        }

        var probe = new TcpListener(IPAddress.IPv6Loopback, port);
        try
        {
            probe.Start();
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return false;
        }
        catch (SocketException)
        {
            // Sin ::1 utilizable (IPv6 desactivado en la interfaz de bucle): no hay nada que choque.
            return true;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptLoopAsync()
    {
        var failures = 0;
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _receive(_listener).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Ningún fallo al recibir puede apagar el servidor en silencio: se anota en Faults.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_stopping.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }

                // En Windows, HTTP.sys puede fallar al recibir una petición (un cliente que corta a mitad de las
                // cabeceras, E/S abortada) con el servidor sano: antes eso terminaba este bucle y las peticiones
                // siguientes quedaban sin contestar hasta el plazo del cliente. Se anota y se sigue escuchando, salvo
                // que falle demasiadas veces seguidas: entonces el listener está roto y se deja constancia en Faults.
                var giveUp = ++failures > MaxReceiveFailures;
                lock (_gate)
                {
                    _faults.Add(ex);
                    if (giveUp)
                    {
                        _faults.Add(new InvalidOperationException(
                            string.Create(CultureInfo.InvariantCulture, $"El servidor de pruebas deja de escuchar tras {failures} fallos seguidos al recibir peticiones."),
                            ex));
                    }
                }

                if (giveUp)
                {
                    return;
                }

                continue;
            }

            failures = 0;

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
        var response = context.Response;
        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? "/";

            TestResponse? configured;
            lock (_gate)
            {
                configured = _routes.TryGetValue(path, out var route) ? route.Next() : null;
            }

            configured ??= TestResponse.Status(404, "No encontrado");
            await RespondAsync(request, response, path, configured).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // El cliente cortó o el servidor se está cerrando.
            Abort(response);
        }
#pragma warning disable CA1031 // Cualquier otro fallo se guarda en Faults: no puede dejar la conexión colgada ni la tarea sin observar.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Una respuesta mal configurada (p. ej. un código de estado que HttpListener no acepta): se corta la conexión
            // para que el cliente vea el error enseguida, y la excepción queda en Faults para la prueba.
            lock (_gate)
            {
                _faults.Add(ex);
            }

            await AbortAfterFaultAsync(response).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Corta una respuesta que falló. Si las cabeceras no habían salido, se anuncia un 500 de dos bytes, se manda uno y
    /// se corta: el cliente ve un cuerpo incompleto (un error) en vez de un archivo vacío o una espera.
    /// </summary>
    /// <remarks>
    /// En Linux, <see cref="HttpListenerResponse.Abort"/> manda antes las cabeceras que falten (un 200 vacío si no se
    /// llegó a fijar nada). En Windows, <see cref="HttpListenerResponse.Abort"/> sin nada enviado cancela la petición
    /// en HTTP.sys, pero la CI mostró que a veces la conexión no se reinicia y el cliente espera hasta su plazo. Con las
    /// cabeceras y un byte ya en el cable, el corte es el mismo que el de <see cref="TestResponse.CutAfterBytes"/> a
    /// mitad del cuerpo, que HTTP.sys sí reinicia. Escribir cero bytes o vaciar el flujo no basta: en HTTP.sys no
    /// manda las cabeceras mientras falte cuerpo.
    /// </remarks>
    private async Task AbortAfterFaultAsync(HttpListenerResponse response)
    {
        var headersPending = true;
        try
        {
            response.StatusCode = 500;
            response.SendChunked = false;
            response.ContentLength64 = 2;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or HttpListenerException)
        {
            // Las cabeceras ya salieron: el corte basta.
            headersPending = false;
        }

        if (headersPending)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var output = response.OutputStream;
                await output.WriteAsync(FaultBodyByte, timeout.Token).ConfigureAwait(false);
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // El cliente ya se fue o el servidor se cierra: el corte basta.
            }
        }

        Abort(response);
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
