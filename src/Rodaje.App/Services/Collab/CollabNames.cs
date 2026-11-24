namespace Rodaje.App.Services.Collab;

/// <summary>Claves y nombres de la sala: lo que se acepta y cómo se limpia lo que escribe la persona.</summary>
public static class CollabNames
{
    public const int MaxAlias = 40;

    public static bool IsKey(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsAlias(string? s) => s is { Length: >= 1 and <= MaxAlias } && !s.Any(char.IsControl);

    /// <summary>Quita espacios y caracteres de control; <c>null</c> si queda vacío o pasa de 40 caracteres.</summary>
    public static string? NormalizeAlias(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        string clean = string.Join(' ', new string([.. raw.Where(c => !char.IsControl(c) || char.IsWhiteSpace(c))])
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return IsAlias(clean) ? clean : null;
    }
}
