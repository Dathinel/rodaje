using System.Text;

namespace Rodaje.Packages.Tests;

/// <summary>
/// <see cref="PackageIntegrity"/>: el manifiesto <c>integridad.json</c> que <see cref="PackageInstaller"/> escribe al
/// instalar y que se vuelve a comprobar antes de cargar un módulo o un codificador GPL.
/// </summary>
public sealed class PackageIntegrityTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void EscribirYVerificar_PasaConLosArchivosIntactos()
    {
        File.WriteAllText(_folder.Combine("a.dll"), "uno");
        Directory.CreateDirectory(_folder.Combine("sub"));
        File.WriteAllText(_folder.Combine("sub", "b.json"), "{}");

        PackageIntegrity.Write(_folder.Path);
        PackageIntegrity.Verify(_folder.Path);
    }

    [Fact]
    public void Verificar_RechazaUnArchivoManipulado()
    {
        File.WriteAllText(_folder.Combine("a.dll"), "uno");
        PackageIntegrity.Write(_folder.Path);

        File.WriteAllText(_folder.Combine("a.dll"), "troyanizado");

        Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
    }

    [Fact]
    public void Verificar_RechazaUnArchivoQueFalta()
    {
        File.WriteAllText(_folder.Combine("a.dll"), "uno");
        File.WriteAllText(_folder.Combine("b.dll"), "dos");
        PackageIntegrity.Write(_folder.Path);

        File.Delete(_folder.Combine("b.dll"));

        Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
    }

    [Fact]
    public void Verificar_RechazaUnArchivoSobrante()
    {
        File.WriteAllText(_folder.Combine("a.dll"), "uno");
        PackageIntegrity.Write(_folder.Path);

        File.WriteAllText(_folder.Combine("colada.dll"), "extra");

        Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
    }

    [Fact]
    public void Verificar_RechazaUnManifiestoConEntradaInvalida()
    {
        File.WriteAllText(_folder.Combine("a.dll"), "uno");
        // «../../fuera» no es una relativa sana y «xyz» no es un SHA-256.
        File.WriteAllText(_folder.Combine(PackageIntegrity.FileName), """{"../../fuera": "xyz"}""");

        Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
    }

    [Fact]
    public void Verificar_SinManifiesto_Falla()
    {
        File.WriteAllText(_folder.Combine("a.dll"), "uno");

        Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
    }

    [Fact]
    public void Asegurar_SinManifiesto_TomaLaBaseActual_YLuegoDetectaElCambio()
    {
        File.WriteAllText(_folder.Combine("a.dll"), "uno");

        PackageIntegrity.Ensure(_folder.Path);
        Assert.True(File.Exists(_folder.Combine(PackageIntegrity.FileName)));

        // Segunda llamada sin cambios: verifica contra la base y pasa.
        PackageIntegrity.Ensure(_folder.Path);

        File.WriteAllText(_folder.Combine("a.dll"), "troyanizado");
        Assert.Throws<PackageInstallException>(() => PackageIntegrity.Ensure(_folder.Path));
    }

    [Fact]
    public void ElPropioManifiesto_NoQuedaCubicoPorElManifiesto()
    {
        File.WriteAllText(_folder.Combine("a.dll"), "uno");
        PackageIntegrity.Write(_folder.Path);

        // integridad.json no está dentro del manifiesto ni se exige que lo esté: si lo contara
        // no se podría verificar (cualquier escritura futura cambiaría su hash).
        string json = File.ReadAllText(_folder.Combine(PackageIntegrity.FileName), Encoding.UTF8);
        Assert.DoesNotContain(PackageIntegrity.FileName, json);
        PackageIntegrity.Verify(_folder.Path);
    }
}
