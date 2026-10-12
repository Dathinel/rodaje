using System.Diagnostics;
using System.Text;
using Rodaje.Packages.Download;
using Rodaje.Packages.Tests.Download;

namespace Rodaje.Packages.Tests.Hardened;

/// <summary>
/// Versión endurecida de <c>FirmarIndiceScriptTests.Signatures_made_by_the_script_verify_in_the_program</c>: allí,
/// fuera de Windows, la prueba hacía <c>return</c> y salía en verde sin haber corrido el script. Aquí se informa como
/// omitida, con la causa, y además se comprueba que una firma del script no sirve para un índice modificado.
/// </summary>
public sealed class HardenedFirmarIndiceScriptTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Script()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "build", "firmar-indice.ps1")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return Path.Combine(directory ?? throw new InvalidOperationException("No encuentro build\\firmar-indice.ps1."), "build", "firmar-indice.ps1");
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

    [WindowsOnlyFact("build\\firmar-indice.ps1 se corre con Windows PowerShell 5.1 (powershell.exe), que no existe en Linux.")]
    public void Signatures_made_by_the_script_verify_in_the_program_and_only_for_that_index()
    {
        string key = _temp.Combine("claves", "privada.pem");
        string publicFile = _temp.Combine("publica.txt");
        (int code, string output) = Run("-NuevaClave", "-Clave", key, "-Publica", publicFile);
        Assert.True(code == 0, output);
        Assert.Contains("BEGIN PRIVATE KEY", File.ReadAllText(key), StringComparison.Ordinal);
        Assert.Equal(1, Run("-NuevaClave", "-Clave", key, "-Publica", publicFile).Code);

        using var publicKey = IndexSignature.TryImportPublicKey(File.ReadAllText(publicFile));
        Assert.NotNull(publicKey);

        for (int i = 0; i < 6; i++)
        {
            string index = _temp.Combine($"indice {i} ñ.json");
            File.WriteAllBytes(index, SignedIndexTests.Index(SignedIndexTests.Entry(version: $"1.0.{i}")));
            (code, output) = Run("-Indice", index, "-Clave", key, "-Publica", publicFile);
            Assert.True(code == 0, output);
            byte[] json = File.ReadAllBytes(index);
            byte[] signature = File.ReadAllBytes(index + ".sig");
            SignedIndex loaded = SignedIndex.Load(json, signature, publicKey, DownloadUrlPolicy.Default);
            Assert.Equal($"1.0.{i}", Assert.Single(loaded.Entries).Version.ToString());

            // La misma firma no vale para un índice con un byte cambiado.
            byte[] tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(json).Replace($"1.0.{i}", $"9.0.{i}", StringComparison.Ordinal));
            Assert.Throws<DownloadVerificationException>(() => SignedIndex.Load(tampered, signature, publicKey, DownloadUrlPolicy.Default));
        }

        File.WriteAllText(publicFile, Convert.ToBase64String(Encoding.ASCII.GetBytes("otra")));
        Assert.Equal(2, Run("-Indice", _temp.Combine("indice 0 ñ.json"), "-Clave", key, "-Publica", publicFile).Code);
    }
}
