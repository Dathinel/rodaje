using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

public class CheatSheetTests
{
    private static ShortcutRegistry Catalog(ShortcutPreset preset = ShortcutPreset.Rodaje)
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        registry.Preset = preset;
        return registry;
    }

    [Theory]
    [InlineData(ShortcutPreset.Rodaje)]
    [InlineData(ShortcutPreset.Agile)]
    [InlineData(ShortcutPreset.Classic)]
    public void Select_DaQuinceAtajosConTeclaEmpezandoPorLosMasUtiles(ShortcutPreset preset)
    {
        IReadOnlyList<CheatSheetEntry> entries = CheatSheet.Select(Catalog(preset));

        Assert.Equal(CheatSheet.DefaultCount, entries.Count);
        Assert.Equal(ActionIds.TogglePlay, entries[0].ActionId);
        Assert.Equal(entries.Count, entries.Select(e => e.ActionId).Distinct().Count());
        Assert.All(entries, e => Assert.InRange(e.Keys.Count, 1, 2));
    }

    [Fact]
    public void Select_SigueLosCambiosDeLaPersonaYSaltaLasAccionesSinTecla()
    {
        ShortcutRegistry registry = Catalog();
        registry.RemoveKey(ActionIds.Split, KeyChord.Parse("S"));
        registry.RemoveKey(ActionIds.Split, KeyChord.Parse("Ctrl+B"));

        IReadOnlyList<CheatSheetEntry> entries = CheatSheet.Select(registry);

        Assert.DoesNotContain(entries, e => e.ActionId == ActionIds.Split);
        Assert.Equal(CheatSheet.DefaultCount, entries.Count);
        Assert.Equal(["Espacio"], entries[0].Keys);
    }

    [Fact]
    public void ToHtml_EsUnaPaginaSolaYEscapaElTexto()
    {
        CheatSheetEntry[] entries =
        [
            new("a", "Reproducir o pausar", ShortcutGroups.Playback, ["Espacio"]),
            new("b", "Mover <izquierda> & más", ShortcutGroups.Edit, ["Ctrl+<", "Alt+'"]),
        ];

        string html = CheatSheet.ToHtml(entries, "Atajos esenciales", "Estilo Rodaje · los 2 más útiles");

        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"es\">", html, StringComparison.Ordinal);
        Assert.Contains("<title>Atajos esenciales</title>", html, StringComparison.Ordinal);
        Assert.Contains("<kbd>Espacio</kbd>", html, StringComparison.Ordinal);
        Assert.Contains("Mover &lt;izquierda&gt; &amp; más", html, StringComparison.Ordinal);
        Assert.Contains("<kbd>Ctrl+&lt;</kbd> <kbd>Alt+&#39;</kbd>", html, StringComparison.Ordinal);
        Assert.Contains("@media print", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http", html, StringComparison.Ordinal);
    }
}
