using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Services.MotionTemplates;

/// <summary>Una plantilla de movimiento lista para usar: su documento ya validado y el texto tal cual, que es lo que guarda el proyecto.</summary>
public sealed record MotionTemplateEntry(string Reference, string Name, string Description, string Source, PackageOrigin Origin,
    TemplateDocument Document, string DocumentText)
{
    public TemplateKind Kind => Document.Kind;

    /// <summary>Lo que va en <c>template.source</c> del proyecto: la referencia, o nulo si es propia.</summary>
    public string? ProjectSource => Origin == PackageOrigin.Own ? null : Reference;
}

/// <summary>
/// Las plantillas de movimiento (D-064) de los paquetes instalados y las de <c>propios</c>. Una que no se puede leer no
/// sale en la lista. Lee archivos: se llama fuera del hilo de la interfaz.
/// </summary>
public static class MotionTemplateCatalog
{
    public static IReadOnlyList<MotionTemplateEntry> List(PackageScan installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var entries = new List<MotionTemplateEntry>();
        foreach (PackageItemEntry item in installed.Items(ItemKind.MotionTemplate))
        {
            if (Read(item) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>La plantilla instalada de esa referencia; como el resto del contenido, sirve otra versión del mismo paquete.</summary>
    public static MotionTemplateEntry? Find(PackageScan installed, string? reference)
    {
        ArgumentNullException.ThrowIfNull(installed);
        return PackageReference.TryParse(reference ?? "", out PackageReference? parsed)
            && installed.Find(parsed.PackageId) is { } package
            && package.Manifest.FindItem(parsed.ItemId, ItemKind.MotionTemplate) is { } item
            ? Read(new PackageItemEntry(package, item))
            : null;
    }

    /// <summary>La plantilla instalada de <paramref name="source"/> si su paquete tiene una versión más nueva que la del proyecto.</summary>
    public static MotionTemplateEntry? Newer(PackageScan installed, string? source) =>
        PackageReference.TryParse(source ?? "", out PackageReference? used) && Find(installed, source) is { } entry
        && PackageReference.TryParse(entry.Reference, out PackageReference? current) && current.Version.CompareTo(used.Version) > 0
            ? entry
            : null;

    private static MotionTemplateEntry? Read(PackageItemEntry item)
    {
        if (item.FilePath is not { } path)
        {
            return null;
        }

        try
        {
            if (new FileInfo(path).Length > TemplateLimits.MaxFileBytes)
            {
                return null;
            }

            string text = File.ReadAllText(path);
            TemplateDocument document = ContentReaders.ReadTemplate(text);
            bool own = item.Package.Origin == PackageOrigin.Own;
            return new MotionTemplateEntry(item.Reference.ToString(), item.Item.Name, item.Item.Description ?? "",
                own ? "Mío" : item.Package.Manifest.Name, item.Package.Origin, document, text);
        }
        catch (Exception ex) when (ex is PackageContentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
