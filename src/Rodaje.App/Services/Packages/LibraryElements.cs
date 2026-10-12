using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Model;
using Rodaje.Packages;
using Rodaje.Packages.Content;

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
/// <see cref="Reference"/> es lo que se arrastra a la línea de tiempo: el tipo de efecto o transición del motor (por
/// ejemplo <c>rodaje.transform</c>) o la referencia estable <c>paquete@versión/elemento</c>. <see cref="Kind"/> dice cómo
/// se aplica.
/// </summary>
public sealed record LibraryElement(string Name, string Description, string Source, string Reference, bool IsAvailable, PackageOrigin? Origin, ItemKind Kind)
{
    public const string DragFormat = "rodaje/elemento";

    /// <summary>Es algo del motor (un tipo del registro) y no de un paquete.</summary>
    public bool IsBuiltIn => Origin is null;
}

/// <summary>Qué muestra cada categoría: lo que trae el motor y lo que aportan los paquetes.</summary>
public static class LibraryElements
{
    private const string Engine = "Rodaje";

    public static IReadOnlyList<ItemKind> KindsOf(LibraryCategory category) => category switch
    {
        LibraryCategory.Audio => [ItemKind.Sound],
        LibraryCategory.Text => [ItemKind.TextStyle],
        LibraryCategory.Effects => [ItemKind.Effect],
        LibraryCategory.Transitions => [ItemKind.Transition],
        LibraryCategory.Filters => [ItemKind.Filter, ItemKind.Lut],
        _ => [],
    };

    /// <summary>Lo que trae el motor en cada categoría, sacado del registro de efectos; en Texto, el estilo de fábrica.</summary>
    public static IReadOnlyList<LibraryElement> BuiltIn(LibraryCategory category) => category switch
    {
        LibraryCategory.Text =>
        [
            new("Texto", "Un título en el cursor: blanco, en negrita y con sombra.", Engine, TitleStyles.BuiltInReference, true, null, ItemKind.TextStyle),
        ],
        LibraryCategory.Effects =>
        [
            .. EffectCatalog.For(TrackKind.Video).Where(e => e.Type != BuiltInEffects.Color)
                .Select(e => new LibraryElement(e.Name, EffectUi.Description(e.Type), Engine, e.Type, true, null, ItemKind.Effect)),
        ],
        LibraryCategory.Transitions =>
        [
            .. TransitionEditor.BuiltInTypes
                .Select(t => new LibraryElement(EffectUi.Name(t.Type), EffectUi.Description(t.Type), Engine, t.Type, true, null, ItemKind.Transition)),
        ],
        LibraryCategory.Filters =>
        [
            new(EffectUi.Name(BuiltInEffects.Color), EffectUi.Description(BuiltInEffects.Color), Engine, BuiltInEffects.Color, true, null, ItemKind.Filter),
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

    /// <summary>
    /// Lo del motor seguido de lo de los paquetes y lo propio. En Filtros no salen las LUT que ya usa un filtro de su mismo
    /// paquete (el look se ve una vez, como filtro); las LUT sueltas sí.
    /// </summary>
    public static IReadOnlyList<LibraryElement> For(LibraryCategory category, PackageScan installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var elements = new List<LibraryElement>(BuiltIn(category));
        HashSet<(string Package, string Item)> covered = category == LibraryCategory.Filters ? LutsUsedByFilters(installed) : [];
        foreach (ItemKind kind in KindsOf(category))
        {
            foreach (PackageItemEntry entry in installed.Items(kind))
            {
                if (kind == ItemKind.Lut && covered.Contains((entry.Package.Id, entry.Item.Id)))
                {
                    continue;
                }

                string source = entry.Package.Origin == PackageOrigin.Own ? "Mío" : entry.Package.Manifest.Name;
                elements.Add(new LibraryElement(entry.Item.Name, entry.Item.Description ?? "", source, entry.Reference.ToString(), true, entry.Package.Origin, kind));
            }
        }

        return elements;
    }

    private static HashSet<(string Package, string Item)> LutsUsedByFilters(PackageScan installed)
    {
        var used = new HashSet<(string, string)>();
        var loader = new InstalledContentLoader(() => installed);
        foreach (PackageItemEntry entry in installed.Items(ItemKind.Filter))
        {
            FilterPreset? filter;
            try
            {
                filter = loader.LoadFilter(entry.Reference.ToString());
            }
            catch (PackageContentException)
            {
                continue;
            }

            foreach (PresetEffect effect in filter?.Effects ?? [])
            {
                foreach (string value in effect.Options.Values)
                {
                    if (PackageReference.TryParse(value, out PackageReference? reference) && reference.PackageId == entry.Package.Id)
                    {
                        used.Add((reference.PackageId, reference.ItemId));
                    }
                }
            }
        }

        return used;
    }
}
