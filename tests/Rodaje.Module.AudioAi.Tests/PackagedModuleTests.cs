using Rodaje.Modules.Protocol;
using Rodaje.Packages;
using Xunit.Abstractions;

namespace Rodaje.Module.AudioAi.Tests;

/// <summary>
/// El .paquete que arma build/empaquetar-modulo.ps1 para este sistema (D-066, fase 7): se instala como lo hace el editor,
/// declara su plataforma con el formato 2 (que un editor viejo rechaza) y el módulo instalado arranca y saluda por el
/// protocolo. <c>RODAJE_PAQUETE_MODULO</c> apunta al paquete; sin ella se omite (la CI de Linux y macOS la pone).
/// </summary>
public sealed class PackagedModuleTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rodaje-paquete-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [PackageFact]
    public async Task The_package_for_this_system_installs_declares_its_platform_and_greets()
    {
        string archive = Environment.GetEnvironmentVariable(PackageFactAttribute.Variable)!;
        var installer = new PackageInstaller(PackageLocations.Under(_root), "rodaje");

        InstallResult result = installer.Install(archive);

        PackageManifest manifest = result.Package.Manifest;
        Assert.Equal(PackageKind.Module, manifest.Kind);
        Assert.Equal(PackageManifest.PlatformFormatVersion, manifest.FormatVersion);
        Assert.Equal(PackageManifest.ThisPlatform, manifest.Platform);
        string entry = Path.Combine(result.Package.Directory, manifest.Module!.Entry);
        Assert.False(entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        Assert.True((File.GetUnixFileMode(entry) & UnixFileMode.UserExecute) != 0, "El ejecutable instalado no tiene el bit de ejecución.");

        await using ModuleProcess module = await ModuleProcess.StartAsync(entry);
        TimeSpan ping = await module.Client.PingAsync();
        output.WriteLine($"{manifest.Id} {manifest.Version} para {manifest.Platform}: {new FileInfo(archive).Length / 1048576.0:0.0} MB; " +
            $"funciones {string.Join(", ", module.Client.Module.Functions.Select(f => f.Id))}; ping {ping.TotalMilliseconds:0.0} ms.");
        Assert.NotEmpty(module.Client.Module.Functions);
    }
}

/// <summary>Solo con <c>RODAJE_PAQUETE_MODULO</c> apuntando a un paquete de Linux o macOS.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PackageFactAttribute : FactAttribute
{
    public const string Variable = "RODAJE_PAQUETE_MODULO";

    public PackageFactAttribute()
    {
        if (OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } path || !File.Exists(path))
        {
            Skip = $"Sin {Variable} (lo pone la CI de Linux y macOS tras build/empaquetar-modulo.ps1).";
        }
    }
}
