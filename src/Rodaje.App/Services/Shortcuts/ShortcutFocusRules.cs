using Windows.System;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>Qué teclas se quedan los controles con el foco antes de que la ventana las trate como atajos.</summary>
public static class ShortcutFocusRules
{
    /// <summary>
    /// Un botón al que se llegó con el teclado (el diamante de keyframe del inspector) se pulsa con Espacio; no es «reproducir».
    /// Con el foco por un clic, Espacio sigue reproduciendo: tras pulsar «Cortar» no se quiere volver a cortar.
    /// </summary>
    public static bool SpaceGoesToButton(KeyChord chord, bool keyboardFocusedButton) =>
        keyboardFocusedButton && chord.Modifiers == KeyModifiers.None && chord.Key == (int)VirtualKey.Space;
}
