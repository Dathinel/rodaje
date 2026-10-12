namespace Rodaje.TestServer;

/// <summary>
/// Una respuesta configurada para una ruta del <see cref="TestHttpServer"/>. Es inmutable: cada prueba arma la
/// suya con los métodos de fábrica y modifica lo que necesite con <c>with</c>.
/// </summary>
/// <remarks>
/// Los cortes y las esperas cuentan sobre los bytes del cuerpo de <em>esta</em> respuesta: con un <c>Range</c>
/// que empieza en 1000 y <see cref="CutAfterBytes"/> = 10, el cliente recibe los bytes 1000 a 1009 del archivo
/// y luego la conexión se corta.
/// </remarks>
public sealed record TestResponse
{
    /// <summary>Código de estado. Con 200 y un <c>Range</c> válido el servidor responde 206.</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>Cuerpo completo del recurso (el archivo entero, aunque se pida un rango).</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>Tipo de contenido que se anuncia.</summary>
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>
    /// Si es <c>true</c> (por defecto) se anuncia <c>Accept-Ranges: bytes</c> y se atiende <c>Range</c> e
    /// <c>If-Range</c>. Si es <c>false</c> se ignora <c>Range</c> y siempre se manda el archivo entero con 200,
    /// como un servidor que no sabe reanudar.
    /// </summary>
    public bool AcceptRanges { get; init; } = true;

    /// <summary>
    /// ETag que se anuncia (con comillas, p. ej. <c>"v1"</c>). <c>null</c> no manda ETag; entonces un
    /// <c>If-Range</c> nunca coincide y se manda el archivo entero.
    /// </summary>
    public string? ETag { get; init; }

    /// <summary>Destino de una redirección (<c>Location</c>). Puede ser absoluto, a otro servidor, o relativo.</summary>
    public string? Location { get; init; }

    /// <summary>
    /// Corta la conexión (sin cerrar bien la respuesta) tras mandar estos bytes del cuerpo. Con 0 se corta
    /// antes de mandar las cabeceras: el cliente ve un error de conexión, no una respuesta.
    /// </summary>
    public long? CutAfterBytes { get; init; }

    /// <summary>
    /// Si no es <c>null</c>, el corte de <see cref="CutAfterBytes"/> espera a que la prueba suelte la señal. Sin
    /// ella el corte es inmediato y, en Windows, el cliente puede recibir menos bytes que los mandados (HTTP.sys
    /// corta con un RST que descarta lo no leído): las pruebas que exigen el conteo exacto usan la señal.
    /// </summary>
    public CutSignal? CutSignal { get; init; }

    /// <summary>
    /// Deja de mandar tras estos bytes del cuerpo y se queda colgado sin cerrar hasta que el cliente se
    /// rinda o el servidor se cierre. Sirve para probar plazos de espera. Con 0, <see cref="System.Net.HttpListener"/>
    /// tampoco manda las cabeceras (las manda con el primer byte): el cliente espera la respuesta sin recibirla.
    /// </summary>
    public long? StallAfterBytes { get; init; }

    /// <summary>Espera antes de mandar las cabeceras (servidor lento en contestar).</summary>
    public TimeSpan HeaderDelay { get; init; }

    /// <summary>Tamaño de cada escritura del cuerpo.</summary>
    public int ChunkSize { get; init; } = 64 * 1024;

    /// <summary>Espera entre escrituras del cuerpo (servidor lento al mandar).</summary>
    public TimeSpan ChunkDelay { get; init; }

    /// <summary>Cabeceras extra que se mandan tal cual.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Un archivo servido con 200 (o 206 con <c>Range</c>), con un ETag opcional.</summary>
    public static TestResponse File(ReadOnlyMemory<byte> content, string? etag = null) =>
        new() { Body = content, ETag = etag };

    /// <summary>Una respuesta con ese código y un cuerpo de texto opcional (404, 500, 503…).</summary>
    public static TestResponse Status(int statusCode, string? body = null) => new()
    {
        StatusCode = statusCode,
        Body = body is null ? ReadOnlyMemory<byte>.Empty : System.Text.Encoding.UTF8.GetBytes(body),
        ContentType = "text/plain; charset=utf-8",
        AcceptRanges = false,
    };

    /// <summary>Una redirección (302 por defecto; 301, 303, 307 y 308 también valen).</summary>
    public static TestResponse Redirect(string location, int statusCode = 302) => new()
    {
        StatusCode = statusCode,
        Location = location,
        AcceptRanges = false,
    };
}
