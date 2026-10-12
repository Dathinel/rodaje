using Rodaje.Paquetes;

namespace Rodaje.App.Services.Titles;

/// <summary>
/// Los archivos de fuente de los paquetes instalados (<c>rodaje-fuentes</c> primero), para la colección privada del
/// rasterizador de la vista previa y de la exportación. Nunca fuentes sueltas de un proyecto (D-029); las del sistema las
/// encuentra DirectWrite sin registrarlas.
/// </summary>
public static class TitleFontFiles
{
    public const string FontsPackage = "rodaje-fuentes";

    /// <summary>Tope de archivos: la colección se arma de una vez y cada uno se analiza.</summary>
    public const int MaxFiles = 256;

    private static readonly string[] Extensions = [".ttf", ".otf", ".ttc", ".otc"];

    public static IReadOnlyList<string> Find(PackageScan installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var files = new List<string>();
        foreach (InstalledPackage package in installed.Packages.OrderBy(p => p.Id == FontsPackage ? 0 : 1).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(package.Directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    if (Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    {
                        files.Add(file);
                        if (files.Count == MaxFiles)
                        {
                            return files;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return files;
    }
}
