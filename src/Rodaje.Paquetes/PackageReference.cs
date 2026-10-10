namespace Rodaje.Paquetes;

/// <summary>
/// Referencia estable a un elemento de un paquete, <c>id@versión/elemento</c>, como la guarda un proyecto. Si el paquete
/// falta al abrir el proyecto, la referencia dice cuál instalar.
/// </summary>
public sealed record PackageReference(string PackageId, PackageVersion Version, string ItemId)
{
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PackageReference? reference)
    {
        reference = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        int at = text.IndexOf('@', StringComparison.Ordinal);
        int slash = text.IndexOf('/', StringComparison.Ordinal);
        if (at <= 0 || slash < at + 2 || slash == text.Length - 1)
        {
            return false;
        }

        string id = text[..at];
        string item = text[(slash + 1)..];
        if (!Identifiers.IsValid(id) || !Identifiers.IsValid(item) || !PackageVersion.TryParse(text[(at + 1)..slash], out PackageVersion? version))
        {
            return false;
        }

        reference = new PackageReference(id, version, item);
        return true;
    }

    public override string ToString() => $"{PackageId}@{Version}/{ItemId}";
}

/// <summary>Reglas de los identificadores de paquetes y elementos.</summary>
public static class Identifiers
{
    public const int MaxLength = 64;

    /// <summary>
    /// Minúsculas ASCII, dígitos y guiones o puntos sueltos entre ellos: <c>rodaje-transiciones</c>, <c>zoom.suave</c>. Un id
    /// es también un nombre de carpeta, así que no puede ser un nombre reservado de Windows (<c>con</c>, <c>nul.x</c>…).
    /// </summary>
    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength)
        {
            return false;
        }

        bool previousSeparator = true;
        foreach (char c in id)
        {
            bool separator = c is '-' or '.';
            if (separator ? previousSeparator : !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
            {
                return false;
            }

            previousSeparator = separator;
        }

        return !previousSeparator && !SafePaths.IsReservedName(id);
    }
}
