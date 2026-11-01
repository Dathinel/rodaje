using System.Globalization;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>Importar y exportar archivos de subtítulos. Sin tecla de fábrica.</summary>
public static class SubtitleShortcuts
{
    public const string Import = "file.importSubtitles";
    public const string Export = "file.exportSubtitles";

    public static IReadOnlyList<ShortcutAction> Catalog { get; } =
    [
        new(Import, "Importar subtítulos", ShortcutGroups.File, []) { Description = "Trae un archivo .srt o .vtt a una pista de subtítulos nueva." },
        new(Export, "Exportar subtítulos", ShortcutGroups.File, []) { Description = "Guarda la pista de subtítulos como archivo .srt o .vtt." },
    ];
}
