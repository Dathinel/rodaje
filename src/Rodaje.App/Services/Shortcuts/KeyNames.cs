using System.Globalization;
using System.Text;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>Nombres de las teclas en español (para mostrar) y en un formato estable (para guardar), y su lectura.</summary>
/// <remarks>
/// Las teclas de signos (VK_OEM_*) cambian de carácter según la distribución: la que en un teclado estadounidense lleva
/// «[» en uno español lleva «'». Para guardar y para el catálogo se nombran siempre por la posición estadounidense
/// (formato estable); para mostrar se usa la distribución activa (<see cref="DisplayLayout"/>), así el texto coincide con
/// lo que la persona ve impreso en su teclado.
/// </remarks>
public static class KeyNames
{
    private static readonly NamedKey[] Named =
    [
        new NamedKey(0x08, "Retroceso", "Backspace"),
        new NamedKey(0x09, "Tab", "Tab"),
        new NamedKey(0x0D, "Intro", "Enter", "Entrar"),
        new NamedKey(0x13, "Pausa", "Pause"),
        new NamedKey(0x1B, "Esc", "Escape"),
        new NamedKey(0x20, "Espacio", "Space"),
        new NamedKey(0x21, "RePág", "PageUp", "RePag"),
        new NamedKey(0x22, "AvPág", "PageDown", "AvPag"),
        new NamedKey(0x23, "Fin", "End"),
        new NamedKey(0x24, "Inicio", "Home"),
        new NamedKey(0x25, "←", "Left", "Izquierda", "Flecha izquierda"),
        new NamedKey(0x26, "↑", "Up", "Arriba", "Flecha arriba"),
        new NamedKey(0x27, "→", "Right", "Derecha", "Flecha derecha"),
        new NamedKey(0x28, "↓", "Down", "Abajo", "Flecha abajo"),
        new NamedKey(0x2D, "Insert", "Insert", "Ins"),
        new NamedKey(0x2E, "Supr", "Delete", "Del", "Suprimir"),
        new NamedKey(0x6A, "Num *", "Multiply"),
        new NamedKey(0x6B, "Num +", "Add"),
        new NamedKey(0x6D, "Num -", "Subtract"),
        new NamedKey(0x6E, "Num .", "Decimal"),
        new NamedKey(0x6F, "Num /", "Divide"),
        new NamedKey(0xBA, ";", "OemSemicolon"),
        new NamedKey(0xBB, "+", "OemPlus"),
        new NamedKey(0xBC, ",", "OemComma"),
        new NamedKey(0xBD, "-", "OemMinus"),
        new NamedKey(0xBE, ".", "OemPeriod"),
        new NamedKey(0xBF, "/", "OemSlash"),
        new NamedKey(0xC0, "`", "OemTilde"),
        new NamedKey(0xDB, "[", "OemOpenBracket"),
        new NamedKey(0xDC, "\\", "OemBackslash"),
        new NamedKey(0xDD, "]", "OemCloseBracket"),
        new NamedKey(0xDE, "'", "OemQuote"),
        new NamedKey(0xE2, "<", "Oem102"),
    ];

    private static readonly Dictionary<int, NamedKey> ByKey = BuildByKey();
    private static readonly Dictionary<string, int> ByName = BuildByName();

    private static readonly (KeyModifiers Modifier, string Display, string Invariant, string[] Aliases)[] ModifierNames =
    [
        (KeyModifiers.Control, "Ctrl", "Ctrl", ["Control", "Ctl"]),
        (KeyModifiers.Shift, "Mayús", "Shift", ["Mayus", "Mayúsculas", "Mayusculas"]),
        (KeyModifiers.Alt, "Alt", "Alt", []),
        (KeyModifiers.Windows, "Win", "Win", ["Windows"]),
    ];

    /// <summary>
    /// Distribución con la que se muestran las teclas de signos; <c>null</c> las nombra por su posición estadounidense.
    /// La ventana pone aquí la distribución activa de Windows al arrancar.
    /// </summary>
    public static IKeyboardLayout? DisplayLayout { get; set; }

    /// <summary>«Ctrl+Mayús+Z», con los signos de <see cref="DisplayLayout"/>.</summary>
    public static string Format(KeyChord chord) => Format(chord, DisplayLayout);

    /// <summary>«Ctrl+Mayús+Z», con los signos de <paramref name="layout"/>.</summary>
    public static string Format(KeyChord chord, IKeyboardLayout? layout) => Compose(chord, invariant: false, layout);

    /// <summary>«Ctrl+Shift+Z»: no depende del idioma ni de la distribución, para los archivos.</summary>
    public static string FormatInvariant(KeyChord chord) => Compose(chord, invariant: true, layout: null);

    /// <summary>Nombre de la tecla sola, en español, con los signos de <see cref="DisplayLayout"/>.</summary>
    public static string KeyName(int key) => KeyName(key, DisplayLayout);

    /// <summary>Nombre de la tecla sola, en español, con los signos de <paramref name="layout"/>.</summary>
    public static string KeyName(int key, IKeyboardLayout? layout) => Name(key, invariant: false, layout);

    /// <summary>Teclas de signos: su carácter depende de la distribución del teclado.</summary>
    public static bool IsLayoutDependent(int key) => key is (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDF) or 0xE1 or 0xE2;

    /// <summary>Los modificadores solos, como se ven mientras se captura una tecla: «Ctrl+Mayús+».</summary>
    public static string FormatModifiers(KeyModifiers modifiers)
    {
        var text = new StringBuilder();
        foreach ((KeyModifiers mod, string display, _, _) in ModifierNames)
        {
            if (modifiers.HasFlag(mod))
            {
                text.Append(display).Append('+');
            }
        }

        return text.ToString();
    }

    /// <summary>Lee «Ctrl+Mayús+Z», «ctrl+shift+z», «Alt+[», «Ctrl++» o «Supr» (signos en posición estadounidense).</summary>
    public static bool TryParse(string? text, out KeyChord chord) => TryParse(text, layout: null, out chord);

    /// <summary>
    /// Lee una combinación escrita como se ve en pantalla: un signo de un solo carácter se busca primero en
    /// <paramref name="layout"/> («Ñ» o «'» en un teclado español) y después en la posición estadounidense.
    /// </summary>
    public static bool TryParse(string? text, IKeyboardLayout? layout, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string rest = text.Trim();
        var modifiers = KeyModifiers.None;
        while (TryStripModifier(ref rest, out KeyModifiers modifier))
        {
            modifiers |= modifier;
        }

        rest = rest.Trim();
        int? key = ParseKey(rest, layout);
        if (key is not { } value || KeyChord.IsModifierKey(value))
        {
            return false;
        }

        chord = new KeyChord(value, modifiers);
        return true;
    }

    private static bool TryStripModifier(ref string text, out KeyModifiers modifier)
    {
        int plus = text.IndexOf('+', StringComparison.Ordinal);
        // «+» al principio es la tecla, no un separador.
        if (plus <= 0)
        {
            modifier = KeyModifiers.None;
            return false;
        }

        string token = text[..plus].Trim();
        foreach ((KeyModifiers mod, string display, string invariant, string[] aliases) in ModifierNames)
        {
            if (Same(token, display) || Same(token, invariant) || aliases.Any(a => Same(token, a)))
            {
                modifier = mod;
                text = text[(plus + 1)..];
                return true;
            }
        }

        modifier = KeyModifiers.None;
        return false;
    }

    private static int? ParseKey(string token, IKeyboardLayout? layout)
    {
        if (token.Length == 0)
        {
            return null;
        }

        if (token.Length == 1 && char.ToUpperInvariant(token[0]) is var c && (c is >= 'A' and <= 'Z' || c is >= '0' and <= '9'))
        {
            return c;
        }

        if (token.Length == 1 && layout is not null)
        {
            char wanted = char.ToUpperInvariant(token[0]);
            for (int vk = 0xBA; vk <= 0xE2; vk++)
            {
                if (IsLayoutDependent(vk) && LayoutCharacter(vk, layout) == wanted)
                {
                    return vk;
                }
            }
        }

        if (ByName.TryGetValue(Fold(token), out int named))
        {
            return named;
        }

        if ((token[0] is 'F' or 'f') && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
        {
            return 0x70 + f - 1;
        }

        string compact = Fold(token).Replace(" ", "", StringComparison.Ordinal);
        foreach (string prefix in (string[])["num", "numpad"])
        {
            if (compact.StartsWith(prefix, StringComparison.Ordinal)
                && compact.Length == prefix.Length + 1
                && compact[^1] is >= '0' and <= '9')
            {
                return 0x60 + (compact[^1] - '0');
            }
        }

        return null;
    }

    private static string Compose(KeyChord chord, bool invariant, IKeyboardLayout? layout)
    {
        var text = new StringBuilder();
        foreach ((KeyModifiers mod, string display, string inv, _) in ModifierNames)
        {
            if (chord.Modifiers.HasFlag(mod))
            {
                text.Append(invariant ? inv : display).Append('+');
            }
        }

        return text.Append(Name(chord.Key, invariant, layout)).ToString();
    }

    private static string Name(int key, bool invariant, IKeyboardLayout? layout)
    {
        if (!invariant && layout is not null && IsLayoutDependent(key) && LayoutCharacter(key, layout) is { } printed)
        {
            return printed.ToString();
        }

        if (key is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)key).ToString();
        }

        if (key is >= 0x60 and <= 0x69)
        {
            return (invariant ? "NumPad" : "Num ") + (key - 0x60).ToString(CultureInfo.InvariantCulture);
        }

        if (key is >= 0x70 and <= 0x87)
        {
            return "F" + (key - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
        }

        if (ByKey.TryGetValue(key, out NamedKey? name))
        {
            return invariant ? name.Invariant : name.Display;
        }

        return "VK" + key.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>El carácter impreso, en mayúscula, o <c>null</c> si la tecla no escribe uno visible.</summary>
    private static char? LayoutCharacter(int key, IKeyboardLayout layout) =>
        layout.CharacterOf(key) is { } c && !char.IsControl(c) && !char.IsWhiteSpace(c) ? char.ToUpperInvariant(c) : null;

    private static Dictionary<int, NamedKey> BuildByKey()
    {
        var map = new Dictionary<int, NamedKey>();
        foreach (NamedKey name in Named)
        {
            map[name.Key] = name;
        }

        return map;
    }

    private static Dictionary<string, int> BuildByName()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (NamedKey name in Named)
        {
            map.TryAdd(Fold(name.Display), name.Key);
            map.TryAdd(Fold(name.Invariant), name.Key);
            foreach (string alias in name.Aliases)
            {
                map.TryAdd(Fold(alias), name.Key);
            }
        }

        return map;
    }

    private static bool Same(string a, string b) => string.Equals(Fold(a), Fold(b), StringComparison.Ordinal);

    /// <summary>Minúsculas y sin tildes, para comparar nombres escritos a mano y para buscar.</summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var folded = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            folded.Append(char.ToLowerInvariant(ch) switch
            {
                'á' or 'à' or 'ä' or 'â' => 'a',
                'é' or 'è' or 'ë' or 'ê' => 'e',
                'í' or 'ì' or 'ï' or 'î' => 'i',
                'ó' or 'ò' or 'ö' or 'ô' => 'o',
                'ú' or 'ù' or 'ü' or 'û' => 'u',
                'ñ' => 'n',
                var other => other,
            });
        }

        return folded.ToString();
    }

    private sealed record NamedKey(int Key, string Display, string Invariant, params string[] Aliases);
}
