using Rodaje.Packages;

namespace Rodaje.Module.Subtitles.Tests;

/// <summary>
/// El <c>paquete.json</c> del módulo lo lee la app como módulo del protocolo 1 con la función <c>subtitulos</c> y vale
/// para Rodaje: sin <c>"apps"</c>, <c>ModuleCatalog</c> lo descarta y «Subtítulos automáticos» dice que falta el módulo.
/// </summary>
public sealed class ManifestTests
{
    /// <summary>El id de la app, el mismo de <c>ModuleCatalog.AppId</c> (está en Rodaje.App, que no se referencia aquí).</summary>
    private const string AppId = "rodaje";

    private static string ManifestPath()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "modules", "Rodaje.Module.Subtitles", "paquete.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("No encuentro modules/Rodaje.Module.Subtitles/paquete.json.");
    }

    [Fact]
    public void The_manifest_is_a_protocol_1_module_with_the_subtitles_function()
    {
        ManifestResult result = ManifestReader.ReadFile(ManifestPath(), checkFiles: false);
        Assert.Empty(result.Errors);
        Assert.Equal(PackageKind.Module, result.Manifest!.Kind);
        Assert.Equal("modulo-subtitulos", result.Manifest.Id);
        Assert.Equal(1, result.Manifest.Module!.Protocol);
        Assert.Equal(["subtitulos"], result.Manifest.Module.Functions);
    }

    [Fact]
    public void The_manifest_works_in_rodaje_so_the_module_catalog_keeps_it()
    {
        ManifestResult result = ManifestReader.ReadFile(ManifestPath(), checkFiles: false);
        Assert.NotNull(result.Manifest);
        Assert.True(result.Manifest.WorksIn(AppId), "paquete.json debe nombrar «rodaje» en «apps».");
    }
}
