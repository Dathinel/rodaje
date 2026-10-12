using Rodaje.Packages;

namespace Rodaje.App.Services.Titles;

/// <summary>
/// Los archivos de los elementos <c>font</c> de los paquetes instalados (<c>rodaje-fuentes</c> primero), para la
/// colección privada del rasterizador de la vista previa y de la exportación. Solo los que el paquete declara como fuente,
/// con su licencia y dentro de sus topes; nunca otros archivos del paquete ni fuentes sueltas de un proyecto (D-029). Las
/// del sistema las encuentra DirectWrite sin registrarlas.
/// </summary>
public static class TitleFontFiles
{
    public const string FontsPackage = "rodaje-fuentes";

    /// <summary>Tope de archivos: la colección se arma de una vez y cada uno se analiza.</summary>
    public const int MaxFiles = 256;

    public static IReadOnlyList<string> Find(PackageScan installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var files = new List<string>();
        foreach (InstalledPackage package in installed.Packages.OrderBy(p => p.Id == FontsPackage ? 0 : 1).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            string root = Path.GetFullPath(package.Directory);
            foreach (PackageItem font in package.Manifest.Items.Where(i => i.Kind == ItemKind.Font))
            {
                foreach (string relative in font.Files ?? [])
                {
                    if (!SafePaths.TryNormalizeRelative(relative, out string? normalized))
                    {
                        continue;
                    }

                    string full = Path.GetFullPath(Path.Combine(root, normalized));
                    if (SafePaths.IsInside(full, root) && File.Exists(full) && !files.Contains(full, StringComparer.OrdinalIgnoreCase))
                    {
                        files.Add(full);
                        if (files.Count == MaxFiles)
                        {
                            return files;
                        }
                    }
                }
            }
        }

        return files;
    }
}
