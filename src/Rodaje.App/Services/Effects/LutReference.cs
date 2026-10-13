using Rodaje.Packages;

namespace Rodaje.App.Services.Effects;

/// <summary>
/// Valor de la opción <c>lut</c> de <c>rodaje.color</c>: una referencia <c>paquete@versión/elemento</c> o un archivo
/// <c>.cube</c> suelto, escrito <c>file:</c> más su ruta con <c>/</c>, relativa a la carpeta del <c>.rodaje</c> cuando se
/// puede (si el proyecto se mueve con la LUT, sigue funcionando) y absoluta si no.
/// </summary>
public static class LutReference
{
    public const string FilePrefix = Rodaje.Core.Effects.OptionDefinition.FilePrefix;

    public static bool IsFile(string? reference) => reference is not null && reference.StartsWith(FilePrefix, StringComparison.Ordinal);

    /// <summary>La referencia de un archivo; relativa a la carpeta de <paramref name="projectFile"/> si está en la misma unidad.</summary>
    public static string ForFile(string path, string? projectFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        string? folder = projectFile is null ? null : Path.GetDirectoryName(Path.GetFullPath(projectFile));
        string stored = full;
        if (folder is not null)
        {
            string relative = Path.GetRelativePath(folder, full);
            if (!Path.IsPathRooted(relative))
            {
                stored = relative;
            }
        }

        return FilePrefix + stored.Replace('\\', '/');
    }

    /// <summary>Ruta absoluta del archivo de una referencia <c>file:</c>, o <c>null</c> si no es de archivo o es relativa sin proyecto guardado.</summary>
    public static string? ResolveFile(string? reference, string? projectFolder)
    {
        if (!IsFile(reference) || reference!.Length == FilePrefix.Length)
        {
            return null;
        }

        string path = reference[FilePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        try
        {
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            return projectFolder is null ? null : Path.GetFullPath(Path.Combine(projectFolder, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Nombre para mostrar: el del elemento del paquete, el del archivo o la referencia tal cual.</summary>
    public static string DisplayName(string reference, PackageScan installed)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(installed);
        if (IsFile(reference))
        {
            return Path.GetFileNameWithoutExtension(reference[FilePrefix.Length..].Replace('\\', '/').Split('/')[^1]);
        }

        if (PackageReference.TryParse(reference, out PackageReference? parsed)
            && installed.Find(parsed.PackageId)?.Manifest.FindItem(parsed.ItemId) is { } item)
        {
            return item.Name;
        }

        return reference;
    }
}
