using System.Text;

namespace Rodaje.Paquetes.Tests;

/// <summary>Textos que no son Unicode válido en el manifiesto, el catálogo y los elementos propios.</summary>
public sealed class TextosNoUnicodeTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Catalog_Parse_LoneSurrogateEscape_ThrowsFormatException()
    {
        const string json = """{ "formatVersion": 1, "apps": [], "packages": [{ "id": "x", "name": "a\ud800b" }] }""";
        Assert.Throws<FormatException>(() => PackageCatalog.Parse(json));
    }

    [Fact]
    public void Catalog_ParseUtf8_InvalidBytes_ThrowsFormatException()
    {
        byte[] json = [.. """{ "formatVersion": 1, "apps": [{ "id": "rodaje", "name": "a"""u8, 0xCE, .. """b" }], "packages": [] }"""u8];
        Assert.Throws<FormatException>(() => PackageCatalog.ParseUtf8(json));
    }

    [Fact]
    public void Catalog_ParseUtf8_ValidBytes_Parses()
    {
        byte[] json = Encoding.UTF8.GetBytes("""{ "formatVersion": 1, "apps": [{ "id": "rodaje", "name": "Rodaje ñ" }], "packages": [] }""");
        Assert.Equal("Rodaje ñ", PackageCatalog.ParseUtf8(json).AppName("rodaje"));
    }

    [Fact]
    public void ManifestReader_ReadFile_InvalidUtf8_IsRejectedWithSpanishMessage()
    {
        string file = _folder.Combine("paquete.json");
        string text = Samples.Manifest(items: "[]").Replace("Transiciones de prueba", "Trans@@", StringComparison.Ordinal);
        int at = text.IndexOf("@@", StringComparison.Ordinal);
        byte[] broken = [.. Encoding.UTF8.GetBytes(text[..at]), 0xCE, .. Encoding.UTF8.GetBytes(text[(at + 2)..])];
        File.WriteAllBytes(file, broken);

        ManifestResult result = ManifestReader.ReadFile(file, checkFiles: false);

        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, e => e.Contains("Unicode", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_OneBrokenOwnItemDoesNotHideTheGoodOne()
    {
        PackageLocations locations = PackageLocations.Under(_folder.Combine("datos"));
        Directory.CreateDirectory(locations.Own);
        File.WriteAllBytes(Path.Combine(locations.Own, "a-malo.json"), [.. """{ "kind": "transition", "name": "a"""u8, 0xCE, .. """b" }"""u8]);
        File.WriteAllText(Path.Combine(locations.Own, "b-malo.json"), """{ "kind": "transition", "name": "a\ud800b" }""");
        File.WriteAllText(Path.Combine(locations.Own, "c-bueno.json"), """{ "kind": "transition", "name": "Bueno" }""");

        PackageScan scan = PackageLibrary.Scan(locations, "rodaje");

        InstalledPackage own = Assert.IsType<InstalledPackage>(scan.Find("propios"));
        PackageItem item = Assert.Single(own.Manifest.Items);
        Assert.Equal("Bueno", item.Name);
    }

    [Fact]
    public void ManifestReader_Read_RawLoneSurrogateInString_IsRejectedInsteadOfThrowingArgumentException()
    {
        string json = Samples.Manifest(items: "[]").Replace("Transiciones de prueba", "a\ud800b", StringComparison.Ordinal);
        ManifestResult result = ManifestReader.Read(json);
        Assert.Null(result.Manifest);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Catalog_Parse_RawLoneSurrogateInString_ThrowsFormatException()
    {
        string json = "{ \"formatVersion\": 1, \"apps\": [{ \"id\": \"rodaje\", \"name\": \"a\ud800b\" }], \"packages\": [] }";
        Assert.Throws<FormatException>(() => PackageCatalog.Parse(json));
    }
}
