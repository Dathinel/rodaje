namespace Rodaje.App.Services.Shortcuts;

/// <summary>Importar y exportar la línea de tiempo en EDL, FCPXML u OpenTimelineIO (D-062). Sin tecla de fábrica.</summary>
public static class InterchangeShortcuts
{
    public const string Import = "file.importTimeline";
    public const string Export = "file.exportTimeline";

    public static IReadOnlyList<ShortcutAction> Catalog { get; } =
    [
        new(Import, "Importar línea de tiempo", ShortcutGroups.File, []) { Description = "Trae un montaje en EDL, FCPXML u OpenTimelineIO como clip compuesto en pistas nuevas." },
        new(Export, "Exportar línea de tiempo", ShortcutGroups.File, []) { Description = "Guarda la secuencia como EDL, FCPXML u OpenTimelineIO para otro editor." },
    ];
}
