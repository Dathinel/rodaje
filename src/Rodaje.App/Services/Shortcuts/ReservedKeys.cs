namespace Rodaje.App.Services.Shortcuts;

/// <summary>Combinaciones que Windows o la navegación con teclado ya usan y que no se pueden asignar.</summary>
public static class ReservedKeys
{
    /// <summary>Por qué no se puede usar la combinación, en español sencillo, o <c>null</c> si se puede.</summary>
    public static string? Reason(KeyChord chord)
    {
        KeyModifiers mods = chord.Modifiers;
        if (mods.HasFlag(KeyModifiers.Windows) || chord.Key is 0x5B or 0x5C)
        {
            return "Las combinaciones con la tecla Windows son del sistema.";
        }

        return (chord.Key, mods) switch
        {
            (0x73, KeyModifiers.Alt) => "Alt+F4 cierra la ventana.",
            (0x09, KeyModifiers.Alt) or (0x09, KeyModifiers.Alt | KeyModifiers.Shift) => "Alt+Tab cambia de ventana.",
            (0x09, KeyModifiers.None) or (0x09, KeyModifiers.Shift) => "Tab mueve el foco entre botones; se deja libre para poder usar Rodaje sin ratón.",
            (0x1B, KeyModifiers.Control) or (0x1B, KeyModifiers.Alt) => "Windows usa esa combinación para el menú Inicio y para cambiar de ventana.",
            (0x1B, KeyModifiers.Control | KeyModifiers.Shift) => "Ctrl+Mayús+Esc abre el Administrador de tareas.",
            (0x2E, KeyModifiers.Control | KeyModifiers.Alt) => "Ctrl+Alt+Supr es de Windows.",
            (0x20, KeyModifiers.Alt) => "Alt+Espacio abre el menú de la ventana.",
            (0x79, KeyModifiers.Shift) or (0x5D, _) => "Abre el menú contextual del control con el foco.",
            (0x2C, _) => "Impr Pant es de Windows (capturas de pantalla).",
            (0x14 or 0x90 or 0x91, _) => "Las teclas de bloqueo cambian el estado del teclado.",
            (0 or 0xE5 or 0xE7 or 0xFF, _) => "No se reconoce esa tecla.",
            _ => null,
        };
    }

    public static bool IsReserved(KeyChord chord) => Reason(chord) is not null;

    /// <summary>
    /// Una combinación que se puede usar pero conviene evitar, o <c>null</c>. Ctrl+Alt es lo mismo que AltGr en los
    /// teclados español y latinoamericano: con letras o signos puede tapar «@», «#», «€» o «\» al escribir.
    /// </summary>
    public static string? Warning(KeyChord chord) =>
        (chord.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == (KeyModifiers.Control | KeyModifiers.Alt)
        && chord.Key is (>= 0x30 and <= 0x5A) or (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xE2)
            ? "Ctrl+Alt es AltGr en los teclados en español y latinoamericanos: en un campo de texto puede impedir escribir «@», «#» o «€»."
            : null;
}
