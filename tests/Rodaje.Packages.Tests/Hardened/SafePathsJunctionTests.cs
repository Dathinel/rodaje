using System.Diagnostics;

namespace Rodaje.Packages.Tests.Hardened;

/// <summary>
/// <see cref="SafePaths.IsInside"/> frente a uniones (<c>mklink /J</c>): un enlace dentro del paquete es un escape
/// y se rechaza, pero que la propia carpeta de instalación cuelgue de una unión es elección del usuario.
/// </summary>
public sealed class SafePathsJunctionTests : IDisposable
{
    private readonly TempFolder _outside = new();
    private readonly TempFolder _inside = new();

    public void Dispose()
    {
        _outside.Dispose();
        _inside.Dispose();
    }

    [WindowsOnlyFact("las uniones de directorio son una funcionalidad de NTFS")]
    public void UnaUnionDentroDelPaquete_SeRechaza()
    {
        string link = Path.Combine(_inside.Path, "enlace");
        Junction(link, _outside.Path);

        string file = Path.Combine(link, "bajo.json");

        Assert.False(SafePaths.IsInside(file, _inside.Path));
    }

    [WindowsOnlyFact("las uniones de directorio son una funcionalidad de NTFS")]
    public void LaCarpetaDeInstalacionColgandoDeUnaUnion_SigueDentro()
    {
        // La raíz real no se comprueba: si el usuario decide instalar bajo una unión (típico al reorganizar
        // un disco con junctions), sus paquetes deben seguir cargando.
        string real = Path.Combine(_outside.Path, "real");
        Directory.CreateDirectory(real);
        string link = Path.Combine(_inside.Path, "montado");
        Junction(link, real);
        string file = Path.Combine(real, "a.json");
        File.WriteAllText(file, "{}");

        Assert.True(SafePaths.IsInside(Path.Combine(link, "a.json"), link));
    }

    [WindowsOnlyFact("las uniones de directorio son una funcionalidad de NTFS")]
    public void UnaUnionEnMedioDelArbol_SeRechaza()
    {
        string sub = Path.Combine(_inside.Path, "sub");
        Directory.CreateDirectory(sub);
        string link = Path.Combine(sub, "enlace");
        Junction(link, _outside.Path);

        Assert.False(SafePaths.IsInside(Path.Combine(link, "bajo.json"), _inside.Path));
    }

    /// <summary>Crea una unión de directorio sin privilegios de administrador (a diferencia de los symlinks).</summary>
    private static void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J falló ({process.ExitCode})");
        Assert.True(Directory.Exists(link));
    }
}
