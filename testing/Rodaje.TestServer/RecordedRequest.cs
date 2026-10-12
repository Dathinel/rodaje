namespace Rodaje.TestServer;

/// <summary>Una petición que recibió el <see cref="TestHttpServer"/>, para que la prueba compruebe qué pidió el cliente.</summary>
/// <param name="Method">Método HTTP (<c>GET</c>, <c>HEAD</c>…).</param>
/// <param name="Path">Ruta sin la consulta, p. ej. <c>/releases/download/v1/a.paquete</c>.</param>
/// <param name="Range">Cabecera <c>Range</c> tal cual, o <c>null</c>.</param>
/// <param name="IfRange">Cabecera <c>If-Range</c> tal cual, o <c>null</c>.</param>
/// <param name="Headers">Todas las cabeceras de la petición.</param>
/// <param name="StatusCode">Código con el que se contestó.</param>
public sealed record RecordedRequest(
    string Method,
    string Path,
    string? Range,
    string? IfRange,
    IReadOnlyDictionary<string, string> Headers,
    int StatusCode);
