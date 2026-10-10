using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Shortcuts;

public class ShortcutStoreTests
{
    private static KeyChord K(string text) => KeyChord.Parse(text);

    private static ShortcutRegistry Catalog()
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        return registry;
    }

    [Fact]
    public void GuardarYLeer_ConservaLosCambiosDeCadaEstilo()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ShortcutStore.FileName);
        ShortcutRegistry original = Catalog();
        original.Rebind(ActionIds.Split, K("X"), replacing: K("S"));
        original.Preset = ShortcutPreset.Vegas;
        original.RemoveKey(ActionIds.Delete, K("Supr"));
        new ShortcutStore(path).Save(original);

        ShortcutRegistry loaded = Catalog();
        Assert.Null(new ShortcutStore(path).Load(loaded));

        Assert.Equal(ActionIds.Split, loaded.FindAction(K("X")));
        loaded.Preset = ShortcutPreset.Vegas;
        Assert.Empty(loaded.GetKeys(ActionIds.Delete));
        string json = File.ReadAllText(path);
        Assert.Contains("\"Rodaje\"", json, StringComparison.Ordinal);
        Assert.Contains("\"X\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Mayús", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SinArchivo_NoAvisaNiEscribeSiNoHayCambios()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ShortcutStore.FileName);
        var store = new ShortcutStore(path);

        Assert.Null(store.Load(Catalog()));
        store.Save(Catalog());

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void NoReescribeLoMismo()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ShortcutStore.FileName);
        var store = new ShortcutStore(path);
        ShortcutRegistry registry = Catalog();
        registry.Rebind(ActionIds.Undo, K("U"));
        store.Save(registry);
        DateTime first = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, first.AddMinutes(-5));

        store.Save(registry);

        Assert.Equal(first.AddMinutes(-5), File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void ArchivoDañado_SeApartaYSeUsanLosDeFabrica()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ShortcutStore.FileName);
        File.WriteAllText(path, "{ esto no es json");
        ShortcutRegistry registry = Catalog();

        string? warning = new ShortcutStore(path).Load(registry);

        Assert.NotNull(warning);
        Assert.Contains("dañado", warning, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(dir.File("atajos.dañado.json")));
        Assert.Equal(ActionIds.Undo, registry.FindAction(K("Ctrl+Z")));
    }

    [Fact]
    public void IgnoraEstilosYTeclasDesconocidos()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ShortcutStore.FileName);
        File.WriteAllText(path, """
            { "version": 1, "presets": {
                "Premiere": { "edit.undo": ["Ctrl+U"] },
                "capcut": { "edit.undo": ["Ctrl+U", "Ctrl+Hiper"], "otra.cosa": ["F9"] } } }
            """);
        ShortcutRegistry registry = Catalog();

        Assert.Null(new ShortcutStore(path).Load(registry));

        Assert.Equal(ActionIds.Undo, registry.FindAction(K("Ctrl+Z")));
        registry.Preset = ShortcutPreset.CapCut;
        Assert.Equal([K("Ctrl+U")], registry.GetKeys(ActionIds.Undo));
        Assert.Equal([K("F9")], registry.GetKeys("otra.cosa"));
    }
}
