namespace Rodaje.Packages.Tests;

/// <summary>Módulos por sistema (D-066, fase 7): «platform» va con el formato 2 y un editor de otro sistema no los instala.</summary>
public sealed class PlatformManifestTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static string Module(int format, string? platform, string entry = "rodaje-modulo") =>
        Samples.Manifest(id: "modulo-prueba", kind: "module", items: "[]",
                extra: $$""", "module": { "entry": "{{entry}}", "protocol": 1, "functions": ["eco"] }"""
                    + (platform is null ? "" : $$""", "platform": "{{platform}}" """))
            .Replace("\"formatVersion\": 1", $"\"formatVersion\": {format}", StringComparison.Ordinal);

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("osx-arm64")]
    [InlineData("win-x64")]
    public void A_format_2_module_carries_its_platform(string platform)
    {
        ManifestResult result = ManifestReader.Read(Module(2, platform));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Equal(2, result.Manifest!.FormatVersion);
        Assert.Equal(platform, result.Manifest.Platform);
    }

    [Fact]
    public void A_platform_never_travels_in_format_1_so_an_old_editor_rejects_the_whole_package()
    {
        // Un editor publicado (formato 1) rechaza todo lo que pase de su formato: si «platform» pudiera ir en el 1, lo
        // ignoraría e instalaría un ejecutable de otro sistema.
        ManifestResult result = ManifestReader.Read(Module(1, "linux-x64"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("«platform» necesita «formatVersion» 2", StringComparison.Ordinal));
        Assert.True(PackageManifest.PlatformFormatVersion > 1);
    }

    [Fact]
    public void A_format_2_module_without_platform_is_rejected()
    {
        ManifestResult result = ManifestReader.Read(Module(2, null));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("lleva «platform»", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("linux-arm64")]
    [InlineData("Linux-x64")]
    [InlineData("osx-x64")]
    public void An_unknown_platform_is_rejected(string platform)
    {
        ManifestResult result = ManifestReader.Read(Module(2, platform));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no se conoce", StringComparison.Ordinal));
    }

    [Fact]
    public void Windows_modules_and_content_stay_in_format_1_without_platform()
    {
        PackageManifest module = ManifestReader.Read(Module(1, null)).Manifest!;
        PackageManifest content = ManifestReader.Read(Samples.Manifest()).Manifest!;

        Assert.Null(module.Platform);
        Assert.True(module.RunsOn("linux-x64") && module.RunsOn("win-x64"));
        Assert.True(content.RunsOn("osx-arm64"));
    }

    [Fact]
    public void This_platform_is_one_of_the_known_ones_on_the_test_runners() =>
        Assert.Contains(PackageManifest.ThisPlatform, PackageManifest.KnownPlatforms);

    [Fact]
    public void The_installer_refuses_a_module_built_for_another_system()
    {
        string other = PackageManifest.ThisPlatform == "linux-x64" ? "osx-arm64" : "linux-x64";
        var installer = new PackageInstaller(PackageLocations.Under(_folder.Combine("complementos")), "rodaje");
        string archive = Samples.Zip(_folder.Combine("otro.paquete"), ("paquete.json", Module(2, other)), ("rodaje-modulo", "#!/bin/sh"));

        PackageInstallException error = Assert.Throws<PackageInstallException>(() => installer.Install(archive));

        Assert.Contains($"es para {other}", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(installer.Locations.Modules, "modulo-prueba")));
    }

    [Fact]
    public void The_installer_takes_a_module_built_for_this_system()
    {
        var installer = new PackageInstaller(PackageLocations.Under(_folder.Combine("complementos")), "rodaje");
        string archive = Samples.Zip(_folder.Combine("propio.paquete"),
            ("paquete.json", Module(2, PackageManifest.ThisPlatform)), ("rodaje-modulo", "#!/bin/sh"));

        InstallResult result = installer.Install(archive);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        Assert.Equal(PackageManifest.ThisPlatform, result.Package.Manifest.Platform);
    }
}
