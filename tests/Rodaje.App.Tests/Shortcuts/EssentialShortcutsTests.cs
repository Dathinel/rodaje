using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

public class EssentialShortcutsTests
{
    // Las de la lanzadera las registra el reproductor; aquí se registran igual para ver la lista completa.
    private static ShortcutRegistry Catalog(ShortcutPreset preset = ShortcutPreset.Rodaje)
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        registry.Register(new ShortcutAction(ActionIds.ShuttleReverse, "Lanzadera hacia atrás", ShortcutGroups.Playback, ["J"]));
        registry.Register(new ShortcutAction(ActionIds.ShuttleStop, "Lanzadera: detener", ShortcutGroups.Playback, ["K"]));
        registry.Register(new ShortcutAction(ActionIds.ShuttleForward, "Lanzadera hacia adelante", ShortcutGroups.Playback, ["L"]));
        registry.Preset = preset;
        return registry;
    }

    [Theory]
    [InlineData(ShortcutPreset.Rodaje)]
    [InlineData(ShortcutPreset.Agile)]
    [InlineData(ShortcutPreset.Classic)]
    public void Select_DaLasEsencialesEnSuOrdenYConTecla(ShortcutPreset preset)
    {
        IReadOnlyList<CheatSheetEntry> entries = EssentialShortcuts.Select(Catalog(preset));

        Assert.InRange(entries.Count, 12, 18);
        Assert.Equal(ActionIds.TogglePlay, entries[0].ActionId);
        Assert.Equal([.. EssentialShortcuts.Ids.Where(id => entries.Any(e => e.ActionId == id))], entries.Select(e => e.ActionId));
        Assert.All(entries, e => Assert.InRange(e.Keys.Count, 1, 2));
        Assert.Contains(entries, e => e.ActionId == ActionIds.ShuttleStop && e.Keys.SequenceEqual(["K"]));
    }

    [Fact]
    public void Ids_SonAccionesQueExisten()
    {
        ShortcutRegistry registry = Catalog();
        Assert.All(EssentialShortcuts.Ids, id => Assert.NotNull(registry.Find(id)));
        Assert.Equal(EssentialShortcuts.Ids.Count, EssentialShortcuts.Ids.Distinct().Count());
    }

    [Fact]
    public void Select_SaltaLasQueNoTienenTeclaOFaltan()
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        registry.RemoveKey(ActionIds.Save, KeyChord.Parse("Ctrl+S"));

        IReadOnlyList<CheatSheetEntry> entries = EssentialShortcuts.Select(registry);

        Assert.DoesNotContain(entries, e => e.ActionId == ActionIds.Save);
        Assert.DoesNotContain(entries, e => e.ActionId == ActionIds.ShuttleForward);
    }

    [Fact]
    public void ElTecladoEsencial_SoloMuestraLasEsenciales()
    {
        IReadOnlyList<KeyBinding> bindings = [.. KeyboardGeometry.Map(Catalog(), null, include: EssentialShortcuts.Contains).SelectMany(k => k.Bindings)];

        Assert.All(bindings, b => Assert.Contains(b.Action.Id, EssentialShortcuts.Ids));
        Assert.Contains(bindings, b => b.Action.Id == ActionIds.Split);
        Assert.DoesNotContain(bindings, b => b.Action.Id == ActionIds.NormalTool);
    }
}
