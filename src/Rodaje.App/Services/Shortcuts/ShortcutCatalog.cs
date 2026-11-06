using System.Globalization;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// Todas las acciones con teclado de Rodaje, en el orden en que la app las registra: las del armazón
/// (<see cref="EditorShortcuts"/>) y las de la línea de tiempo, el reproductor, los subtítulos y la ventana. Se arma sin
/// WinUI: la guía <c>docs/guia/atajos.md</c> y la hoja de atajos de Ayuda salen de <see cref="All"/>, y una prueba
/// comprueba que la guía esté al día.
/// </summary>
public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutAction> All { get; } =
    [
        .. EditorShortcuts.Catalog,
        .. TimelineShortcuts.Catalog,
        .. PlayerShortcuts.Catalog,
        .. SubtitleShortcuts.Catalog,
        .. ShellShortcuts.Catalog,
    ];

    /// <summary>Un registro con el catálogo entero y las teclas de fábrica del estilo pedido.</summary>
    public static ShortcutRegistry CreateRegistry(ShortcutPreset preset = ShortcutPreset.Rodaje)
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(All);
        registry.Preset = preset;
        return registry;
    }
}
