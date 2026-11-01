using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>Grupos de la página de atajos, en el orden en que se muestran.</summary>
public static class ShortcutGroups
{
    public const string Playback = "Reproducción";
    public const string Edit = "Edición";
    public const string Timeline = "Línea de tiempo";
    public const string Tools = "Herramientas";
    public const string View = "Vista";
    public const string File = "Archivo";

    /// <summary>Páginas y paneles: ahora forman parte de <see cref="View"/>.</summary>
    public const string Windows = View;

    /// <summary>Orden de la página; los grupos que registren las vistas y no estén aquí van al final.</summary>
    public static IReadOnlyList<string> Order { get; } = [Playback, Edit, Timeline, Tools, View, File];
}
