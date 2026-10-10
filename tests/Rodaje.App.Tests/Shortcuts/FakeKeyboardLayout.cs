using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

/// <summary>Distribuciones de teclado de tabla, con lo que Windows devuelve para cada una (MapVirtualKeyEx).</summary>
internal sealed class FakeKeyboardLayout(Dictionary<int, int> scanToVk, Dictionary<int, char> characters) : IKeyboardLayout
{
    /// <summary>Español de España (KBDSP): las teclas de signos tienen otros VK_OEM y otros caracteres que en EE. UU.</summary>
    public static FakeKeyboardLayout Spanish { get; } = new(
        WithLettersAndDigits(new()
        {
            [0x29] = 0xDC, // º
            [0x0C] = 0xDB, // '
            [0x0D] = 0xDD, // ¡
            [0x1A] = 0xBA, // ` (muerta)
            [0x1B] = 0xBB, // +
            [0x27] = 0xC0, // ñ
            [0x28] = 0xDE, // ´ (muerta)
            [0x2B] = 0xBF, // ç
            [0x33] = 0xBC, // ,
            [0x34] = 0xBE, // .
            [0x35] = 0xBD, // -
            [0x56] = 0xE2, // <
        }),
        new()
        {
            [0xDC] = 'º',
            [0xDB] = '\'',
            [0xDD] = '¡',
            [0xBA] = '`',
            [0xBB] = '+',
            [0xC0] = 'ñ',
            [0xDE] = '´',
            [0xBF] = 'ç',
            [0xBC] = ',',
            [0xBE] = '.',
            [0xBD] = '-',
            [0xE2] = '<',
        });

    public int VirtualKeyFromScanCode(int scanCode) => scanToVk.GetValueOrDefault(scanCode);

    public char? CharacterOf(int virtualKey) =>
        characters.TryGetValue(virtualKey, out char c) ? c : virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9' ? (char)virtualKey : null;

    private static Dictionary<int, int> WithLettersAndDigits(Dictionary<int, int> map)
    {
        const string top = "QWERTYUIOP";
        const string middle = "ASDFGHJKL";
        const string bottom = "ZXCVBNM";
        for (int i = 0; i < 9; i++)
        {
            map[0x02 + i] = '1' + i;
        }

        map[0x0B] = '0';
        for (int i = 0; i < top.Length; i++)
        {
            map[0x10 + i] = top[i];
        }

        for (int i = 0; i < middle.Length; i++)
        {
            map[0x1E + i] = middle[i];
        }

        for (int i = 0; i < bottom.Length; i++)
        {
            map[0x2C + i] = bottom[i];
        }

        return map;
    }
}
