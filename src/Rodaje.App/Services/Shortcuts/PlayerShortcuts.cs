using System.Globalization;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>Las acciones del reproductor (además de las de reproducción del armazón).</summary>
public static class PlayerShortcuts
{
    public const string FullScreen = "view.playerFullScreen";
    public const string SafeAreas = "view.safeAreas";
    public const string ProjectFormat = "project.format";
    public const string SecondScreen = "view.secondScreen";

    public static IReadOnlyList<ShortcutAction> Catalog { get; } =
    [
        new(ActionIds.ShuttleReverse, "Lanzadera hacia atrás (más rápido con cada pulsación)", ShortcutGroups.Playback, ["J"]) { ShortLabel = "Atrás" },
        new(ActionIds.ShuttleStop, "Lanzadera: detener", ShortcutGroups.Playback, ["K"]) { ShortLabel = "Detener" },
        new(ActionIds.ShuttleForward, "Lanzadera hacia adelante (más rápido con cada pulsación)", ShortcutGroups.Playback, ["L"]) { ShortLabel = "Adelante" },
        new(FullScreen, "Reproductor en pantalla completa", ShortcutGroups.View, ["F"]) { ShortLabel = "Pantalla completa" },
        new(SafeAreas, "Mostrar u ocultar las zonas seguras", ShortcutGroups.View, []) { ShortLabel = "Zonas seguras" },
        new(ProjectFormat, "Formato del proyecto…", ShortcutGroups.View, []) { ShortLabel = "Formato" },
        new(SecondScreen, "Visor en otra pantalla (abrir o cerrar)", ShortcutGroups.View, []) { ShortLabel = "Otra pantalla" },
    ];
}
