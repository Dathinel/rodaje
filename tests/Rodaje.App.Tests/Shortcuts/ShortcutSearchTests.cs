using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

public class ShortcutSearchTests
{
    private static ShortcutRegistry Catalog()
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        return registry;
    }

    private static string[] Find(string query, IKeyboardLayout? layout = null) =>
        [.. ShortcutSearch.Filter(Catalog(), query, layout).Select(a => a.Id)];

    [Theory]
    [InlineData("s")]
    [InlineData("S")]
    public void UnaLetraSola_BuscaLaTecla(string query)
    {
        Assert.Equal([ActionIds.Split], Find(query));
    }

    [Theory]
    [InlineData("ctrl+b", ActionIds.Split)]
    [InlineData("Ctrl+Mayús+S", ActionIds.SaveAs)]
    [InlineData("control+shift+s", ActionIds.SaveAs)]
    [InlineData("ctrl+s", ActionIds.Save)]
    public void UnaCombinacion_BuscaSoloEsaTecla(string query, string expected)
    {
        Assert.Equal([expected], Find(query));
    }

    [Theory]
    [InlineData("cortar", ActionIds.Split)]
    [InlineData("CORTAR", ActionIds.Split)]
    [InlineData("edición deshacer", ActionIds.Undo)]
    [InlineData("edicion deshacer", ActionIds.Undo)]
    [InlineData("pausa", ActionIds.TogglePlay)]
    [InlineData("supr", ActionIds.Delete)]
    [InlineData("mayus z", ActionIds.Redo)]
    [InlineData("iman", ActionIds.ToggleSnapping)]
    [InlineData("huecos", ActionIds.ToggleAutoRipple)]
    [InlineData("añadir", ActionIds.AddMarker)]
    public void UnTexto_BuscaEnNombreExplicacionGrupoYTeclas(string query, string expected)
    {
        Assert.Contains(expected, Find(query));
    }

    [Fact]
    public void UnaLetraQueNingunaAccionUsa_SeBuscaComoTexto()
    {
        Assert.Contains(ActionIds.SelectAll, Find("a"));
    }

    [Fact]
    public void ConDistribucionEspañola_EntiendeLosSignosImpresos()
    {
        ShortcutRegistry registry = Catalog();
        registry.Register(new ShortcutAction("x.enie", "Algo con eñe", "Grupo", ["Ctrl+`"]));

        Assert.Equal(["x.enie"], ShortcutSearch.Filter(registry, "ctrl+ñ", FakeKeyboardLayout.Spanish).Select(a => a.Id));
        registry.Preset = ShortcutPreset.Vegas;
        Assert.Equal([ActionIds.TrimStart], ShortcutSearch.Filter(registry, "alt+'", FakeKeyboardLayout.Spanish).Select(a => a.Id));
    }

    [Fact]
    public void SinTexto_DevuelveTodasYNadaCoincide_NoDevuelveNinguna()
    {
        Assert.Equal(EditorShortcuts.Catalog.Count, Find("   ").Length);
        Assert.Empty(Find("zzz-nada"));
    }

    [Fact]
    public void CadaAccionDelCatalogo_TieneUnaExplicacionCortaEnEspañol()
    {
        Assert.All(EditorShortcuts.Catalog, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Description), a.Id);
            Assert.True(a.Description.Length <= 80, $"{a.Id}: {a.Description.Length} caracteres");
            Assert.EndsWith(".", a.Description, StringComparison.Ordinal);
            Assert.True(a.ShortLabel.Length <= 16, $"{a.Id}: «{a.ShortLabel}» no cabe en una tecla");
        });
    }

    [Fact]
    public void ShortLabel_SinDarlo_EsElTextoCompleto()
    {
        Assert.Equal("Algo largo", new ShortcutAction("x", "Algo largo", "Grupo", []).ShortLabel);
        Assert.Equal("Corto", new ShortcutAction("x", "Algo largo", "Grupo", []) { ShortLabel = "Corto" }.ShortLabel);
    }

    [Fact]
    public void LosGruposDelCatalogo_EstanEnElOrdenDeLaPagina()
    {
        Assert.All(EditorShortcuts.Catalog, a => Assert.Contains(a.Group, ShortcutGroups.Order));
    }
}
