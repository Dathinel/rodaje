using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

public class ModifierKeysTests
{
    private static KeyModifiers From(bool leftControl = false, bool rightControl = false, bool shift = false, bool leftAlt = false, bool rightAlt = false, bool windows = false) =>
        ModifierKeys.From(leftControl, rightControl, shift, leftAlt, rightAlt, windows);

    [Fact]
    public void AltGr_NoEsCtrlAlt()
    {
        Assert.Equal(KeyModifiers.None, From(leftControl: true, rightAlt: true));
        Assert.Equal(KeyModifiers.Shift, From(leftControl: true, rightAlt: true, shift: true));
    }

    [Fact]
    public void CtrlAltDeVerdad_SiguenContando()
    {
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, From(leftControl: true, leftAlt: true));
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, From(rightControl: true, leftAlt: true));
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, From(leftControl: true, rightControl: true, rightAlt: true));
    }

    [Fact]
    public void CadaTeclaSola()
    {
        Assert.Equal(KeyModifiers.Control, From(leftControl: true));
        Assert.Equal(KeyModifiers.Control, From(rightControl: true));
        Assert.Equal(KeyModifiers.Alt, From(rightAlt: true));
        Assert.Equal(KeyModifiers.Alt, From(leftAlt: true));
        Assert.Equal(KeyModifiers.Windows, From(windows: true));
        Assert.Equal(KeyModifiers.None, From());
    }
}
