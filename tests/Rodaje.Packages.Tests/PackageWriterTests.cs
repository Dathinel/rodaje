using System.Text;
using Rodaje.Packages.Content;

namespace Rodaje.Packages.Tests;

public sealed class PackageWriterTests
{
    private static readonly SingleItemPackage Package = new("mis-plantillas.barra", PackageVersion.Parse("1.0.0"), "Rótulo «Barra»", "Ana Ñandú", "CC0-1.0",
        "rodaje", "barra", ItemKind.MotionTemplate, "Rótulo inferior Barra", "plantillas/barra.json");

    [Fact]
    public void Las_mismas_fuentes_dan_los_mismos_bytes()
    {
        byte[] content = Encoding.UTF8.GetBytes(TemplateReaderTests.Example);
        byte[] first = PackageWriter.Write(Package, content);
        Thread.Sleep(2100);
        Assert.Equal(first, PackageWriter.Write(Package, content));
    }

    [Fact]
    public void El_instalador_real_lo_acepta_y_la_plantilla_se_lee()
    {
        using var temp = new TempFolder();
        string archive = temp.Combine("barra.paquete");
        File.WriteAllBytes(archive, PackageWriter.Write(Package, Encoding.UTF8.GetBytes(TemplateReaderTests.Example)));

        InstallResult result = new PackageInstaller(PackageLocations.Under(temp.Combine("complementos")), "rodaje").Install(archive);

        Assert.Empty(result.Warnings);
        Assert.Equal("Rótulo «Barra»", result.Package.Manifest.Name);
        PackageItem item = Assert.Single(result.Package.Manifest.Items);
        Assert.Equal((ItemKind.MotionTemplate, "barra", "Rótulo inferior Barra"), (item.Kind, item.Id, item.Name));
        using FileStream file = File.OpenRead(Path.Combine(result.Package.Directory, item.File!));
        Assert.Equal(2, TemplateInstantiator.Instantiate(ContentReaders.ReadTemplate(file)).Layers.Count);
    }

    [Theory]
    [InlineData("Mayúsculas", "barra", "plantillas/barra.json")]
    [InlineData("paquete", "con espacio", "plantillas/barra.json")]
    [InlineData("paquete", "barra", "../fuera.json")]
    [InlineData("paquete", "barra", "paquete.json")]
    public void Rechaza_ids_y_rutas_que_no_valen(string id, string itemId, string file)
    {
        Assert.Throws<ArgumentException>(() => PackageWriter.Write(Package with { Id = id, ItemId = itemId, File = file }, []));
    }
}
