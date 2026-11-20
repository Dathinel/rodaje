using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

/// <summary>F6 del repaso del 19-nov: con el foco en el botón de keyframe, Espacio alternaba la reproducción en vez de pulsarlo.</summary>
public sealed class ShortcutFocusRulesTests
{
    [Fact]
    public void SpaceOnAButtonReachedWithTheKeyboardPressesIt()
    {
        var space = new KeyChord(ShortcutFocusRules.SpaceKey);

        Assert.True(ShortcutFocusRules.SpaceGoesToButton(space, keyboardFocusedButton: true));
        Assert.False(ShortcutFocusRules.SpaceGoesToButton(space, keyboardFocusedButton: false));
    }

    [Fact]
    public void OtherKeysAndModifiedSpaceStayShortcuts()
    {
        Assert.False(ShortcutFocusRules.SpaceGoesToButton(new KeyChord(0x4B), keyboardFocusedButton: true));
        Assert.False(ShortcutFocusRules.SpaceGoesToButton(new KeyChord(ShortcutFocusRules.SpaceKey, KeyModifiers.Shift), keyboardFocusedButton: true));
    }
}
