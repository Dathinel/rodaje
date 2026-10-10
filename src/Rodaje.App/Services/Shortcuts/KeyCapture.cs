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
}

public enum CaptureStatus
{
    /// <summary>Solo hay modificadores apretados: falta la tecla.</summary>
    Waiting,

    /// <summary>Esc sin modificadores: se deja todo como estaba.</summary>
    Cancelled,

    /// <summary>La combinación está reservada; se sigue esperando otra.</summary>
    Rejected,

    Accepted,
}

/// <summary>Qué hacer con una tecla durante la captura y el texto que se le muestra a la persona.</summary>
public readonly record struct CaptureResult(CaptureStatus Status, KeyChord Chord, string Message);

/// <summary>
/// La captura de una combinación nueva en la página de atajos: recibe cada tecla que baja y sube (con los modificadores
/// que hay apretados) y decide si ya hay una combinación, si se canceló o si está reservada. No depende de WinUI.
/// </summary>
public sealed class KeyCapture
{
    public const string Prompt = "Pulsa la nueva combinación… Esc cancela";

    private bool _rejected;

    /// <summary>Lo que se muestra en este momento.</summary>
    public string Message { get; private set; } = Prompt;

    public CaptureResult KeyDown(int key, KeyModifiers modifiers)
    {
        modifiers |= ModifierOf(key);
        if (modifiers.HasFlag(KeyModifiers.Windows))
        {
            // La tecla Windows sola ya abre Inicio: se avisa en cuanto baja, sin esperar a la otra tecla.
            return Show(CaptureStatus.Rejected, new KeyChord(key, modifiers), $"{ReservedKeys.Reason(new KeyChord(key, modifiers))} Prueba otra combinación; Esc cancela.");
        }

        if (KeyChord.IsModifierKey(key))
        {
            return Show(CaptureStatus.Waiting, default, Holding(modifiers));
        }

        if (key == 0x1B && modifiers == KeyModifiers.None)
        {
            return Show(CaptureStatus.Cancelled, default, "");
        }

        var chord = new KeyChord(key, modifiers);
        if (ReservedKeys.Reason(chord) is { } reason)
        {
            return Show(CaptureStatus.Rejected, chord, $"{reason} Prueba otra combinación; Esc cancela.");
        }

        return Show(CaptureStatus.Accepted, chord, "");
    }

    /// <summary>
    /// Al soltar un modificador se actualiza lo que se ve («Ctrl+…»). El motivo de un rechazo se queda a la vista hasta
    /// la siguiente tecla.
    /// </summary>
    public CaptureResult KeyUp(int key, KeyModifiers modifiers)
    {
        if (_rejected || !KeyChord.IsModifierKey(key))
        {
            return new CaptureResult(CaptureStatus.Waiting, default, Message);
        }

        return Show(CaptureStatus.Waiting, default, Holding(modifiers & ~ModifierOf(key)));
    }

    /// <summary>El modificador que es la propia tecla (Windows avisa del cambio después del evento).</summary>
    public static KeyModifiers ModifierOf(int key) => key switch
    {
        0x10 or 0xA0 or 0xA1 => KeyModifiers.Shift,
        0x11 or 0xA2 or 0xA3 => KeyModifiers.Control,
        0x12 or 0xA4 or 0xA5 => KeyModifiers.Alt,
        0x5B or 0x5C => KeyModifiers.Windows,
        _ => KeyModifiers.None,
    };

    private static string Holding(KeyModifiers modifiers) =>
        modifiers == KeyModifiers.None ? Prompt : KeyNames.FormatModifiers(modifiers) + "… (Esc cancela)";

    private CaptureResult Show(CaptureStatus status, KeyChord chord, string message)
    {
        _rejected = status == CaptureStatus.Rejected;
        if (status is CaptureStatus.Waiting or CaptureStatus.Rejected)
        {
            Message = message;
        }

        return new CaptureResult(status, chord, message);
    }
}
