using System.Diagnostics;
using Rodaje.Paquetes.Content;

namespace Rodaje.Paquetes.Tests;

/// <summary>El paquete propio <c>contenido/rodaje-esenciales</c>: fuentes válidas y el .paquete armado por el script.</summary>
public sealed class EsencialesTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Source = Path.Combine(Root, "contenido", "rodaje-esenciales");

    private static string FindRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Rodaje.slnx")))
            {
                return d.FullName;
            }
        }

        throw new InvalidOperationException("No encuentro la raíz del repositorio.");
    }

    private static PackageManifest Manifest()
    {
        ManifestResult result = ManifestReader.ReadFile(Path.Combine(Source, PackageManifest.FileName), checkFiles: false);
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Empty(result.Warnings);
        return result.Manifest!;
    }

    [Fact]
    public void Trae_8_transiciones_8_filtros_y_8_LUT_validos()
    {
        PackageManifest manifest = Manifest();
        Assert.Equal("CC0-1.0", manifest.License);
        Assert.Equal(8, manifest.Items.Count(i => i.Kind == ItemKind.Transition));
        Assert.Equal(8, manifest.Items.Count(i => i.Kind == ItemKind.Filter));
        Assert.Equal(8, manifest.Items.Count(i => i.Kind == ItemKind.Lut));

        foreach (PackageItem item in manifest.Items.Where(i => i.Kind == ItemKind.Transition))
        {
            using FileStream stream = File.OpenRead(Path.Combine(Source, item.File!));
            TransitionPreset preset = ContentReaders.ReadTransition(stream);
            Assert.Equal(item.Id, preset.Id);
            Assert.Equal(0.5, preset.DurationSeconds);
            Assert.NotEmpty(preset.Incoming);
        }

        foreach (PackageItem item in manifest.Items.Where(i => i.Kind == ItemKind.Filter))
        {
            using FileStream stream = File.OpenRead(Path.Combine(Source, item.File!));
            FilterPreset filter = ContentReaders.ReadFilter(stream);
            Assert.Equal(item.Id, filter.Id);
            string lut = filter.Effects[0].Options["lut"];
            Assert.Contains(manifest.Items, i => i.Kind == ItemKind.Lut && "./" + i.Id == lut);
        }
    }

    [Fact]
    public async Task El_script_arma_un_paquete_que_instala_aplica_y_al_desinstalar_avisa()
    {
        using var folder = new TempFolder();
        string output = folder.Combine("salida");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Path.Combine(Root, "herramientas", "empaquetar.ps1"));
        start.ArgumentList.Add("-Salida");
        start.ArgumentList.Add(output);
        using (Process process = Process.Start(start)!)
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "empaquetar.ps1 no terminó en 2 min");
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
        }

        string archive = Path.Combine(output, "rodaje-esenciales-1.0.0.paquete");
        var locations = PackageLocations.Under(folder.Combine("datos"));
        var installer = new PackageInstaller(locations, "rodaje");
        Assert.Equal(InstallOutcome.Installed, installer.Install(archive).Outcome);

        using var store = new PackageContentStore(PackageLibrary.Scan(locations, "rodaje"));
        FilterPreset? cine = store.FindFilter("rodaje-esenciales@1.0.0/cine");
        Assert.NotNull(cine);
        string lut = cine.Effects[0].Options["lut"];
        Assert.Equal("rodaje-esenciales@1.0.0/lut-cine", lut);
        foreach (string look in new[] { "calido", "frio", "verde-azulado-naranja", "blanco-y-negro", "desvaido", "vivo", "noche", "cine" })
        {
            store.TryGetLut($"rodaje-esenciales@1.0.0/lut-{look}", out _);
        }

        await store.WhenLoaded();
        Assert.True(store.TryGetLut(lut, out LutTable? table));
        Assert.Equal(33, table.Size);
        Assert.All(table.Rgb, v => Assert.InRange(v, 0f, 1f));
        Assert.True(store.TryGetLut("rodaje-esenciales@1.0.0/lut-blanco-y-negro", out LutTable? gray));
        Assert.All(Enumerable.Range(0, gray.Rgb.Length / 3), i => Assert.Equal(gray.Rgb[i * 3], gray.Rgb[(i * 3) + 2]));
        Assert.NotNull(store.FindTransition("rodaje-esenciales@1.0.0/girar"));

        installer.Uninstall("rodaje-esenciales");
        store.Reload(PackageLibrary.Scan(locations, "rodaje"));
        Assert.False(store.TryGetLut(lut, out _));
        Assert.Contains("Falta el paquete «rodaje-esenciales»", store.LutProblem(lut), StringComparison.Ordinal);
        Assert.Null(store.FindTransition("rodaje-esenciales@1.0.0/girar"));
        Assert.Contains("Falta el paquete", store.PresetProblem("rodaje-esenciales@1.0.0/girar"), StringComparison.Ordinal);
    }
}
