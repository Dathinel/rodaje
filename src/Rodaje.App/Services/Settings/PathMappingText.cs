using Rodaje.Core.Model;

namespace Rodaje.App.Services.Settings;

/// <summary>El mapeo de rutas como lo escribe Preferencias: una fila por línea, «origen | destino».</summary>
public static class PathMappingText
{
    public static string Format(IEnumerable<PathMapping> mappings) =>
        string.Join('\n', mappings.Select(m => $"{m.From} | {m.To}"));

    /// <summary>Las filas con origen y destino; las líneas vacías o sin «|» no cuentan.</summary>
    public static List<PathMapping> Parse(string? text) =>
        [.. (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            .Select(parts => new PathMapping(parts[0], parts[1]))];
}