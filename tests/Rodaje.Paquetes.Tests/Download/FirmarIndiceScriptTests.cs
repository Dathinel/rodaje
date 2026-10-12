using System.Diagnostics;
using System.Text;
using Rodaje.Paquetes.Download;

namespace Rodaje.Paquetes.Tests.Download;

/// <summary>herramientas\firmar-indice.ps1 (Windows PowerShell 5.1) firma lo que el programa acepta.</summary>
public sealed class FirmarIndiceScriptTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Script()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "herramientas", "firmar-indice.ps1")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return Path.Combine(directory ?? throw new InvalidOperationException("No encuentro herramientas\\firmar-indice.ps1."), "herramientas", "firmar-indice.ps1");
    }

    private static (int Code, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in (string[])["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Script(), .. arguments])
        {
            start.ArgumentList.Add(a);
        }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    [Fact]
    public void Signatures_made_by_the_script_verify_in_the_program()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string key = _temp.Combine("claves", "privada.pem");
        string publicFile = _temp.Combine("publica.txt");
        (int code, string output) = Run("-NuevaClave", "-Clave", key, "-Publica", publicFile);
        Assert.True(code == 0, output);
        Assert.Contains("BEGIN PRIVATE KEY", File.ReadAllText(key), StringComparison.Ordinal);
        Assert.Equal(1, Run("-NuevaClave", "-Clave", key, "-Publica", publicFile).Code);

        using var publicKey = IndexSignature.TryImportPublicKey(File.ReadAllText(publicFile));
        Assert.NotNull(publicKey);

        // Varias firmas: r y s con el bit alto encendido o con ceros al principio necesitan bien el DER.
        for (int i = 0; i < 6; i++)
        {
            string index = _temp.Combine($"indice {i} ñ.json");
            File.WriteAllBytes(index, SignedIndexTests.Index(SignedIndexTests.Entry(version: $"1.0.{i}")));
            (code, output) = Run("-Indice", index, "-Clave", key, "-Publica", publicFile);
            Assert.True(code == 0, output);
            SignedIndex loaded = SignedIndex.Load(File.ReadAllBytes(index), File.ReadAllBytes(index + ".sig"), publicKey, DownloadUrlPolicy.Default);
            Assert.Single(loaded.Entries);
        }

        File.WriteAllText(publicFile, Convert.ToBase64String(Encoding.ASCII.GetBytes("otra")));
        Assert.Equal(2, Run("-Indice", _temp.Combine("indice 0 ñ.json"), "-Clave", key, "-Publica", publicFile).Code);
    }
}
