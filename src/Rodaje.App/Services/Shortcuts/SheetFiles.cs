namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// Nombres y filtros de las hojas de atajos que se guardan con el diálogo de archivo: la hoja rápida del panel
/// (<see cref="CheatSheet"/>) y la completa de Ayuda (<see cref="ShortcutGuide.ToHtml"/>).
/// </summary>
public static class SheetFiles
{
    /// <summary>El nombre, sin extensión, con que se propone guardar la hoja rápida.</summary>
    public const string QuickSheetStem = "Atajos de Rodaje";

    /// <summary>El nombre con que se propone guardar la hoja completa de Ayuda.</summary>
    public const string FullSheetName = "Atajos de Rodaje (completa).html";

    public static readonly (string Name, string Spec) HtmlFilter = ("Página web (HTML)", "*.html;*.htm");

    public static readonly (string Name, string Spec) PngFilter = ("Imagen (PNG)", "*.png");

    /// <summary>La extensión, con punto, de cada formato.</summary>
    public static string Extension(SheetFormat format) => format == SheetFormat.Png ? ".png" : ".html";

    /// <summary>«Atajos de Rodaje.html» o «Atajos de Rodaje.png»: lo que propone el diálogo para la hoja rápida.</summary>
    public static string QuickSheetName(SheetFormat format) => QuickSheetStem + Extension(format);

    /// <summary>El filtro del diálogo para ese formato.</summary>
    public static (string Name, string Spec) Filter(SheetFormat format) => format == SheetFormat.Png ? PngFilter : HtmlFilter;

    /// <summary>El título del diálogo de guardar de la hoja rápida.</summary>
    public static string DialogTitle(SheetFormat format) =>
        format == SheetFormat.Png ? "Guardar la hoja rápida como imagen" : "Guardar la hoja rápida como página";
}
