using System.Text;

namespace Rodaje.Packages.Tests.Fuzz;

/// <summary>
/// Mutaciones deterministas para los lectores de contenido (<c>rodaje.safezones</c>, <c>rodaje.textstyle</c>) y para el
/// índice firmado. Cada caso sale de <c>new Random(semilla + caso)</c>, así que un fallo se repite solo con su número: el
/// mensaje del fallo dice la semilla y el caso, y <see cref="Mutate"/> con ese <see cref="Random"/> da los mismos bytes.
/// </summary>
internal static class ContentReaderFuzz
{
    /// <summary>Semilla base de las pruebas de la tarea 78 (la de la tarea 25 es 20261011).</summary>
    public const int Seed = 20261012;

    /// <summary>Trozos que suelen romper lectores: números extremos, nulos, tipos cambiados, Unicode raro y escapes.</summary>
    private static readonly string[] Tokens =
    [
        "1e400", "-1e400", "1e-400", "-0", "0", "1", "-1", "0.99999999999", "1.0000001", "9007199254740993", "99999999999999999999",
        "2147483648", "-2147483649", "NaN", "Infinity", "null", "true", "false", "[]", "{}", "[[[[[[[[", "]]]]", "\"\"", "\" \"",
        "\"\\ud800\"", "\"\\udc00x\"", "\"\\ud83d\\ude00\"", "\"\\u202e\"", "\"\\u200f\"", "\"\\u0000\"", "\"\\u2028\"", "\"\\u200b\"",
        "\"ñandú 🎬\"", "\"#FFFFFF\"", "\"#ffffff00\"", "\"#GGGGGG\"", "\"a@1.0.0/b\"", "\"../x\"", "\"C:\\\\x\"",
        ",", ":", "\"", "\\", "/*", "*/", "//",
    ];

    /// <summary>Bytes que no son UTF-8 válido (sueltos, cortados, sustituto codificado, forma larga).</summary>
    private static readonly byte[][] BadUtf8 = [[0xFF], [0xC3, 0x28], [0xED, 0xA0, 0x80], [0xC0, 0xAF], [0xE2, 0x82], [0xF4, 0x90, 0x80, 0x80], [0xEF, 0xBB, 0xBF]];

    public static byte[] Mutate(Random random, byte[] original)
    {
        List<byte> data = [.. original];
        for (int k = random.Next(1, 4); k > 0 && data.Count > 0; k--)
        {
            int at = random.Next(data.Count);
            switch (random.Next(8))
            {
                case 0:
                    data[at] = (byte)random.Next(32, 127);
                    break;
                case 1:
                    data.RemoveAt(at);
                    break;
                case 2:
                    data.Insert(at, (byte)random.Next(32, 127));
                    break;
                case 3:
                    data.RemoveRange(at, data.Count - at);
                    break;
                case 4:
                    data.InsertRange(at, BadUtf8[random.Next(BadUtf8.Length)]);
                    break;
                case 5:
                    // Cambia un número o un texto por otra cosa: se busca el valor más cercano y se reemplaza entero.
                    (int start, int length) = ValueAt(data, at);
                    data.RemoveRange(start, length);
                    data.InsertRange(start, Encoding.UTF8.GetBytes(Tokens[random.Next(Tokens.Length)]));
                    break;
                case 6:
                    data.InsertRange(at, Encoding.UTF8.GetBytes(Tokens[random.Next(Tokens.Length)]));
                    break;
                default:
                    // Duplica un trozo (propiedades repetidas, zonas de más, anidamiento).
                    int count = Math.Min(random.Next(1, 200), data.Count - at);
                    data.InsertRange(at, data.GetRange(at, count));
                    break;
            }
        }

        return [.. data];
    }

    /// <summary>El valor JSON simple (número o texto) que empieza en <paramref name="at"/> o después.</summary>
    private static (int Start, int Length) ValueAt(List<byte> data, int at)
    {
        int start = at;
        while (start < data.Count && !(data[start] is (byte)'"' or (byte)'-' || char.IsAsciiDigit((char)data[start])))
        {
            start++;
        }

        if (start >= data.Count)
        {
            return (at, 0);
        }

        int end = start + 1;
        if (data[start] == '"')
        {
            while (end < data.Count && data[end] != '"')
            {
                end += data[end] == '\\' ? 2 : 1;
            }

            end = Math.Min(end + 1, data.Count);
        }
        else
        {
            while (end < data.Count && (char.IsAsciiDigit((char)data[end]) || data[end] is (byte)'.' or (byte)'e' or (byte)'E' or (byte)'-' or (byte)'+'))
            {
                end++;
            }
        }

        return (start, end - start);
    }

    /// <summary>Un texto ya limpio: sin controles, sin marcas de dirección y sin sustitutos sueltos.</summary>
    public static void AssertClean(string text, string what)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            Assert.False(char.IsControl(c), $"{what} trae el control U+{(int)c:X4}.");
            Assert.False(SafeText.IsBidiControl(c), $"{what} trae la marca de dirección U+{(int)c:X4}.");
            if (char.IsHighSurrogate(c))
            {
                Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), $"{what} trae un sustituto alto suelto.");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(c), $"{what} trae un sustituto bajo suelto.");
            }
        }
    }

    public static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json));

    public static string Show(byte[] data) =>
        data.Length > 400 ? Encoding.UTF8.GetString(data, 0, 400) + "…" : Encoding.UTF8.GetString(data);
}
