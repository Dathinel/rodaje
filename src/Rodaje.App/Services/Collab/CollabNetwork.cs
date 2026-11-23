using System.Diagnostics;
using System.Net;
using Dathinel.Sala;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// Lo que Preferencias › Colaboración decide sobre la sala: el relé (públicos de n0 o uno propio, con su
/// comprobación), el nombre por defecto y la carpeta por defecto de las copias. La red se lee de los
/// ajustes cada vez que se abre o se entra en una sala, para que cambiarla no exija reiniciar.
/// </summary>
public static class CollabNetwork
{
    /// <summary>La petición de «Probar conexión»: un GET de {relé}/ping, que los relés de iroh sirven para medir distancia.</summary>
    public const string ProbePath = "ping";

    /// <summary>Lo máximo que tarda la comprobación entera (conectar, TLS y cabeceras).</summary>
    public static TimeSpan ProbeTimeout { get; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// La red con la que se abre o se entra en una sala según los ajustes: «n0» por defecto, o la
    /// dirección del relé propio si está puesta. <see cref="Red.SoloRele"/> sale de la casilla
    /// «Solo por relé» de Preferencias.
    /// </summary>
    /// <exception cref="CollabException">La dirección del relé propio guardada no vale.</exception>
    public static Red ForSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.CollabRelayUrl is { Length: > 0 } raw)
        {
            if (TryNormalizeRelayUrl(raw, out string? url, out string? problem) && url is not null)
            {
                return new Red(url, settings.CollabRelayOnly);
            }

            throw new CollabException("La dirección del relé propio guardada en Preferencias › Colaboración no vale: " + problem);
        }

        return new Red("n0", settings.CollabRelayOnly);
    }

    /// <summary>
    /// Dice si una dirección de relé vale — «https://» con un servidor válido, sin usuario ni
    /// contraseña y sin ruta, «?» ni «#» — y la deja en su forma corta (host en minúsculas,
    /// «/» al final) para dársela a <see cref="Red"/>.
    /// </summary>
    public static bool TryNormalizeRelayUrl(string? raw, out string? url, out string? problem)
    {
        url = null;
        problem = null;
        string text = raw?.Trim() ?? "";
        if (text.Length == 0)
        {
            problem = "Escribe la dirección del relé (empieza por «https://»).";
            return false;
        }

        if (!text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            problem = "La dirección del relé tiene que empezar por «https://».";
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
        {
            problem = "Esa dirección no se puede leer como una dirección web.";
            return false;
        }

        if (uri.UserInfo.Length > 0)
        {
            problem = "La dirección del relé no puede llevar usuario ni contraseña.";
            return false;
        }

        if (uri.Host.Length == 0 || uri.HostNameType == UriHostNameType.Unknown)
        {
            problem = "El servidor de esa dirección no es un nombre ni una IP válidos.";
            return false;
        }

        if (uri.LocalPath is not ("" or "/") || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            problem = "La dirección del relé es solo el servidor (y si hace falta el puerto): sin ruta, sin «?» y sin «#».";
            return false;
        }

        url = uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// «Probar conexión»: un GET corto de {relé}/ping sin mandar nada del usuario. Dice si un relé
    /// de iroh contestó; se puede cancelar y, con todo, espera como mucho <see cref="ProbeTimeout"/>.
    /// </summary>
    public static async Task<RelayProbeResult> ProbeAsync(Uri relay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relay);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        using var http = new HttpClient(handler) { Timeout = ProbeTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Rodaje/" + CollabHost.CurrentVersion());
        long start = Stopwatch.GetTimestamp();
        try
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri(relay, ProbePath), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new RelayProbeResult(true, string.Create(SpanishNumbers.Format, $"El relé responde (en {Stopwatch.GetElapsedTime(start).TotalSeconds:0.0} s)."))
                : new RelayProbeResult(false, $"El servidor contesta (HTTP {(int)response.StatusCode}), pero no como un relé de iroh. Comprueba la dirección.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RelayProbeResult(false, ex is TaskCanceledException
                ? string.Create(SpanishNumbers.Format, $"No respondió en {ProbeTimeout.TotalSeconds:0} segundos. Comprueba que el relé está encendido y que la dirección es la buena.")
                : "No se pudo conectar con el relé: " + ex.Message);
        }
    }

    /// <summary>
    /// Carpeta por defecto de las copias de sala: la de Preferencias si está puesta y es una ruta
    /// completa; si no, «Documentos\Rodaje\Salas». (Que exista se comprueba al abrirla, en el
    /// diálogo de unirse.)
    /// </summary>
    public static string DefaultRoomsFolder(AppSettings? settings) =>
        settings?.CollabRoomsFolder is { Length: > 0 } folder && Path.IsPathRooted(folder)
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rodaje", "Salas");
}

/// <summary>Lo que contesta «Probar conexión»: si un relé de iroh respondió y el texto para mostrar.</summary>
public sealed record RelayProbeResult(bool Ok, string Text);
