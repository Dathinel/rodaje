using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

public class ShortcutRegistryTests
{
    private static KeyChord K(string text) => KeyChord.Parse(text);

    private static ShortcutRegistry Catalog()
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        return registry;
    }

    private static ShortcutRegistry Small()
    {
        var registry = new ShortcutRegistry();
        registry.Register(new ShortcutAction("a", "Acción A", "Grupo", ["A"]));
        registry.Register(new ShortcutAction("b", "Acción B", "Grupo", ["B", "Ctrl+B"]));
        registry.Register(new ShortcutAction("c", "Acción C", "Otro", []));
        return registry;
    }

    [Fact]
    public void ElCatalogoNoTieneConflictosEnNingunEstilo()
    {
        ShortcutRegistry registry = Catalog();
        foreach (ShortcutPreset preset in Enum.GetValues<ShortcutPreset>())
        {
            registry.Preset = preset;
            Assert.Empty(registry.FindConflicts());
        }
    }

    [Fact]
    public void ElCatalogoTieneIdentificadoresUnicosYTextosEnEspañol()
    {
        Assert.Equal(EditorShortcuts.Catalog.Count, EditorShortcuts.Catalog.Select(a => a.Id).Distinct().Count());
        Assert.All(EditorShortcuts.Catalog, a => Assert.False(string.IsNullOrWhiteSpace(a.Label)));
    }

    [Fact]
    public void CadaEstiloDaSusTeclas()
    {
        ShortcutRegistry registry = Catalog();
        Assert.Equal(ActionIds.Split, registry.FindAction(K("S")));
        Assert.Equal(ActionIds.Split, registry.FindAction(K("Ctrl+B")));
        registry.Preset = ShortcutPreset.Agile;
        Assert.Null(registry.FindAction(K("S")));
        Assert.Equal(ActionIds.Split, registry.FindAction(K("Ctrl+B")));
        registry.Preset = ShortcutPreset.Classic;
        Assert.Equal(ActionIds.Split, registry.FindAction(K("S")));
        Assert.Equal(ActionIds.TrimStart, registry.FindAction(K("Alt+[")));
        Assert.Equal(ActionIds.PreviousFrame, registry.FindAction(K("Alt+←")));
    }

    [Fact]
    public void GetKeyText_DaLaPrimeraTeclaEnEspañol()
    {
        ShortcutRegistry registry = Catalog();
        Assert.Equal("Ctrl+Mayús+Z", registry.GetKeyText(ActionIds.Redo));
        Assert.Equal("Espacio", registry.GetKeyText(ActionIds.TogglePlay));
        Assert.Equal("E", registry.GetKeyText(ActionIds.EnvelopeTool));
        Assert.Equal("", registry.GetKeyText(ActionIds.SelectionTool));
        Assert.Equal("", registry.GetKeyText("no.existe"));
    }

    [Fact]
    public void Register_RechazaRepetidos()
    {
        ShortcutRegistry registry = Small();
        Assert.Throws<InvalidOperationException>(() => registry.Register(new ShortcutAction("a", "Otra", "Grupo", [])));
    }

    [Fact]
    public void Rebind_ReemplazandoUnaTeclaOcupada_Intercambia()
    {
        ShortcutRegistry registry = Small();

        RebindResult result = registry.Rebind("a", K("B"), replacing: K("A"));

        Assert.True(result.Changed);
        Assert.Equal("b", result.TakenFromActionId);
        Assert.Equal(K("A"), result.GivenToOther);
        Assert.Equal([K("B")], registry.GetKeys("a"));
        Assert.Equal([K("A"), K("Ctrl+B")], registry.GetKeys("b"));
        Assert.Equal("a", registry.FindAction(K("B")));
        Assert.Equal("b", registry.FindAction(K("A")));
        Assert.Empty(registry.FindConflicts());
    }

    [Fact]
    public void Rebind_AñadiendoUnaTeclaOcupada_SeLaQuitaALaOtra()
    {
        ShortcutRegistry registry = Small();

        RebindResult result = registry.Rebind("c", K("Ctrl+B"));

        Assert.Equal("b", result.TakenFromActionId);
        Assert.Null(result.GivenToOther);
        Assert.Equal([K("Ctrl+B")], registry.GetKeys("c"));
        Assert.Equal([K("B")], registry.GetKeys("b"));
        Assert.True(registry.IsCustomized("b"));
        Assert.True(registry.IsCustomized("c"));
    }

    [Fact]
    public void Rebind_TeclaLibre_LaAñadeSinTocarOtras()
    {
        ShortcutRegistry registry = Small();
        int changes = 0;
        registry.Changed += (_, _) => changes++;

        RebindResult result = registry.Rebind("a", K("Ctrl+Mayús+A"));

        Assert.True(result.Changed);
        Assert.Null(result.TakenFromActionId);
        Assert.Equal([K("A"), K("Ctrl+Mayús+A")], registry.GetKeys("a"));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Rebind_LaMismaTecla_NoCambiaNada()
    {
        ShortcutRegistry registry = Small();
        Assert.Same(RebindResult.None, registry.Rebind("a", K("A")));
        Assert.Same(RebindResult.None, registry.Rebind("a", K("A"), replacing: K("A")));
        Assert.False(registry.IsCustomized("a"));
    }

    [Fact]
    public void Rebind_VolverALasDeFabrica_DejaDeContarComoCambio()
    {
        ShortcutRegistry registry = Small();
        registry.Rebind("a", K("X"), replacing: K("A"));
        Assert.True(registry.IsCustomized("a"));

        registry.Rebind("a", K("A"), replacing: K("X"));

        Assert.False(registry.IsCustomized("a"));
    }

    [Fact]
    public void Rebind_ValidaArgumentos()
    {
        ShortcutRegistry registry = Small();
        Assert.Throws<ArgumentException>(() => registry.Rebind("nada", K("A")));
        Assert.Throws<ArgumentException>(() => registry.Rebind("a", new KeyChord(0x11)));
    }

    [Fact]
    public void RemoveKey_PuedeDejarLaAccionSinTeclas()
    {
        ShortcutRegistry registry = Small();
        Assert.True(registry.RemoveKey("a", K("A")));
        Assert.Empty(registry.GetKeys("a"));
        Assert.Null(registry.FindAction(K("A")));
        Assert.False(registry.RemoveKey("a", K("A")));
    }

    [Fact]
    public void LosCambiosSonPorEstilo_YResetVuelveALosDeFabrica()
    {
        ShortcutRegistry registry = Catalog();
        registry.Rebind(ActionIds.Split, K("X"), replacing: K("S"));
        Assert.Equal(ActionIds.Split, registry.FindAction(K("X")));

        registry.Preset = ShortcutPreset.Classic;
        Assert.Null(registry.FindAction(K("X")));
        Assert.Equal(ActionIds.Split, registry.FindAction(K("S")));

        registry.Preset = ShortcutPreset.Rodaje;
        Assert.Equal(ActionIds.Split, registry.FindAction(K("X")));
        registry.Reset(ActionIds.Split);
        Assert.Equal(ActionIds.Split, registry.FindAction(K("S")));

        registry.Rebind(ActionIds.Undo, K("U"));
        registry.Rebind(ActionIds.Redo, K("Mayús+U"));
        registry.ResetAll();
        Assert.Empty(registry.GetOverrides(ShortcutPreset.Rodaje));
    }

    [Fact]
    public void FindConflicts_DetectaTeclasRepetidasDeVistas()
    {
        ShortcutRegistry registry = Catalog();
        registry.Register(new ShortcutAction("timeline.blade", "Cuchilla", ShortcutGroups.Tools, ["S"]));

        ShortcutConflict conflict = Assert.Single(registry.FindConflicts());

        Assert.Equal(K("S"), conflict.Chord);
        Assert.Equal([ActionIds.Split, "timeline.blade"], conflict.ActionIds);
        Assert.Equal(ActionIds.Split, registry.FindAction(K("S")));
    }

    [Fact]
    public void LosCambiosDeAccionesQueTodaviaNoExisten_SeAplicanAlRegistrarlas()
    {
        var registry = new ShortcutRegistry();
        registry.SetOverrides(ShortcutPreset.Rodaje, new Dictionary<string, IReadOnlyList<KeyChord>> { ["tarde"] = [K("Ctrl+T")] });

        registry.Register(new ShortcutAction("tarde", "Llega tarde", "Grupo", ["T"]));

        Assert.Equal("tarde", registry.FindAction(K("Ctrl+T")));
        Assert.Null(registry.FindAction(K("T")));
    }

    [Theory]
    [InlineData("deshacer", ActionIds.Undo)]
    [InlineData("EDICION cortar", ActionIds.Split)]
    [InlineData("mayus z", ActionIds.Redo)]
    [InlineData("espacio", ActionIds.TogglePlay)]
    [InlineData("region", ActionIds.AddRegion)]
    public void Search_IgnoraTildesYMayusculas(string text, string expected)
    {
        Assert.Contains(Catalog().Search(text), a => a.Id == expected);
    }

    [Fact]
    public void Search_SinTexto_DevuelveTodo()
    {
        ShortcutRegistry registry = Catalog();
        Assert.Equal(registry.Actions.Count, registry.Search("  ").Count());
        Assert.Empty(registry.Search("zzz-nada"));
    }
}
