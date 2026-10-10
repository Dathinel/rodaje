namespace Rodaje.App.Services.Shortcuts;

/// <summary>Traduce el estado de las teclas modificadoras a <see cref="KeyModifiers"/>.</summary>
public static class ModifierKeys
{
    /// <remarks>
    /// AltGr llega como Ctrl izquierdo + Alt derecho: en un teclado español escribe «@», «#» o «€» y no es Ctrl+Alt,
    /// así que no cuenta ni como Ctrl ni como Alt. Con el Ctrl derecho apretado a la vez sí es Ctrl+Alt.
    /// </remarks>
    public static KeyModifiers From(bool leftControl, bool rightControl, bool shift, bool leftAlt, bool rightAlt, bool windows)
    {
        bool altGr = rightAlt && leftControl && !rightControl;
        bool control = rightControl || (leftControl && !altGr);
        bool alt = leftAlt || (rightAlt && !altGr);
        var modifiers = KeyModifiers.None;
        if (control)
        {
            modifiers |= KeyModifiers.Control;
        }

        if (shift)
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (alt)
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (windows)
        {
            modifiers |= KeyModifiers.Windows;
        }

        return modifiers;
    }
}
