namespace Rodaje.Packages;

/// <summary>
/// Quitar tildes y ordenar alfabéticamente en español sin datos de ICU. La app, el lanzador y los módulos publican con
/// <c>InvariantGlobalization</c>: ahí <see cref="string.Normalize(System.Text.NormalizationForm)"/> devuelve el texto tal
/// cual (no separa las tildes) y comparar con la cultura actual es comparar por código, así que «Árbol»
/// y «Ñandú» quedaban detrás de «Zeta» y «Música» se volvía «m-sica». Las pruebas normales (con ICU) no lo ven.
/// </summary>
public static class SpanishText
{
    private const char First = 'À';
    private const char Last = 'ſ';

    // Letras latinas con diacríticos (Latin-1 y Latin Extended-A) y su letra base. Una prueba con ICU exige que coincida
    // con la descomposición canónica (FormD) en todo el rango; Ø, Đ, Ħ, Ł, ı y ſ, que no se descomponen, se pliegan igual.
    private static readonly (string Letters, char Base)[] Groups =
    [
        ("ÀÁÂÃÄÅĀĂĄ", 'A'), ("àáâãäåāăą", 'a'), ("ÇĆĈĊČ", 'C'), ("çćĉċč", 'c'), ("ĎĐ", 'D'), ("ďđ", 'd'),
        ("ÈÉÊËĒĔĖĘĚ", 'E'), ("èéêëēĕėęě", 'e'), ("ĜĞĠĢ", 'G'), ("ĝğġģ", 'g'), ("ĤĦ", 'H'), ("ĥħ", 'h'),
        ("ÌÍÎÏĨĪĬĮİ", 'I'), ("ìíîïĩīĭįı", 'i'), ("Ĵ", 'J'), ("ĵ", 'j'), ("Ķ", 'K'), ("ķ", 'k'), ("ĹĻĽĿŁ", 'L'), ("ĺļľŀł", 'l'),
        ("ÑŃŅŇ", 'N'), ("ñńņň", 'n'), ("ÒÓÔÕÖØŌŎŐ", 'O'), ("òóôõöøōŏő", 'o'), ("ŔŖŘ", 'R'), ("ŕŗř", 'r'),
        ("ŚŜŞŠ", 'S'), ("śŝşšſ", 's'), ("ŢŤŦ", 'T'), ("ţťŧ", 't'), ("ÙÚÛÜŨŪŬŮŰŲ", 'U'), ("ùúûüũūŭůűų", 'u'),
        ("Ŵ", 'W'), ("ŵ", 'w'), ("ÝŶŸ", 'Y'), ("ýÿŷ", 'y'), ("ŹŻŽ", 'Z'), ("źżž", 'z'),
    ];

    private static readonly char[] Table = BuildTable();

    /// <summary>Orden alfabético del español: sin distinguir tildes ni mayúsculas (salvo para desempatar) y la ñ tras la n.</summary>
    public static IComparer<string> Comparer { get; } = new SpanishComparer();

    /// <summary>La letra sin su tilde, diéresis o cedilla (la ñ da n); cualquier otro carácter queda igual.</summary>
    public static char Fold(char c) => c is >= First and <= Last && Table[c - First] is not '\0' and var folded ? folded : c;

    /// <summary>El texto sin tildes, diéresis ni cedillas en las letras latinas (la ñ da n). Mide lo mismo que el original.</summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Create(text.Length, text, static (chars, source) =>
        {
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = Fold(source[i]);
            }
        });
    }

    private static char[] BuildTable()
    {
        var table = new char[Last - First + 1];
        foreach ((string letters, char baseLetter) in Groups)
        {
            foreach (char c in letters)
            {
                table[c - First] = baseLetter;
            }
        }

        return table;
    }

    private sealed class SpanishComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null || y is null)
            {
                return x is null ? -1 : 1;
            }

            // Primero las letras (sin tildes ni mayúsculas; la ñ entre la n y la o), luego las tildes, luego las mayúsculas.
            int length = Math.Min(x.Length, y.Length);
            for (int i = 0; i < length; i++)
            {
                int c = Primary(x[i]).CompareTo(Primary(y[i]));
                if (c != 0)
                {
                    return c;
                }
            }

            if (x.Length != y.Length)
            {
                return x.Length.CompareTo(y.Length);
            }

            for (int i = 0; i < length; i++)
            {
                int c = Accented(x[i]).CompareTo(Accented(y[i]));
                if (c != 0)
                {
                    return c;
                }
            }

            for (int i = 0; i < length; i++)
            {
                int c = char.IsUpper(x[i]).CompareTo(char.IsUpper(y[i]));
                if (c != 0)
                {
                    return c;
                }
            }

            return string.CompareOrdinal(x, y);
        }

        private static int Primary(char c) => (char.ToLowerInvariant(Fold(c)) * 2) + (c is 'ñ' or 'Ñ' ? 1 : 0);

        private static bool Accented(char c) => c is not ('ñ' or 'Ñ') && Fold(c) != c;
    }
}
