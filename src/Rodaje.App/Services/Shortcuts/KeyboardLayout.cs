namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// La distribución de teclado activa: qué tecla virtual da cada tecla física y qué carácter lleva impreso. En la app la
/// da Windows (<c>MapVirtualKeyEx</c> con la distribución del hilo); en las pruebas, una tabla.
/// </summary>
public interface IKeyboardLayout
{
    /// <summary>Código virtual de la tecla física con ese código de exploración (con 0xE000 si es extendida), o 0.</summary>
    int VirtualKeyFromScanCode(int scanCode);

    /// <summary>El carácter que escribe la tecla sin modificadores, o <c>null</c> si no escribe ninguno.</summary>
    char? CharacterOf(int virtualKey);
}

/// <summary>Una tecla dibujada del teclado, en unidades de tecla (1 = una letra).</summary>
/// <param name="ScanCode">Código de exploración (0xE0xx si es extendida), con el que la distribución da su tecla virtual.</param>
/// <param name="VirtualKey">Tecla virtual fija (no depende de la distribución), o 0 para pedirla a la distribución.</param>
/// <param name="Label">Texto fijo («Tab», «Bloq Mayús»), o <c>null</c> para usar el nombre de la tecla.</param>
/// <param name="Modifier">Si es Ctrl, Mayús o Alt: al pulsarla en el dibujo se ven las combinaciones con ella.</param>
public sealed record KeyCap(
    double X,
    double Y,
    double Width,
    double Height,
    int ScanCode,
    int VirtualKey = 0,
    string? Label = null,
    KeyModifiers Modifier = KeyModifiers.None)
{
    /// <summary>No lanza acciones: modificadores, bloqueos y la tecla Windows.</summary>
    public bool IsInert => Modifier != KeyModifiers.None || VirtualKey is 0x14 or 0x5B or 0x5C or 0x5D or 0x90 or 0xA5;
}

/// <summary>Lo que muestra una tecla del dibujo: su tecla virtual, su nombre impreso y las acciones que lanza.</summary>
public sealed record KeyCapState(KeyCap Cap, int VirtualKey, string Label, IReadOnlyList<KeyBinding> Bindings)
{
    /// <summary>La acción que lanza con exactamente esos modificadores, o <c>null</c>.</summary>
    public KeyBinding? For(KeyModifiers modifiers) => Bindings.FirstOrDefault(b => b.Chord.Modifiers == modifiers);
}

/// <summary>Una combinación y la acción que la usa.</summary>
public sealed record KeyBinding(KeyChord Chord, ShortcutAction Action);

/// <summary>
/// El teclado español (ISO, 105 teclas) dibujado en unidades de tecla: bloque principal, flechas y bloque de
/// edición, y teclado numérico. Las teclas de escribir se identifican por su posición física (código de exploración),
/// así cada una muestra el carácter de la distribución activa.
/// </summary>
public static class KeyboardGeometry
{
    public const double Width = 22.75;
    public const double Height = 6.5;

    private const double MainY = 1.5;
    private const double NavX = 15.25;
    private const double PadX = 18.75;

    public static IReadOnlyList<KeyCap> SpanishIso { get; } = BuildSpanishIso();

    /// <summary>Las teclas con sus acciones en el estilo actual, con los nombres de <paramref name="layout"/>.</summary>
    /// <param name="registry">Acciones y teclas.</param>
    /// <param name="layout">Distribución activa, o <c>null</c> para la estadounidense.</param>
    /// <param name="caps">Teclas del dibujo; por omisión, <see cref="SpanishIso"/>.</param>
    /// <param name="include">Solo las acciones que cumplan esto (por ejemplo, las esenciales); por omisión, todas.</param>
    public static IReadOnlyList<KeyCapState> Map(
        ShortcutRegistry registry,
        IKeyboardLayout? layout,
        IReadOnlyList<KeyCap>? caps = null,
        Func<ShortcutAction, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var byKey = new Dictionary<int, List<KeyBinding>>();
        foreach (ShortcutAction action in registry.Actions)
        {
            if (include is not null && !include(action))
            {
                continue;
            }

            foreach (KeyChord chord in registry.GetKeys(action.Id))
            {
                if (!byKey.TryGetValue(chord.Key, out List<KeyBinding>? list))
                {
                    byKey[chord.Key] = list = [];
                }

                // La primera registrada gana, como en ShortcutRegistry.FindAction.
                if (!list.Any(b => b.Chord == chord))
                {
                    list.Add(new KeyBinding(chord, action));
                }
            }
        }

        var states = new List<KeyCapState>();
        foreach (KeyCap cap in caps ?? SpanishIso)
        {
            int vk = VirtualKeyOf(cap, layout);
            string label = cap.Label ?? (vk == 0 ? "" : KeyNames.KeyName(vk, layout));
            IReadOnlyList<KeyBinding> bindings = !cap.IsInert && vk != 0 && byKey.TryGetValue(vk, out List<KeyBinding>? found)
                ? [.. found.OrderBy(b => BitCount(b.Chord.Modifiers)).ThenBy(b => (int)b.Chord.Modifiers)]
                : [];
            states.Add(new KeyCapState(cap, vk, label, bindings));
        }

        return states;
    }

    /// <summary>La tecla virtual de una tecla dibujada en la distribución dada (sin ella, la estadounidense).</summary>
    public static int VirtualKeyOf(KeyCap cap, IKeyboardLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(cap);
        if (cap.VirtualKey != 0)
        {
            return cap.VirtualKey;
        }

        int vk = layout?.VirtualKeyFromScanCode(cap.ScanCode) ?? 0;
        return vk != 0 ? vk : UsVirtualKey(cap.ScanCode);
    }

    private static int BitCount(KeyModifiers modifiers) => System.Numerics.BitOperations.PopCount((uint)modifiers);

    /// <summary>Tecla virtual de cada tecla de escribir en la distribución estadounidense (la de los nombres estables).</summary>
    private static int UsVirtualKey(int scanCode) => scanCode switch
    {
        >= 0x02 and <= 0x0A => '1' + scanCode - 0x02,
        0x0B => '0',
        0x0C => 0xBD,
        0x0D => 0xBB,
        0x10 => 'Q', 0x11 => 'W', 0x12 => 'E', 0x13 => 'R', 0x14 => 'T',
        0x15 => 'Y', 0x16 => 'U', 0x17 => 'I', 0x18 => 'O', 0x19 => 'P',
        0x1A => 0xDB,
        0x1B => 0xDD,
        0x1E => 'A', 0x1F => 'S', 0x20 => 'D', 0x21 => 'F', 0x22 => 'G',
        0x23 => 'H', 0x24 => 'J', 0x25 => 'K', 0x26 => 'L',
        0x27 => 0xBA,
        0x28 => 0xDE,
        0x29 => 0xC0,
        0x2B => 0xDC,
        0x2C => 'Z', 0x2D => 'X', 0x2E => 'C', 0x2F => 'V', 0x30 => 'B', 0x31 => 'N', 0x32 => 'M',
        0x33 => 0xBC,
        0x34 => 0xBE,
        0x35 => 0xBF,
        0x56 => 0xE2,
        _ => 0,
    };

    private static List<KeyCap> BuildSpanishIso()
    {
        var caps = new List<KeyCap>
        {
            new(0, 0, 1, 1, 0x01, 0x1B),
        };

        // Fila de funciones: F1-F4, F5-F8, F9-F12 en bloques separados.
        int[] fScans = [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58];
        for (int i = 0; i < 12; i++)
        {
            double x = 2 + i + (i / 4 * 0.5);
            caps.Add(new KeyCap(x, 0, 1, 1, fScans[i], 0x70 + i));
        }

        double y = MainY;
        caps.Add(new KeyCap(0, y, 1, 1, 0x29));
        for (int i = 0; i < 12; i++)
        {
            caps.Add(new KeyCap(1 + i, y, 1, 1, 0x02 + i));
        }

        caps.Add(new KeyCap(13, y, 2, 1, 0x0E, 0x08));

        y++;
        caps.Add(new KeyCap(0, y, 1.5, 1, 0x0F, 0x09, "Tab"));
        for (int i = 0; i < 12; i++)
        {
            caps.Add(new KeyCap(1.5 + i, y, 1, 1, 0x10 + i));
        }

        // Intro ISO: se dibuja como una tecla alta en la columna de la derecha.
        caps.Add(new KeyCap(13.75, y, 1.25, 2, 0x1C, 0x0D));

        y++;
        caps.Add(new KeyCap(0, y, 1.75, 1, 0x3A, 0x14, "Bloq Mayús"));
        for (int i = 0; i < 11; i++)
        {
            caps.Add(new KeyCap(1.75 + i, y, 1, 1, 0x1E + i));
        }

        caps.Add(new KeyCap(12.75, y, 1, 1, 0x2B));

        y++;
        caps.Add(new KeyCap(0, y, 1.25, 1, 0x2A, 0xA0, "Mayús", KeyModifiers.Shift));
        caps.Add(new KeyCap(1.25, y, 1, 1, 0x56));
        for (int i = 0; i < 10; i++)
        {
            caps.Add(new KeyCap(2.25 + i, y, 1, 1, 0x2C + i));
        }

        caps.Add(new KeyCap(12.25, y, 2.75, 1, 0x36, 0xA1, "Mayús", KeyModifiers.Shift));

        y++;
        caps.Add(new KeyCap(0, y, 1.25, 1, 0x1D, 0xA2, "Ctrl", KeyModifiers.Control));
        caps.Add(new KeyCap(1.25, y, 1.25, 1, 0xE05B, 0x5B, "Win"));
        caps.Add(new KeyCap(2.5, y, 1.25, 1, 0x38, 0xA4, "Alt", KeyModifiers.Alt));
        caps.Add(new KeyCap(3.75, y, 6.25, 1, 0x39, 0x20));
        // Alt Gr equivale a Ctrl+Alt: no es un modificador propio en el dibujo.
        caps.Add(new KeyCap(10, y, 1.25, 1, 0xE038, 0xA5, "Alt Gr"));
        caps.Add(new KeyCap(11.25, y, 1.25, 1, 0xE05C, 0x5C, "Win"));
        caps.Add(new KeyCap(12.5, y, 1.25, 1, 0xE05D, 0x5D, "Menú"));
        caps.Add(new KeyCap(13.75, y, 1.25, 1, 0xE01D, 0xA3, "Ctrl", KeyModifiers.Control));

        // Bloque de edición y flechas.
        caps.Add(new KeyCap(NavX, MainY, 1, 1, 0xE052, 0x2D));
        caps.Add(new KeyCap(NavX + 1, MainY, 1, 1, 0xE047, 0x24));
        caps.Add(new KeyCap(NavX + 2, MainY, 1, 1, 0xE049, 0x21));
        caps.Add(new KeyCap(NavX, MainY + 1, 1, 1, 0xE053, 0x2E));
        caps.Add(new KeyCap(NavX + 1, MainY + 1, 1, 1, 0xE04F, 0x23));
        caps.Add(new KeyCap(NavX + 2, MainY + 1, 1, 1, 0xE051, 0x22));
        caps.Add(new KeyCap(NavX + 1, MainY + 3, 1, 1, 0xE048, 0x26));
        caps.Add(new KeyCap(NavX, MainY + 4, 1, 1, 0xE04B, 0x25));
        caps.Add(new KeyCap(NavX + 1, MainY + 4, 1, 1, 0xE050, 0x28));
        caps.Add(new KeyCap(NavX + 2, MainY + 4, 1, 1, 0xE04D, 0x27));

        // Teclado numérico (con Bloq Num activo).
        caps.Add(new KeyCap(PadX, MainY, 1, 1, 0x45, 0x90, "Bloq Num"));
        caps.Add(new KeyCap(PadX + 1, MainY, 1, 1, 0xE035, 0x6F, "/"));
        caps.Add(new KeyCap(PadX + 2, MainY, 1, 1, 0x37, 0x6A, "*"));
        caps.Add(new KeyCap(PadX + 3, MainY, 1, 1, 0x4A, 0x6D, "-"));
        caps.Add(new KeyCap(PadX, MainY + 1, 1, 1, 0x47, 0x67, "7"));
        caps.Add(new KeyCap(PadX + 1, MainY + 1, 1, 1, 0x48, 0x68, "8"));
        caps.Add(new KeyCap(PadX + 2, MainY + 1, 1, 1, 0x49, 0x69, "9"));
        caps.Add(new KeyCap(PadX + 3, MainY + 1, 1, 2, 0x4E, 0x6B, "+"));
        caps.Add(new KeyCap(PadX, MainY + 2, 1, 1, 0x4B, 0x64, "4"));
        caps.Add(new KeyCap(PadX + 1, MainY + 2, 1, 1, 0x4C, 0x65, "5"));
        caps.Add(new KeyCap(PadX + 2, MainY + 2, 1, 1, 0x4D, 0x66, "6"));
        caps.Add(new KeyCap(PadX, MainY + 3, 1, 1, 0x4F, 0x61, "1"));
        caps.Add(new KeyCap(PadX + 1, MainY + 3, 1, 1, 0x50, 0x62, "2"));
        caps.Add(new KeyCap(PadX + 2, MainY + 3, 1, 1, 0x51, 0x63, "3"));
        caps.Add(new KeyCap(PadX + 3, MainY + 3, 1, 2, 0xE01C, 0x0D, "Intro"));
        caps.Add(new KeyCap(PadX, MainY + 4, 2, 1, 0x52, 0x60, "0"));
        caps.Add(new KeyCap(PadX + 2, MainY + 4, 1, 1, 0x53, 0x6E, "."));
        return caps;
    }
}
