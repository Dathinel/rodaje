namespace Rodaje.App.Services.Shortcuts;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8,
}

/// <summary>Una combinación de teclas: una tecla (código virtual de Windows) con sus modificadores.</summary>
/// <remarks>
/// Los códigos son los de <c>Windows.System.VirtualKey</c> (los VK_ de Win32), así que la ventana los convierte sin
/// tablas; esta clase no depende de WinUI y se prueba en la suite normal.
/// </remarks>
public readonly record struct KeyChord(int Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    /// <summary>Texto en español para mostrar: «Ctrl+Mayús+Z», «Espacio», «Supr».</summary>
    public override string ToString() => KeyNames.Format(this);

    /// <summary>Texto estable para guardar en archivos: «Ctrl+Shift+Z», «Space», «Delete».</summary>
    public string ToInvariantString() => KeyNames.FormatInvariant(this);

    /// <summary>Lee una combinación escrita en español o en el formato de archivo; falla si no la entiende.</summary>
    public static KeyChord Parse(string text) =>
        KeyNames.TryParse(text, out KeyChord chord) ? chord : throw new FormatException($"No se entiende la combinación de teclas «{text}».");

    public static bool TryParse(string? text, out KeyChord chord) => KeyNames.TryParse(text, out chord);

    /// <summary>Ctrl, Mayús, Alt o Windows solos: no forman una combinación.</summary>
    public static bool IsModifierKey(int key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);
}
