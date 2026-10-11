using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

public class ZoomShortcutsTests
{
    [Theory]
    [InlineData(ShortcutPreset.Rodaje)]
    [InlineData(ShortcutPreset.Agile)]
    [InlineData(ShortcutPreset.Classic)]
    public void AjustarYZoomALaSeleccionTienenTeclaPropiaEnCadaEstilo(ShortcutPreset preset)
    {
        ShortcutAction fit = EditorShortcuts.Catalog.Single(a => a.Id == ActionIds.ZoomToFit);
        ShortcutAction selection = EditorShortcuts.Catalog.Single(a => a.Id == ActionIds.ZoomToSelection);

        Assert.Equal([KeyChord.Parse("Mayús+Z")], fit.DefaultKeys(preset));
        Assert.Equal([KeyChord.Parse("Ctrl+Alt+Z")], selection.DefaultKeys(preset));

        foreach (ShortcutAction other in EditorShortcuts.Catalog.Where(a => a.Id is not ActionIds.ZoomToFit and not ActionIds.ZoomToSelection))
        {
            Assert.DoesNotContain(other.DefaultKeys(preset), k => fit.DefaultKeys(preset).Contains(k) || selection.DefaultKeys(preset).Contains(k));
        }
    }
}
