using Rodaje.Media;
using Rodaje.Media.Proxies;
using Rodaje.Paquetes;

namespace Rodaje.App.Services.Modules;

/// <summary>
/// El complemento <c>rodaje-codificadores-gpl</c> (un <c>paquete.json</c> de módulo con <c>protocol: 0</c>: una herramienta
/// de línea de comandos, no un módulo del protocolo). Si está instalado, Exportar ofrece x264 y x265 en «Avanzado» y los
/// codifica con su <c>ffmpeg.exe</c> GPL en otro proceso.
/// </summary>
public static class GplEncoderPack
{
    public const string Id = "rodaje-codificadores-gpl";

    /// <summary>La ruta del <c>ffmpeg.exe</c> del complemento instalado, o <c>null</c>. Solo lee el disco.</summary>
    public static string? FindExecutable(IEnumerable<ModuleRoot>? roots = null)
    {
        foreach (ModuleRoot root in roots ?? ModuleCatalog.DefaultRoots())
        {
            string manifest = Path.Combine(root.Path, Id, PackageManifest.FileName);
            if (!File.Exists(manifest))
            {
                continue;
            }

            ManifestResult result = ManifestReader.ReadFile(manifest, checkFiles: true);
            if (result.Manifest is { Id: Id, Kind: PackageKind.Module, Module: { Protocol: 0 } module }
                && Path.GetFullPath(Path.Combine(root.Path, Id, module.Entry)) is { } exe && File.Exists(exe))
            {
                return exe;
            }
        }

        return null;
    }

    /// <summary>El exe verificado (GPL o LGPL, nunca <c>nonfree</c>, de la serie de Rodaje), o <c>null</c> con el motivo.</summary>
    public static FfmpegExecutable? TryOpen(IEnumerable<ModuleRoot>? roots, out string? problem)
    {
        problem = null;
        if (FindExecutable(roots) is not { } exe)
        {
            return null;
        }

        try
        {
            return FfmpegExecutable.OpenGplAddOn(exe);
        }
        catch (FfmpegNotFoundException ex)
        {
            problem = ex.Message;
            return null;
        }
    }
}
