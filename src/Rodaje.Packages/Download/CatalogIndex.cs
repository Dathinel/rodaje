using System.Runtime.InteropServices;

namespace Rodaje.Packages.Download;

/// <summary>
/// Cómo se llama el índice firmado de cada sistema en la release del catálogo (D-066). El de Windows no cambia
/// (<c>indice.json</c>): los editores ya publicados lo siguen leyendo y no ven nada de otros sistemas. Linux y macOS leen
/// el suyo (<c>indice-linux-x64.json</c>, <c>indice-osx-arm64.json</c>), con su firma en el mismo nombre más <c>.sig</c>.
/// </summary>
public static class CatalogIndex
{
    public const string WindowsFileName = "indice.json";

    /// <summary>El índice de un RID (<c>win-x64</c>, <c>linux-x64</c>, <c>osx-arm64</c>).</summary>
    public static string FileName(string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        if (runtimeIdentifier.StartsWith("win", StringComparison.Ordinal))
        {
            return WindowsFileName;
        }

        return Identifiers.IsValid(runtimeIdentifier)
            ? $"indice-{runtimeIdentifier}.json"
            : throw new ArgumentException($"«{runtimeIdentifier}» no es un RID válido.", nameof(runtimeIdentifier));
    }

    /// <summary>El índice de este sistema.</summary>
    public static string ForThisSystem =>
        OperatingSystem.IsWindows() ? WindowsFileName : FileName(RuntimeInformation.RuntimeIdentifier);
}
