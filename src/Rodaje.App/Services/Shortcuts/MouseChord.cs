namespace Rodaje.App.Services.Shortcuts;

/// <summary>Un botón o eje del ratón con los modificadores apretados.</summary>
public readonly record struct MouseChord(MouseInput Input, KeyModifiers Modifiers = KeyModifiers.None)
{
    /// <summary>Las combinaciones de modificadores que muestra la página, en este orden.</summary>
    public static IReadOnlyList<KeyModifiers> ModifierLayers { get; } =
    [
        KeyModifiers.None,
        KeyModifiers.Control,
        KeyModifiers.Shift,
        KeyModifiers.Alt,
        KeyModifiers.Control | KeyModifiers.Shift,
        KeyModifiers.Control | KeyModifiers.Alt,
        KeyModifiers.Shift | KeyModifiers.Alt,
        KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt,
    ];

    public bool IsAxis => Input is MouseInput.Wheel or MouseInput.Tilt;

    /// <summary>«Ctrl+Rueda», «Botón lateral atrás».</summary>
    public override string ToString() => KeyNames.FormatModifiers(Modifiers) + MouseNames.Display(Input);

    /// <summary>«Ctrl+Wheel», «X1»: para guardar en archivos.</summary>
    public string ToInvariantString() => MouseNames.FormatInvariantModifiers(Modifiers) + Input;

    public static bool TryParse(string? text, out MouseChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Enum.TryParse(parts[^1], ignoreCase: true, out MouseInput input) || !Enum.IsDefined(input))
        {
            return false;
        }

        var modifiers = KeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= KeyModifiers.Control;
                    break;
                case "shift" or "mayús" or "mayus":
                    modifiers |= KeyModifiers.Shift;
                    break;
                case "alt":
                    modifiers |= KeyModifiers.Alt;
                    break;
                default:
                    return false;
            }
        }

        chord = new MouseChord(input, modifiers);
        return true;
    }
}
