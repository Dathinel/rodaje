namespace Rodaje.Packages.Download;

/// <summary>
/// De dónde puede salir un archivo del índice. Por defecto: un asset de una release de GitHub de los dueños dados
/// (<c>github.com/&lt;dueño&gt;/&lt;repo&gt;/releases/download/&lt;tag&gt;/&lt;archivo&gt;</c>) o un archivo de Hugging Face
/// fijado por commit (<c>huggingface.co/&lt;repo&gt;/resolve/&lt;40 hex&gt;/&lt;archivo&gt;</c>), siempre por https. Las
/// redirecciones (a los CDN de GitHub y Hugging Face) se siguen sin fijar el host: lo que protege es el hash firmado.
/// </summary>
public sealed class DownloadUrlPolicy
{
    private readonly HashSet<string> _owners;
    private readonly bool _loopback;

    private DownloadUrlPolicy(IEnumerable<string> owners, bool loopback)
    {
        _owners = new HashSet<string>(owners, StringComparer.OrdinalIgnoreCase);
        _loopback = loopback;
    }

    /// <summary>Releases de Dathinel y Hugging Face fijado por commit.</summary>
    public static DownloadUrlPolicy Default { get; } = new(["Dathinel"], loopback: false);

    /// <summary>Releases de GitHub de estos dueños y Hugging Face fijado por commit.</summary>
    public static DownloadUrlPolicy ForOwners(params string[] owners) => new(owners, loopback: false);

    /// <summary>Solo para pruebas: además acepta cualquier dirección http o https de este equipo (127.0.0.1, localhost).</summary>
    public static DownloadUrlPolicy WithLoopback(params string[] owners) => new(owners, loopback: true);

    /// <summary>Si se puede descargar de <paramref name="url"/>; si no, <paramref name="reason"/> dice por qué.</summary>
    public bool IsAllowed(Uri url, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(url);
        reason = null;
        if (_loopback && url.IsAbsoluteUri && url.IsLoopback && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        // Solo el puerto de https (443, escrito o no): https://github.com:8443/… es otro servidor.
        if (!SafeText.TryParseHttpsUri(url.OriginalString, out Uri? parsed) || parsed.Query.Length > 0 || parsed.Fragment.Length > 0 || parsed.Port != 443)
        {
            reason = "La dirección no es https (puerto 443) o tiene partes no permitidas.";
            return false;
        }

        string[] segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (string.Equals(parsed.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length == 6 && _owners.Contains(segments[0]) && segments[2] == "releases" && segments[3] == "download")
            {
                return true;
            }

            reason = "Solo se descarga de las releases de GitHub de " + string.Join(", ", _owners) + ".";
            return false;
        }

        if (string.Equals(parsed.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase))
        {
            int resolve = Array.IndexOf(segments, "resolve");
            if (resolve >= 2 && segments.Length > resolve + 2 && IsCommit(segments[resolve + 1]))
            {
                return true;
            }

            reason = "Un modelo de Hugging Face tiene que estar fijado por commit (…/resolve/<commit de 40 caracteres>/…).";
            return false;
        }

        reason = $"«{parsed.Host}» no es un origen permitido.";
        return false;
    }

    private static bool IsCommit(string text) => text.Length == 40 && text.All(char.IsAsciiHexDigitLower);
}
