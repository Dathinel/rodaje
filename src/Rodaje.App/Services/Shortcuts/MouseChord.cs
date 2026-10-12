namespace Rodaje.App.Services.Shortcuts;

/// <summary>Las partes del ratón que pueden lanzar algo. La rueda y su inclinación cuentan como un eje cada una.</summary>
public enum MouseInput
{
    Left,
    Right,
    Middle,

    /// <summary>Botón lateral de atrás.</summary>
    X1,

    /// <summary>Botón lateral de adelante.</summary>
    X2,

    /// <summary>Girar la rueda: arriba y abajo.</summary>
    Wheel,

    /// <summary>Inclinar la rueda a la izquierda o a la derecha (o el desplazamiento horizontal del panel táctil).</summary>
    Tilt,
}

/// <summary>Dónde está el puntero: cada zona tiene su propio mapa del ratón.</summary>
public enum MouseContext
{
    Timeline,
    Viewer,
    Library,
}

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

/// <summary>Nombres en español de las partes del ratón y de las zonas.</summary>
public static class MouseNames
{
    public static string Display(MouseInput input) => input switch
    {
        MouseInput.Left => "Clic izquierdo",
        MouseInput.Right => "Clic derecho",
        MouseInput.Middle => "Botón central",
        MouseInput.X1 => "Lateral atrás",
        MouseInput.X2 => "Lateral adelante",
        MouseInput.Wheel => "Rueda",
        _ => "Inclinar rueda",
    };

    /// <summary>Cómo se usa: «arriba / abajo», «clic o arrastrar».</summary>
    public static string Hint(MouseInput input) => input switch
    {
        MouseInput.Wheel => "girar arriba o abajo",
        MouseInput.Tilt => "inclinar a izquierda o derecha",
        MouseInput.Middle => "clic o arrastrar",
        MouseInput.X1 or MouseInput.X2 => "clic (el pulgar)",
        _ => "clic o arrastrar",
    };

    public static string Display(MouseContext context) => context switch
    {
        MouseContext.Viewer => "Visor",
        MouseContext.Library => "Biblioteca",
        _ => "Línea de tiempo",
    };

    public static string Invariant(MouseContext context) => context switch
    {
        MouseContext.Viewer => "viewer",
        MouseContext.Library => "library",
        _ => "timeline",
    };

    public static bool TryParseContext(string? text, out MouseContext context)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "timeline":
                context = MouseContext.Timeline;
                return true;
            case "viewer":
                context = MouseContext.Viewer;
                return true;
            case "library":
                context = MouseContext.Library;
                return true;
            default:
                context = MouseContext.Timeline;
                return false;
        }
    }

    internal static string FormatInvariantModifiers(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) ? "Ctrl+" : "")
        + (modifiers.HasFlag(KeyModifiers.Shift) ? "Shift+" : "")
        + (modifiers.HasFlag(KeyModifiers.Alt) ? "Alt+" : "");
}
