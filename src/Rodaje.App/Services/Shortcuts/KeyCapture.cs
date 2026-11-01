namespace Rodaje.App.Services.Shortcuts;

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
