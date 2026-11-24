namespace Rodaje.App.Services.Shortcuts;

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
