using System.IO.Compression;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Support;
using Rodaje.Packages;

namespace Rodaje.App.Tests.Packages;

/// <summary>
/// Un <c>paquete.json</c> dañado en la carpeta de un módulo con id viejo (<see cref="PackageService.RenamedModules"/>),
/// tarea 262. Antes, la migración de ids lanzaba <c>JsonException</c> sin atrapar: fallaban leer, instalar y desinstalar
/// complementos, y en cada intento, porque la migración no quedaba hecha.
/// </summary>
public sealed class RenamedModuleDamagedManifestTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public RenamedModuleDamagedManifestTests() => Locations = PackageLocations.Under(Path.Combine(_folder.Path, "complementos"));

    private PackageLocations Locations { get; }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void La_migracion_salta_el_manifiesto_danado_y_sigue_con_los_demas()
    {
        string damaged = WriteDamagedOldModule("rodaje-audio-ia");
        WriteOldVision();

        Assert.Equal(1, PackageService.MigrateRenamedModules(Locations));

        // El dañado se queda donde estaba, con lo que tenía; el otro sí se migró.
        Assert.True(File.Exists(Path.Combine(damaged, PackageManifest.FileName)));
        Assert.False(Directory.Exists(Path.Combine(Locations.Modules, "modulo-audio-ia")));
        Assert.True(Directory.Exists(Path.Combine(Locations.Modules, "modulo-vision")));
        Assert.False(Directory.Exists(Path.Combine(Locations.Modules, "rodaje-vision")));
    }

    [Fact]
    public void Un_manifiesto_danado_de_id_viejo_no_bloquea_leer_instalar_ni_desinstalar() => UiThread.Run(async () =>
    {
        string damaged = WriteDamagedOldModule("rodaje-vision");
        var packages = new PackageService(Locations, PackageService.LoadBundledCatalog());

        PackageScan scan = await packages.RefreshAsync();
        Assert.True(packages.HasScanned);
        Assert.Contains(scan.Problems, p => string.Equals(Path.GetFullPath(p.Directory), Path.GetFullPath(damaged), StringComparison.Ordinal));

        // Ni se repite en cada intento ni bloquea lo demás.
        await packages.RefreshAsync();
        await packages.InstallFromFileAsync(WriteArchive("otro-paquete"));
        Assert.NotNull(packages.Installed.Find("otro-paquete"));
        await packages.UninstallAsync("otro-paquete");
        Assert.Null(packages.Installed.Find("otro-paquete"));
        Assert.True(File.Exists(Path.Combine(damaged, PackageManifest.FileName)));
    });

    private string WriteDamagedOldModule(string oldId)
    {
        string folder = Path.Combine(Locations.Modules, oldId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, PackageManifest.FileName), """{ "formatVersion": 1, "id": "rodaje-""");
        return folder;
    }

    private void WriteOldVision()
    {
        string folder = Path.Combine(Locations.Modules, "rodaje-vision");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Rodaje.Module.Vision.exe"), "");
        File.WriteAllText(Path.Combine(folder, PackageManifest.FileName), """
            { "formatVersion": 1, "id": "rodaje-vision", "version": "0.1.0", "kind": "module", "name": "Visión", "author": "Dathinel",
              "license": "GPL-3.0-only", "apps": ["rodaje"],
              "module": { "entry": "Rodaje.Module.Vision.exe", "protocol": 1, "functions": ["quitar-fondo"] } }
            """);
    }

    private string WriteArchive(string id)
    {
        string path = Path.Combine(_folder.Path, id + ".paquete");
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(PackageManifest.FileName).Open());
        writer.Write($$"""
            {
              "formatVersion": 1, "id": "{{id}}", "version": "1.0.0", "name": "{{id}}", "author": "Dathinel", "license": "MIT",
              "apps": ["rodaje"], "items": [{ "id": "elemento", "kind": "transition", "name": "Elemento" }]
            }
            """);
        return path;
    }
}
