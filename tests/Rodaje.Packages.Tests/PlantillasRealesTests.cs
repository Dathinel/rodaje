using Rodaje.Packages.Content;

namespace Rodaje.Packages.Tests;

/// <summary>Solo con RODAJE_PRUEBAS_LARGAS=1 y el paquete armado de <c>rodaje-plantillas</c> (D-064).</summary>
public sealed class PlantillasRealesFactAttribute : FactAttribute
{
    public PlantillasRealesFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RODAJE_PRUEBAS_LARGAS") != "1")
        {
            Skip = "Omitida: prueba larga (RODAJE_PRUEBAS_LARGAS=1).";
        }
        else if (PlantillasRealesTests.FindPackage() is null)
        {
            Skip = "Omitida: falta rodaje-plantillas-*.paquete (RODAJE_PLANTILLAS_PAQUETE o Complementos\\rodaje-plantillas\\dist).";
        }
    }
}

/// <summary>El paquete real de plantillas se instala con el instalador de verdad y cada plantilla instancia con sus valores por defecto.</summary>
public sealed class PlantillasRealesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rodaje-plantillas-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>La variable <c>RODAJE_PLANTILLAS_PAQUETE</c> o el último <c>Complementos\rodaje-plantillas\dist\*.paquete</c> subiendo desde las pruebas.</summary>
    internal static string? FindPackage()
    {
        if (Environment.GetEnvironmentVariable("RODAJE_PLANTILLAS_PAQUETE") is { Length: > 0 } explicitPath)
        {
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string dist = Path.Combine(dir.FullName, "Complementos", "rodaje-plantillas", "dist");
            if (Directory.Exists(dist))
            {
                return Directory.GetFiles(dist, "rodaje-plantillas-*.paquete").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
            }
        }

        return null;
    }

    [PlantillasRealesFact]
    public void Se_instala_y_todas_las_plantillas_instancian_con_sus_valores_por_defecto()
    {
        var locations = PackageLocations.Under(Path.Combine(_folder, "complementos"));
        InstallResult result = new PackageInstaller(locations, "rodaje").Install(FindPackage()!);

        Assert.Empty(result.Warnings);
        IReadOnlyList<PackageItem> templates = [.. result.Package.Manifest.Items.Where(i => i.Kind == ItemKind.MotionTemplate)];
        Assert.NotEmpty(templates);
        Assert.Equal(result.Package.Manifest.Items.Count, templates.Count);
        foreach (PackageItem item in templates)
        {
            using FileStream file = File.OpenRead(Path.Combine(result.Package.Directory, item.File!));
            TemplateDocument document = ContentReaders.ReadTemplate(file);
            TemplateInstance instance = TemplateInstantiator.Instantiate(document);

            Assert.Equal(item.Id, document.Id);
            Assert.NotEmpty(document.Parameters);
            Assert.True(instance.Layers.Count > 0 || instance.Transition is not null || instance.Effect is not null, item.Id);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
