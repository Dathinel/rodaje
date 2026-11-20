using Rodaje.App.Services.Shortcuts;
using Windows.System;

namespace Rodaje.App.Tests.Shortcuts;

/// <summary>F6 del repaso del 19-nov: con el foco en el botón de keyframe, Espacio alternaba la reproducción en vez de pulsarlo.</summary>
public sealed class ShortcutFocusRulesTests
{
    [Fact]
    public void SpaceOnAButtonReachedWithTheKeyboardPressesIt()
    {
        var space = new KeyChord((int)VirtualKey.Space);

        Assert.True(ShortcutFocusRules.SpaceGoesToButton(space, keyboardFocusedButton: true));
        Assert.False(ShortcutFocusRules.SpaceGoesToButton(space, keyboardFocusedButton: false));
    }

    [Fact]
    public void OtherKeysAndModifiedSpaceStayShortcuts()
    {
        Assert.False(ShortcutFocusRules.SpaceGoesToButton(new KeyChord((int)VirtualKey.K), keyboardFocusedButton: true));
        Assert.False(ShortcutFocusRules.SpaceGoesToButton(new KeyChord((int)VirtualKey.Space, KeyModifiers.Shift), keyboardFocusedButton: true));
    }
}
