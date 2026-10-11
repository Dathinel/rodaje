using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Tests.Support;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Tests.Settings;

public class SettingsStoreTests
{
    [Fact]
    public void GuardarYLeer_ConservaTodo()
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        var store = new SettingsStore(path);
        store.Current.LastMediaFolder = @"D:\Videos con espacios\ñ";
        store.Current.LibraryWidth = 410;
        store.Current.TimelineHeight = 222;
        store.Current.WindowWidth = 1600;
        store.Current.WindowHeight = 900;
        store.Current.WindowMaximized = true;
        store.Current.ShortcutPreset = ShortcutPreset.Classic;
        store.Current.ProxyUsage = ProxyUsage.Never;
        store.Current.ShortcutsAdvanced = true;
        store.Current.AddRecentProject(@"C:\a.rodaje");
        store.Save();

        var loaded = new SettingsStore(path);
        Assert.Null(loaded.Load());

        AppSettings s = loaded.Current;
        Assert.Equal(@"D:\Videos con espacios\ñ", s.LastMediaFolder);
        Assert.Equal(410, s.LibraryWidth);
        Assert.Equal(222, s.TimelineHeight);
        Assert.Equal(1600, s.WindowWidth);
        Assert.True(s.WindowMaximized);
        Assert.Equal(ShortcutPreset.Classic, s.ShortcutPreset);
        Assert.Equal(ProxyUsage.Never, s.ProxyUsage);
        Assert.True(s.ShortcutsAdvanced);
        Assert.Equal([@"C:\a.rodaje"], s.RecentProjects);
        Assert.Contains("\"Classic\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void SeccionesDelInspector_SeRecuerdanYUnNuloQuedaVacio()
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        var store = new SettingsStore(path);
        store.Current.InspectorSections["opacidad"] = false;
        store.Current.InspectorSections["audio"] = true;
        store.Save();

        var loaded = new SettingsStore(path);
        Assert.Null(loaded.Load());
        Assert.False(loaded.Current.InspectorSections["opacidad"]);
        Assert.True(loaded.Current.InspectorSections["audio"]);

        File.WriteAllText(path, "{\"version\":1,\"inspectorSections\":null}");
        var broken = new SettingsStore(path);
        Assert.Null(broken.Load());
        Assert.Empty(broken.Current.InspectorSections);
    }

    [Theory]
    [InlineData(320, 380)]
    [InlineData(500, 500)]
    public void AjustesVersion1_ElPanelDerechoPasaAlMenosA380(double before, double after)
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        File.WriteAllText(path, "{\"version\":1,\"inspectorWidth\":" + before.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
        var store = new SettingsStore(path);
        Assert.Null(store.Load());
        Assert.Equal(after, store.Current.InspectorWidth);
        Assert.Equal(AppSettings.CurrentVersion, store.Current.Version);

        store.Current.InspectorWidth = 300;
        store.Save();
        var again = new SettingsStore(path);
        Assert.Null(again.Load());
        Assert.Equal(300, again.Current.InspectorWidth);
    }

    [Fact]
    public void GuardarSiCambio_SoloEscribeCuandoHayAlgoNuevo()
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        var store = new SettingsStore(path);
        Assert.Null(store.Load());

        Assert.True(store.SaveIfChanged());
        Assert.False(store.SaveIfChanged());
        store.Current.LibraryWidth = 400;
        Assert.True(store.SaveIfChanged());
        Assert.False(store.SaveIfChanged());

        var reloaded = new SettingsStore(path);
        Assert.Null(reloaded.Load());
        Assert.Equal(400, reloaded.Current.LibraryWidth);
        Assert.False(reloaded.SaveIfChanged());
    }

    [Fact]
    public void SinArchivo_DaLosDeFabrica()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(dir.File(SettingsStore.FileName));
        Assert.Null(store.Load());
        Assert.Equal(280, store.Current.LibraryWidth);
        Assert.Equal(380, store.Current.InspectorWidth);
        Assert.Equal(ProxyUsage.Auto, store.Current.ProxyUsage);
        Assert.False(store.Current.ShortcutsAdvanced);
    }

    [Fact]
    public void ValoresSinSentido_SeCorrigen()
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        File.WriteAllText(path, """{ "libraryWidth": 5, "inspectorWidth": 99999, "timelineHeight": -3, "windowWidth": 10, "recentProjects": ["", "  ", "x.rodaje"] }""");
        var store = new SettingsStore(path);

        Assert.Null(store.Load());

        Assert.Equal(220, store.Current.LibraryWidth);
        Assert.Equal(900, store.Current.InspectorWidth);
        Assert.Equal(160, store.Current.TimelineHeight);
        Assert.Equal(800, store.Current.WindowWidth);
        Assert.Equal(["x.rodaje"], store.Current.RecentProjects);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("""{ "proxyUsage": "Quizas" }""")]
    public void ArchivoDañado_SeApartaYSeUsanLosDeFabrica(string content)
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        File.WriteAllText(path, content);
        var store = new SettingsStore(path);

        string? warning = store.Load();

        Assert.NotNull(warning);
        Assert.True(File.Exists(dir.File("ajustes.dañado.json")));
        Assert.Equal(ProxyUsage.Auto, store.Current.ProxyUsage);
    }

    [Fact]
    public void Recientes_SinRepetirYConMaximo()
    {
        var settings = new AppSettings();
        for (int i = 0; i < 15; i++)
        {
            settings.AddRecentProject($@"C:\p{i}.rodaje");
        }

        settings.AddRecentProject(@"C:\P3.RODAJE");

        Assert.Equal(AppSettings.MaxRecentProjects, settings.RecentProjects.Count);
        Assert.Equal(@"C:\P3.RODAJE", settings.RecentProjects[0]);
        Assert.Single(settings.RecentProjects, p => p.Equals(@"C:\p3.rodaje", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EscrituraAtomica_NoDejaTemporales()
    {
        using var dir = new TempDirectory();
        string path = dir.File("x.json");
        AppDataPaths.WriteAllTextAtomic(path, "uno");
        AppDataPaths.WriteAllTextAtomic(path, "dos");
        Assert.Equal("dos", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("CapCut", ShortcutPreset.Agile)]
    [InlineData("Vegas", ShortcutPreset.Classic)]
    [InlineData("Classic", ShortcutPreset.Classic)]
    [InlineData("Otro", ShortcutPreset.Rodaje)]
    [InlineData("2", ShortcutPreset.Classic)]
    public void ElEstiloDeAtajos_SeMigraSinDarPorDañadoElArchivo(string stored, ShortcutPreset expected)
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        string value = int.TryParse(stored, out _) ? stored : $"\"{stored}\"";
        File.WriteAllText(path, $$"""{ "version": 1, "libraryWidth": 410, "shortcutPreset": {{value}} }""");
        var store = new SettingsStore(path);

        Assert.Null(store.Load());

        Assert.Equal(expected, store.Current.ShortcutPreset);
        Assert.Equal(410, store.Current.LibraryWidth);
        store.Save();
        Assert.Contains($"\"{expected}\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void LaCarpetaDeDatos_SeCambiaConLaVariable()
    {
        using var dir = new TempDirectory();
        string? before = Environment.GetEnvironmentVariable(AppDataPaths.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, dir.Path);
            Assert.Equal(dir.File(SettingsStore.FileName), SettingsStore.DefaultPath);
            Assert.Equal(dir.File(ShortcutStore.FileName), ShortcutStore.DefaultPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, before);
        }
    }
}
