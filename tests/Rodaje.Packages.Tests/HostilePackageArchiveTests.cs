namespace Rodaje.Packages.Tests;

/// <summary>
/// Familia 3: lectura e instalación de archivos .paquete hostiles: zips vacíos o truncados, rutas de escape, absolutas o
/// con unidad, nombres reservados de Windows, entradas repetidas, tipos cambiados en el manifiesto, ids y versiones mal
/// escritos. Cada rechazo dice por qué y no deja nada en la carpeta de complementos.
/// </summary>
public sealed class HostilePackageArchiveTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly PackageLocations _locations;
    private readonly PackageInstaller _installer;

    public HostilePackageArchiveTests()
    {
        _locations = PackageLocations.Under(_folder.Combine("complementos"));
        _installer = new PackageInstaller(_locations, "rodaje");
    }

    public void Dispose() => _folder.Dispose();

    private static string Manifest(string id = "paquete-ok", string version = "1.0.0") =>
        Samples.Manifest(id, version, items: """[{ "id": "item1", "kind": "transition", "name": "Item 1", "file": "transiciones/uno.json" }]""");

    private string Zip(params (string Name, string Content)[] entries) =>
        Samples.Zip(_folder.Combine($"hostil-{Guid.NewGuid():N}.paquete"), entries);

    /// <summary>Instalar falla con ese mensaje y no queda nada instalado, a medias ni fuera de la carpeta de complementos.</summary>
    private void AssertRefused(string package, string expected)
    {
        var ex = Assert.Throws<PackageInstallException>(() => _installer.Install(package));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Empty(new[] { _locations.Packages, _locations.Modules }.Where(Directory.Exists).SelectMany(Directory.EnumerateFileSystemEntries));
        Assert.All(Directory.EnumerateFiles(_folder.Path, "*", SearchOption.AllDirectories), file =>
            Assert.True(file.EndsWith(".paquete", StringComparison.Ordinal) || Path.GetFileName(file) == PackageInstaller.OperationLockFileName, file));
    }

    [Fact]
    public void PaqueteHostil_ZipVacio_Rechazado()
    {
        string empty = _folder.Combine("vacio.paquete");
        File.WriteAllBytes(empty, []);

        AssertRefused(empty, "No se pudo instalar: ");
    }

    [Fact]
    public void PaqueteHostil_ZipTruncado_Rechazado()
    {
        byte[] bytes = File.ReadAllBytes(Zip(("paquete.json", Manifest()), ("transiciones/uno.json", "{}")));
        string truncated = _folder.Combine("truncado.paquete");
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);

        AssertRefused(truncated, "No se pudo instalar: ");
    }

    [Theory]
    [InlineData("../fuera.txt")]
    [InlineData("..\\fuera.txt")]
    [InlineData("transiciones/../../fuera.txt")]
    [InlineData("a/b/../../../escape.txt")]
    [InlineData("/absoluta.txt")]
    [InlineData("\\absoluta.txt")]
    [InlineData("/transiciones/uno.json")]
    [InlineData("C:/archivo.txt")]
    [InlineData("D:\\archivo.txt")]
    [InlineData("Z:/payload.exe")]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT1")]
    [InlineData("con.txt")]
    [InlineData("nul.json")]
    [InlineData("sub/com2.dat")]
    [InlineData("transiciones/archivo<1>.json")]
    [InlineData("transiciones/archivo>2>.json")]
    [InlineData("transiciones/archivo:3.json")]
    [InlineData("transiciones/archivo\"4.json")]
    [InlineData("transiciones/archivo|5.json")]
    [InlineData("transiciones/archivo?6.json")]
    [InlineData("transiciones/archivo*7.json")]
    [InlineData("transiciones/ con_espacio_al_inicio.json")]
    [InlineData("transiciones/con_espacio_al_final.json ")]
    public void PaqueteHostil_EntradaConRutaNoPermitida_Rechazado(string entry) =>
        AssertRefused(Zip(("paquete.json", Manifest()), ("transiciones/uno.json", "{}"), (entry, "malicioso")), "ruta no permitida");

    [Fact]
    public void PaqueteHostil_DosEntradasConElMismoNombreExacto_Rechazado() =>
        AssertRefused(Zip(("paquete.json", Manifest()), ("transiciones/uno.json", "{}"), ("transiciones/uno.json", "{\"otro\": 2}")),
            "repite el archivo «transiciones/uno.json»");

    [Theory]
    [InlineData("""{ "formatVersion": "uno", "id": "p", "version": "1.0.0" }""", "«formatVersion» debe ser un número entero")]
    [InlineData("""{ "formatVersion": 1, "id": 12345, "version": "1.0.0" }""", "«id» debe ser un texto no vacío")]
    [InlineData("""{ "formatVersion": 1, "id": "p", "version": true }""", "«version» debe ser un texto no vacío")]
    [InlineData("""{ "formatVersion": 1, "id": "p", "version": "1.0.0", "items": "no-es-lista" }""", "items debe ser una lista")]
    [InlineData("""{ "formatVersion": 1, "id": "p", "version": "1.0.0", "apps": {} }""", "«apps» debe ser una lista de textos")]
    public void PaqueteHostil_ManifiestoConTiposCambiados_Rechazado(string manifest, string expected) =>
        AssertRefused(Zip(("paquete.json", manifest), ("transiciones/uno.json", "{}")), expected);

    [Theory]
    [InlineData("", "«id» debe ser un texto no vacío")]
    [InlineData("-inicio-guion", "El id «-inicio-guion» no es válido")]
    [InlineData("fin-guion-", "El id «fin-guion-» no es válido")]
    [InlineData("doble--guion", "El id «doble--guion» no es válido")]
    [InlineData("punto..punto", "El id «punto..punto» no es válido")]
    [InlineData("MAYUSCULAS", "El id «MAYUSCULAS» no es válido")]
    [InlineData("con espacios", "El id «con espacios» no es válido")]
    [InlineData("con/barras", "El id «con/barras» no es válido")]
    [InlineData("ñandú", "El id «ñandú» no es válido")]
    [InlineData("con", "El id «con» no es válido")]
    [InlineData("nul.x", "El id «nul.x» no es válido")]
    [InlineData("com1", "El id «com1» no es válido")]
    [InlineData("lpt9.a", "El id «lpt9.a» no es válido")]
    [InlineData("propios", "El id «propios» está reservado")]
    public void PaqueteHostil_IdNoPermitido_Rechazado(string id, string expected) =>
        AssertRefused(Zip(("paquete.json", Manifest(id: id)), ("transiciones/uno.json", "{}")), expected);

    [Theory]
    [InlineData("", "«version» debe ser un texto no vacío")]
    [InlineData("v1.0.0", "La versión «v1.0.0» no es mayor.menor.parche")]
    [InlineData("1", "La versión «1» no es mayor.menor.parche")]
    [InlineData("1.0", "La versión «1.0» no es mayor.menor.parche")]
    [InlineData("1.0.0.0", "La versión «1.0.0.0» no es mayor.menor.parche")]
    [InlineData("1.0.0-rc.01", "La versión «1.0.0-rc.01» no es mayor.menor.parche")]
    [InlineData("1.0.0-rc@1", "La versión «1.0.0-rc@1» no es mayor.menor.parche")]
    [InlineData("1.0.beta", "La versión «1.0.beta» no es mayor.menor.parche")]
    [InlineData("-1.0.0", "La versión «-1.0.0» no es mayor.menor.parche")]
    public void PaqueteHostil_VersionQueNoEsUnaVersion_Rechazado(string version, string expected) =>
        AssertRefused(Zip(("paquete.json", Manifest(version: version)), ("transiciones/uno.json", "{}")), expected);
}
