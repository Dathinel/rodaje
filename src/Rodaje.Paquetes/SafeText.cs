using System.Text;
using System.Text.Json;

namespace Rodaje.Paquetes;

/// <summary>
/// Texto y direcciones que vienen de un paquete y se muestran en la interfaz. Sin caracteres de control ni de dirección
/// (bidi), que pueden disfrazar un nombre o una extensión, y con largo máximo.
/// </summary>
public static class SafeText
{
    public const int MaxName = 80;
    public const int MaxDescription = 500;
    public const int MaxLicense = 64;
    public const int MaxUri = 512;

    /// <summary>Quita controles y marcas de dirección; si <paramref name="multiline"/>, conserva los saltos de línea.</summary>
    public static string Clean(string text, bool multiline = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (char c in text.ReplaceLineEndings("\n"))
        {
            if (c is '\n' or '\t')
            {
                builder.Append(multiline && c == '\n' ? '\n' : ' ');
            }
            else if (!char.IsControl(c) && !IsBidiControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>Marcas y controles de dirección de Unicode (LRM, RLM, ALM, LRE…RLO y LRI…PDI).</summary>
    public static bool IsBidiControl(char c) =>
        c is '؜' or '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');

    /// <summary>Corta a <paramref name="max"/> caracteres con «…», sin partir un par sustituto.</summary>
    public static string Truncate(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= max)
        {
            return text;
        }

        int cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>
    /// Una dirección https sin usuario ni contraseña (<c>https://github.com@otro.sitio</c>) y con el dominio en ASCII:
    /// los dominios internacionales (<see cref="Uri.IdnHost"/>, <c>xn--</c>) permiten imitar a otro con letras parecidas.
    /// </summary>
    public static bool TryParseHttpsUri(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrEmpty(text) || text.Length > MaxUri || !Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        string host;
        try
        {
            host = parsed.IdnHost;
        }
        catch (UriFormatException)
        {
            // Un dominio con caracteres que la conversión a ASCII no acepta (por ejemplo U+FFFD): no es una dirección válida.
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps || parsed.UserInfo.Length > 0 || host.Length == 0
            || !string.Equals(host, parsed.Host, StringComparison.OrdinalIgnoreCase)
            || !Ascii.IsValid(host)
            || host.Split('.').Any(label => label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>
    /// Si algún texto o nombre de propiedad del JSON no se puede leer como cadena: UTF-8 inválido o un sustituto suelto
    /// escrito como escape (<c>\ud800</c>). <see cref="JsonElement.GetString"/> lanza <see cref="InvalidOperationException"/>
    /// con ellos, así que los lectores lo comprueban una vez, justo tras interpretar el documento.
    /// </summary>
    public static bool HasUnreadableString(JsonElement element)
    {
        try
        {
            Walk(element);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void Walk(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                _ = element.GetString();
                break;
            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                {
                    Walk(child);
                }

                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    _ = property.Name;
                    Walk(property.Value);
                }

                break;
        }
    }
}
