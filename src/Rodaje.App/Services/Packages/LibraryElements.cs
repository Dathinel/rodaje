using Rodaje.Core.Model;
using Rodaje.Paquetes;

namespace Rodaje.App.Services.Packages;

/// <summary>Categorías del riel de la biblioteca.</summary>
public enum LibraryCategory
{
    Media,
    Audio,
    Text,
    Effects,
    Transitions,
    Filters,
    Ai,
}

/// <summary>
/// Un elemento de la biblioteca que no es un medio: algo del motor, de un paquete instalado o hecho por la persona.
/// <see cref="Reference"/> es lo que se arrastra a la línea de tiempo: el tipo de efecto del motor (por ejemplo
/// <c>rodaje.transform</c>) o la referencia estable <c>paquete@versión/elemento</c>.
/// </summary>
public sealed record LibraryElement(string Name, string Description, string Source, string Reference, bool IsAvailable, PackageOrigin? Origin)
{
    public const string DragFormat = "rodaje/elemento";
}

/// <summary>Qué muestra cada categoría: lo que trae el motor y lo que aportan los paquetes.</summary>
public static class LibraryElements
{
    private const string Engine = "Rodaje";
    private const string Soon = "Llega con la fase 5.";
    private const string NotRendered = "Se aplica y se guarda; la vista previa lo muestra desde la fase 5.";

    public static IReadOnlyList<ItemKind> KindsOf(LibraryCategory category) => category switch
    {
        LibraryCategory.Audio => [ItemKind.Sound],
        LibraryCategory.Text => [ItemKind.TextStyle],
        LibraryCategory.Effects => [ItemKind.Effect],
        LibraryCategory.Transitions => [ItemKind.Transition],
        LibraryCategory.Filters => [ItemKind.Filter, ItemKind.Lut],
        _ => [],
    };

    /// <summary>
    /// Los efectos del motor (<see cref="BuiltInEffects"/>) que se ofrecen en cada categoría; lo que el modelo todavía no
    /// tiene (texto, transiciones) se muestra como «Próximamente».
    /// </summary>
    public static IReadOnlyList<LibraryElement> BuiltIn(LibraryCategory category) => category switch
    {
        LibraryCategory.Text =>
        [
            new("Texto", "Fuente, color, borde y sombra. " + Soon, Engine, "rodaje.text", false, null),
        ],
        LibraryCategory.Effects =>
        [
            new("Transformar", "Posición, escala, rotación y opacidad, con keyframes.", Engine, BuiltInEffects.Transform, true, null),
            new("Desenfoque", "Desenfoque gaussiano. " + NotRendered, Engine, BuiltInEffects.Blur, true, null),
            new("Nitidez", "Realza los bordes. " + NotRendered, Engine, BuiltInEffects.Sharpen, true, null),
            new("Viñeta", "Oscurece las esquinas. " + NotRendered, Engine, BuiltInEffects.Vignette, true, null),
            new("Velocidad", "Cámara lenta o rápida. " + NotRendered, Engine, BuiltInEffects.Speed, true, null),
            new("Estabilizar", "Quita el temblor de la cámara. " + NotRendered, Engine, BuiltInEffects.Stabilize, true, null),
        ],
        LibraryCategory.Transitions =>
        [
            new("Fundido cruzado", "Mezcla un clip con el siguiente. " + Soon, Engine, "rodaje.crossfade", false, null),
            new("Fundido a negro", "Baja a negro y sube desde negro. " + Soon, Engine, "rodaje.fade-black", false, null),
        ],
        LibraryCategory.Filters =>
        [
            new("Color y LUT", "Exposición, contraste, saturación, temperatura y tabla de color .cube. " + NotRendered, Engine, BuiltInEffects.Color, true, null),
        ],
        _ => [],
    };

    /// <summary>Título de la sección con lo propio de cada categoría.</summary>
    public static string OwnTitle(LibraryCategory category) => category switch
    {
        LibraryCategory.Text => "Mis estilos de texto",
        LibraryCategory.Effects => "Mis efectos",
        LibraryCategory.Filters => "Mis filtros",
        LibraryCategory.Audio => "Mis sonidos",
        _ => "Mis transiciones",
    };

    /// <summary>Lo del motor seguido de lo de los paquetes y lo propio.</summary>
    public static IReadOnlyList<LibraryElement> For(LibraryCategory category, PackageScan installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var elements = new List<LibraryElement>(BuiltIn(category));
        foreach (ItemKind kind in KindsOf(category))
        {
            foreach (PackageItemEntry entry in installed.Items(kind))
            {
                string source = entry.Package.Origin == PackageOrigin.Own ? "Mío" : entry.Package.Manifest.Name;
                elements.Add(new LibraryElement(entry.Item.Name, entry.Item.Description ?? "", source, entry.Reference.ToString(), true, entry.Package.Origin));
            }
        }

        return elements;
    }
}
