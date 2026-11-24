using System.Text;

namespace Rodaje.Packages.Tests;

/// <summary>
/// Familia 2: Pruebas hostiles contra PackageIntegrity:
/// Archivos sobrantes, faltantes, alterados en un solo byte, integridad.json ausente,
/// vacío, truncado, rutas con «..», absolutas, con barras invertidas, mayúsculas distintas,
/// duplicadas, paquete.json modificado tras firmar, y modelos en carpetas no declaradas.
/// </summary>
public sealed class PackageIntegrityHostileTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly string _sha64 = new('0', 64);

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Integridad_ArchivoSobranteEnRaiz_Rechazado()
    {
        File.WriteAllText(_folder.Combine("a.txt"), "contenido a");
        PackageIntegrity.Write(_folder.Path);

        File.WriteAllText(_folder.Combine("sobrante.txt"), "no registrado");

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("sobrante.txt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("falta del manifiesto", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integridad_ArchivoSobranteEnSubcarpeta_Rechazado()
    {
        Directory.CreateDirectory(_folder.Combine("sub"));
        File.WriteAllText(_folder.Combine("sub", "base.txt"), "base");
        PackageIntegrity.Write(_folder.Path);

        File.WriteAllText(_folder.Combine("sub", "extra.dll"), "dll intrusa");

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("sub/extra.dll", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integridad_ArchivoFaltaEnRaiz_Rechazado()
    {
        File.WriteAllText(_folder.Combine("uno.txt"), "1");
        File.WriteAllText(_folder.Combine("dos.txt"), "2");
        PackageIntegrity.Write(_folder.Path);

        File.Delete(_folder.Combine("dos.txt"));

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("dos.txt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("falta o su SHA-256 no coincide", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integridad_ArchivoFaltaEnSubcarpeta_Rechazado()
    {
        Directory.CreateDirectory(_folder.Combine("sub", "profunda"));
        File.WriteAllText(_folder.Combine("sub", "profunda", "config.json"), "{}");
        PackageIntegrity.Write(_folder.Path);

        File.Delete(_folder.Combine("sub", "profunda", "config.json"));

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("sub/profunda/config.json", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]   // Primer byte
    [InlineData(50)]  // Byte intermedio
    [InlineData(99)]  // Último byte
    public void Integridad_ArchivoCambiadoEnUnByte_Rechazado(int byteIndex)
    {
        byte[] original = new byte[100];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = (byte)(i + 1);
        }

        File.WriteAllBytes(_folder.Combine("datos.bin"), original);
        PackageIntegrity.Write(_folder.Path);

        byte[] modified = (byte[])original.Clone();
        modified[byteIndex] ^= 0xFF; // alterar un byte exacto
        File.WriteAllBytes(_folder.Combine("datos.bin"), modified);

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("datos.bin", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SHA-256 no coincide", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integridad_ManifiestoAusente_Rechazado()
    {
        File.WriteAllText(_folder.Combine("archivo.txt"), "contenido");

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("integridad.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integridad_ManifiestoVacio_Rechazado()
    {
        File.WriteAllText(_folder.Combine("archivo.txt"), "contenido");
        File.WriteAllBytes(_folder.Combine(PackageIntegrity.FileName), []);

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("integridad.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integridad_ManifiestoTruncado_Rechazado()
    {
        File.WriteAllText(_folder.Combine("archivo.txt"), "contenido");
        File.WriteAllText(_folder.Combine(PackageIntegrity.FileName), """{ "archivo.txt": "0123456789""");

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("integridad.json", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("..\\escape.dll")]
    [InlineData("sub/../../escape.dll")]
    public void Integridad_RutaConPuntosPuntos_Rechazado(string path)
    {
        File.WriteAllText(_folder.Combine("archivo.txt"), "ok");
        File.WriteAllText(_folder.Combine(PackageIntegrity.FileName), $$"""{ "{{path.Replace("\\", "\\\\")}}": "{{_sha64}}" }""");

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("entrada no válida", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:/inseguro.dll")]
    [InlineData("/inseguro.dll")]
    [InlineData("\\\\servidor\\compartido\\inseguro.dll")]
    public void Integridad_RutaAbsoluta_Rechazado(string path)
    {
        File.WriteAllText(_folder.Combine("archivo.txt"), "ok");
        File.WriteAllText(_folder.Combine(PackageIntegrity.FileName), $$"""{ "{{path.Replace("\\", "\\\\")}}": "{{_sha64}}" }""");

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("entrada no válida", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integridad_RutaConBarrasInvertidas_SeNormalizaYVerifica()
    {
        Directory.CreateDirectory(_folder.Combine("sub"));
        File.WriteAllText(_folder.Combine("sub", "a.txt"), "contenido");
        string sha = PackageInstaller.ComputeSha256(_folder.Combine("sub", "a.txt"));

        // Se escribe con barra invertida explícita en el JSON
        File.WriteAllText(_folder.Combine(PackageIntegrity.FileName), $$"""{ "sub\\a.txt": "{{sha}}" }""");

        // Debe normalizarse internamente a 'sub/a.txt' y verificar correctamente
        PackageIntegrity.Verify(_folder.Path);
    }

    [Fact]
    public void Integridad_RutasDuplicadasConDistintasBarras_Rechazado()
    {
        Directory.CreateDirectory(_folder.Combine("sub"));
        File.WriteAllText(_folder.Combine("sub", "a.txt"), "contenido");
        string sha = PackageInstaller.ComputeSha256(_folder.Combine("sub", "a.txt"));

        // Ambas normalizan a "sub/a.txt" -> duplicado en el diccionario
        File.WriteAllText(_folder.Combine(PackageIntegrity.FileName), $$"""{ "sub/a.txt": "{{sha}}", "sub\\a.txt": "{{sha}}" }""");

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("entrada no válida", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integridad_RutasDuplicadasConDistintaMayuscula_Rechazado()
    {
        File.WriteAllText(_folder.Combine("a.txt"), "uno");
        string sha = PackageInstaller.ComputeSha256(_folder.Combine("a.txt"));

        File.WriteAllText(_folder.Combine(PackageIntegrity.FileName), $$"""{ "a.txt": "{{sha}}", "A.TXT": "{{sha}}" }""");

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("entrada no válida", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integridad_PaqueteJsonCambiadoDespuesDeEscribirIntegridad_Rechazado()
    {
        File.WriteAllText(_folder.Combine("paquete.json"), """{ "id": "p", "name": "Original" }""");
        File.WriteAllText(_folder.Combine("script.py"), "print(1)");
        PackageIntegrity.Write(_folder.Path);

        // Se altera paquete.json después de que se escribió la integridad
        File.WriteAllText(_folder.Combine("paquete.json"), """{ "id": "p", "name": "Manipulado" }""");

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("paquete.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SHA-256 no coincide", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integridad_ArchivoSobranteMismoNombreQueModeloPeroEnOtraCarpeta_Rechazado()
    {
        // Se declara un modelo en "modelos/red.onnx"
        File.WriteAllText(_folder.Combine("paquete.json"), $$"""
            {
              "id": "m",
              "module": {
                "entry": "m.exe",
                "models": [
                  {
                    "id": "red",
                    "file": "modelos/red.onnx",
                    "size": 10,
                    "sha256": "{{_sha64}}",
                    "license": "MIT",
                    "url": "https://huggingface.co/org/repo/resolve/{{new string('a', 40)}}/red.onnx"
                  }
                ]
              }
            }
            """);
        File.WriteAllText(_folder.Combine("m.exe"), "exe");
        PackageIntegrity.Write(_folder.Path);

        // Se coloca un archivo con el mismo nombre "red.onnx", pero en "otra_carpeta/red.onnx"
        Directory.CreateDirectory(_folder.Combine("otra_carpeta"));
        File.WriteAllBytes(_folder.Combine("otra_carpeta", "red.onnx"), new byte[10]);

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("otra_carpeta/red.onnx", ex.Message, StringComparison.Ordinal);
        Assert.Contains("falta del manifiesto", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("12345")] // muy corto
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefg")] // 65 caracteres
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")] // mayúsculas
    [InlineData("xyzxyzxyzxyzxyzxyzxyzxyzxyzxyzxyzxyzxyzxyzxyzxyzxyzxyzxyzxyzxyzx")] // caracteres no hex
    public void Integridad_HashInvalidoEnManifiesto_Rechazado(string badHash)
    {
        File.WriteAllText(_folder.Combine("a.txt"), "ok");
        File.WriteAllText(_folder.Combine(PackageIntegrity.FileName), $$"""{ "a.txt": "{{badHash}}" }""");

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("entrada no válida", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integridad_ManifiestoMayorQueMaxBytes_Rechazado()
    {
        // MaxBytes es 8 MB
        string bigFile = _folder.Combine(PackageIntegrity.FileName);
        using (var fs = new FileStream(bigFile, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength((8L << 20) + 1); // 8 MB + 1 byte
        }

        var ex = Assert.Throws<PackageInstallException>(() => PackageIntegrity.Verify(_folder.Path));
        Assert.Contains("supera el tamaño máximo", ex.Message, StringComparison.Ordinal);
    }
}
