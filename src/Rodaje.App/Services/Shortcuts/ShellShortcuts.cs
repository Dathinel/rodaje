using System.Globalization;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>Las acciones de la ventana: recorrido inicial, pistas, carpetas vigiladas y los espacios de trabajo de fábrica.</summary>
public static class ShellShortcuts
{
    public const string Tour = "help.tour";
    public const string ResetHints = "help.resetHints";
    public const string WatchedFolders = "file.watchedFolders";
    public const string ShortcutSheet = "help.shortcutSheet";
    public const string CheckUpdates = "help.checkUpdates";
    public const string WorkspacePrefix = "view.workspace.";

    /// <summary>El identificador de la acción que aplica un espacio de trabajo de fábrica.</summary>
    public static string Workspace(WorkspaceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return WorkspacePrefix + profile.Id["fabrica.".Length..];
    }

    public static IReadOnlyList<ShortcutAction> Catalog { get; } =
    [
        new(Tour, "Recorrido inicial", ShortcutGroups.View, []) { ShortLabel = "Recorrido" },
        new(ResetHints, "Volver a mostrar las pistas", ShortcutGroups.View, []) { ShortLabel = "Pistas" },
        new(ShortcutSheet, "Hoja de atajos (imprimible)…", ShortcutGroups.View, []) { ShortLabel = "Hoja", Description = "Pregunta dónde guardar una página con todos los atajos del estilo actual, lista para imprimir." },
        new(CheckUpdates, "Buscar actualizaciones…", ShortcutGroups.View, []) { ShortLabel = "Actualizar", Description = "Consulta ahora mismo si hay una versión nueva de Rodaje en GitHub." },
        .. WorkspaceProfiles.Factory.Select(profile => new ShortcutAction(Workspace(profile), $"Espacio de trabajo «{profile.Name}»", ShortcutGroups.View, []) { ShortLabel = profile.Name }),
        new(WatchedFolders, "Carpetas vigiladas…", ShortcutGroups.File, []) { ShortLabel = "Vigiladas" },
    ];
}
