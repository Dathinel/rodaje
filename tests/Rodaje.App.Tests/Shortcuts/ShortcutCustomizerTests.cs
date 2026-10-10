using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

public class ShortcutCustomizerTests
{
    private static KeyChord K(string text) => KeyChord.Parse(text);

    private static ShortcutRegistry Small()
    {
        var registry = new ShortcutRegistry();
        registry.Register(new ShortcutAction("a", "Acción A", "Grupo", ["A"]));
        registry.Register(new ShortcutAction("b", "Acción B", "Grupo", ["B", "Ctrl+B"]));
        registry.Register(new ShortcutAction("c", "Acción C", "Otro", []));
        return registry;
    }

    private static ShortcutRegistry Catalog()
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        return registry;
    }

    [Fact]
    public void Assign_TeclaDeOtraAccion_IntercambiaYAvisa()
    {
        ShortcutRegistry registry = Catalog();
        var customizer = new ShortcutCustomizer(registry);

        ShortcutChange change = customizer.Assign(ActionIds.TrimStart, K("S"), replacing: K("Q"));

        Assert.True(change.Changed);
        Assert.Equal("«S» era de «Cortar en el cursor»: se intercambiaron", change.Message);
        Assert.Equal([K("S")], registry.GetKeys(ActionIds.TrimStart));
        Assert.Equal([K("Q"), K("Ctrl+B")], registry.GetKeys(ActionIds.Split));
        Assert.Empty(registry.FindConflicts());
    }

    [Fact]
    public void Undo_DejaTodoComoEstaba()
    {
        ShortcutRegistry registry = Catalog();
        var customizer = new ShortcutCustomizer(registry);
        ShortcutChange change = customizer.Assign(ActionIds.TrimStart, K("S"), replacing: K("Q"));

        Assert.True(customizer.Undo(change));

        Assert.Equal([K("Q")], registry.GetKeys(ActionIds.TrimStart));
        Assert.Equal([K("S"), K("Ctrl+B")], registry.GetKeys(ActionIds.Split));
        Assert.Empty(registry.GetOverrides(ShortcutPreset.Rodaje));
    }

    [Fact]
    public void Undo_RespetaLosCambiosAnterioresDelEstilo()
    {
        ShortcutRegistry registry = Small();
        var customizer = new ShortcutCustomizer(registry);
        customizer.Assign("c", K("Ctrl+Mayús+C"));
        ShortcutChange second = customizer.Assign("a", K("X"), replacing: K("A"));

        customizer.Undo(second);

        Assert.Equal([K("A")], registry.GetKeys("a"));
        Assert.Equal([K("Ctrl+Mayús+C")], registry.GetKeys("c"));
    }

    [Fact]
    public void Assign_AñadirUnaTeclaOcupada_SeLaQuitaALaOtraYLoDice()
    {
        ShortcutRegistry registry = Small();
        var customizer = new ShortcutCustomizer(registry);

        ShortcutChange change = customizer.Assign("a", K("Ctrl+B"));

        Assert.Equal("«Ctrl+B» era de «Acción B»; ahora es de «Acción A»", change.Message);
        Assert.Equal([K("A"), K("Ctrl+B")], registry.GetKeys("a"));
        Assert.Equal([K("B")], registry.GetKeys("b"));
    }

    [Fact]
    public void Assign_TeclaLibre_DiceComoSeLanzaAhora()
    {
        ShortcutRegistry registry = Small();
        var customizer = new ShortcutCustomizer(registry);

        Assert.Equal("«Acción A» ahora también se lanza con «Ctrl+Mayús+A»", customizer.Assign("a", K("Ctrl+Mayús+A")).Message);
        Assert.Equal("«Acción C» ahora se lanza con «F5»", customizer.Assign("c", K("F5")).Message);
        Assert.Equal("«Acción A» ahora se lanza con «X»", customizer.Assign("a", K("X"), replacing: K("A")).Message);
    }

    [Fact]
    public void Assign_LaMismaTecla_NoCambiaNiSePuedeDeshacer()
    {
        ShortcutRegistry registry = Small();
        var customizer = new ShortcutCustomizer(registry);

        ShortcutChange change = customizer.Assign("a", K("A"), replacing: K("A"));

        Assert.False(change.Changed);
        Assert.Equal("«A» ya era una tecla de «Acción A».", change.Message);
        Assert.False(customizer.Undo(change));
    }

    [Fact]
    public void Assign_Reservada_NoSeAsigna()
    {
        ShortcutRegistry registry = Small();
        var customizer = new ShortcutCustomizer(registry);

        ShortcutChange change = customizer.Assign("a", K("Alt+F4"), replacing: K("A"));

        Assert.False(change.Changed);
        Assert.Contains("Alt+F4", change.Message, StringComparison.Ordinal);
        Assert.Equal([K("A")], registry.GetKeys("a"));
    }

    [Fact]
    public void RemoveKey_AvisaYSePuedeDeshacer()
    {
        ShortcutRegistry registry = Small();
        var customizer = new ShortcutCustomizer(registry);

        ShortcutChange first = customizer.RemoveKey("b", K("B"));
        ShortcutChange last = customizer.RemoveKey("b", K("Ctrl+B"));

        Assert.Equal("Se quitó «B» de «Acción B»", first.Message);
        Assert.Equal("Se quitó «Ctrl+B» de «Acción B»; ya no tiene tecla", last.Message);
        Assert.Empty(registry.GetKeys("b"));
        customizer.Undo(last);
        Assert.Equal([K("Ctrl+B")], registry.GetKeys("b"));
        Assert.False(customizer.RemoveKey("b", K("Z")).Changed);
    }

    [Fact]
    public void Reset_YResetAll_SePuedenDeshacer()
    {
        ShortcutRegistry registry = Small();
        var customizer = new ShortcutCustomizer(registry);
        customizer.Assign("a", K("X"), replacing: K("A"));
        customizer.Assign("c", K("F5"));

        ShortcutChange reset = customizer.Reset("a");
        Assert.Equal("«Acción A» volvió a sus teclas de fábrica", reset.Message);
        Assert.False(registry.IsCustomized("a"));
        Assert.False(customizer.Reset("a").Changed);

        ShortcutChange all = customizer.ResetAll();
        Assert.Equal("El estilo Rodaje volvió a sus teclas de fábrica", all.Message);
        Assert.Empty(registry.GetOverrides(ShortcutPreset.Rodaje));
        Assert.False(customizer.ResetAll().Changed);

        customizer.Undo(all);
        Assert.Equal([K("F5")], registry.GetKeys("c"));
        customizer.Undo(reset);
        Assert.Equal([K("X")], registry.GetKeys("a"));
    }

    [Fact]
    public void Undo_TrasCambiarDeEstilo_VuelveAlEstiloDelCambio()
    {
        ShortcutRegistry registry = Catalog();
        var customizer = new ShortcutCustomizer(registry);
        ShortcutChange change = customizer.Assign(ActionIds.AddMarker, K("Ctrl+M"), replacing: K("M"));
        registry.Preset = ShortcutPreset.CapCut;
        customizer.Assign(ActionIds.AddMarker, K("Ctrl+Mayús+M"));

        customizer.Undo(change);

        Assert.Empty(registry.GetOverrides(ShortcutPreset.Rodaje));
        Assert.Single(registry.GetOverrides(ShortcutPreset.CapCut));
    }

    [Fact]
    public void ConflictsOf_DaLasOtrasAccionesConLaMismaTecla()
    {
        ShortcutRegistry registry = Catalog();
        registry.Register(new ShortcutAction("timeline.blade", "Cuchilla", ShortcutGroups.Tools, ["S"]));
        var customizer = new ShortcutCustomizer(registry);

        Assert.Equal(["timeline.blade"], customizer.ConflictsOf(ActionIds.Split));
        Assert.Equal([ActionIds.Split], customizer.ConflictsOf("timeline.blade"));
        Assert.Empty(customizer.ConflictsOf(ActionIds.Undo));
    }

    [Fact]
    public void LosAvisos_UsanLaDistribucionDada()
    {
        ShortcutRegistry registry = Small();
        var customizer = new ShortcutCustomizer(registry, k => KeyNames.Format(k, FakeKeyboardLayout.Spanish));

        Assert.Equal("«Acción C» ahora se lanza con «Ñ»", customizer.Assign("c", new KeyChord(0xC0)).Message);
    }
}
