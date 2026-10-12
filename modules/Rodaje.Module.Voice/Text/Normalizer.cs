using System.Globalization;
using System.Text;

namespace Rodaje.Module.Voice.Text;

/// <summary>Lleva una palabra del texto a palabras para leer: cifras, símbolos, abreviaturas y siglas.</summary>
internal static partial class Normalizer
{
    private static readonly Dictionary<string, string> SpanishAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sr."] = "señor", ["sra."] = "señora", ["srta."] = "señorita", ["dr."] = "doctor", ["dra."] = "doctora",
        ["ud."] = "usted", ["uds."] = "ustedes", ["etc."] = "etcétera", ["pág."] = "página", ["núm."] = "número",
        ["aprox."] = "aproximadamente", ["av."] = "avenida", ["tel."] = "teléfono", ["lic."] = "licenciado",
        ["ee."] = "estados", ["uu."] = "unidos", ["km"] = "kilómetros", ["kg"] = "kilos", ["cm"] = "centímetros",
        ["mm"] = "milímetros", ["km/h"] = "kilómetros por hora",
    };

    private static readonly Dictionary<string, string> PortugueseAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sr."] = "senhor", ["sra."] = "senhora", ["dr."] = "doutor", ["dra."] = "doutora", ["etc."] = "etcétera",
        ["pág."] = "página", ["núm."] = "número", ["av."] = "avenida", ["tel."] = "telefone", ["prof."] = "professor",
        ["profa."] = "professora", ["aprox."] = "aproximadamente", ["eua"] = "estados unidos", ["km"] = "quilômetros",
        ["kg"] = "quilos", ["cm"] = "centímetros", ["mm"] = "milímetros", ["km/h"] = "quilômetros por hora",
    };

    private static readonly Dictionary<string, string> EnglishAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mr."] = "mister", ["mrs."] = "missus", ["ms."] = "miz", ["dr."] = "doctor", ["st."] = "saint", ["etc."] = "etcetera",
        ["vs."] = "versus", ["jr."] = "junior", ["sr."] = "senior", ["prof."] = "professor", ["approx."] = "approximately",
        ["km"] = "kilometers", ["kg"] = "kilograms", ["cm"] = "centimeters", ["mm"] = "millimeters", ["mph"] = "miles per hour",
    };

    public static bool IsAbbreviation(string word, Language language) => Abbreviations(language).ContainsKey(word);

    private static Dictionary<string, string> Abbreviations(Language language) => language switch
    {
        Language.PortugueseBrazil => PortugueseAbbreviations,
        Language.EnglishUs or Language.EnglishUk => EnglishAbbreviations,
        _ => SpanishAbbreviations,
    };

    public static List<string> Expand(string raw, Language language)
    {
        List<string> words = [];
        if (Abbreviations(language).TryGetValue(raw, out string? abbr) && (raw.EndsWith('.') || raw.Length <= 4))
        {
            words.AddRange(abbr.Split(' '));
            return words;
        }
        if (raw.StartsWith("R$", StringComparison.Ordinal) && raw.Length > 2 && char.IsDigit(raw[2]))
            return Number(raw, language);
        foreach (string part in SplitParts(raw))
        {
            if (part.Length == 0) continue;
            if (char.IsDigit(part[0]) || (part.Length > 1 && part[0] is '$' or '€' or '£' && char.IsDigit(part[1])) || part.StartsWith("R$", StringComparison.Ordinal))
            {
                words.AddRange(Number(part, language));
            }
            else if (Symbol(part, language) is { } symbol)
            {
                words.AddRange(symbol.Split(' '));
            }
            else if (part.Length <= 4 && part.All(char.IsLower) && Abbreviations(language).TryGetValue(part, out string? unit))
            {
                words.AddRange(unit.Split(' '));
            }
            else if (Roman(part) is { } roman)
            {
                words.AddRange(NumberWords.Cardinal(roman, language).Split(' '));
            }
            else if (IsAcronym(part))
            {
                foreach (char c in part) words.AddRange(LetterName(char.ToLowerInvariant(c), language).Split(' '));
            }
            else
            {
                string w = part.ToLowerInvariant().Replace('’', '\'');
                if (!language.IsEnglish()) w = w.Replace("'", "", StringComparison.Ordinal);
                if (w.Any(char.IsLetter)) words.Add(w);
            }
        }
        return words;
    }

    // «MP3» → «MP», «3»; «e-mail» → «e», «mail»; «10:30» y «1.000» se quedan juntos.
    private static IEnumerable<string> SplitParts(string raw)
    {
        if (OrdinalPattern().IsMatch(raw))
        {
            yield return raw;
            yield break;
        }
        StringBuilder sb = new();
        int kind = 0; // 1 letras, 2 cifras
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c is '-' or '/' && !(i > 0 && i + 1 < raw.Length && char.IsDigit(raw[i - 1]) && char.IsDigit(raw[i + 1]) && c == '/'))
            {
                yield return sb.ToString();
                sb.Clear();
                kind = 0;
                continue;
            }
            int k = char.IsLetter(c) || c is '\'' or '’' ? 1 : 2;
            if (c is 'º' or 'ª' && kind == 2) k = 2;
            if (kind == 2 && char.IsLetter(c) && IsOrdinalSuffix(raw, i)) k = 2;
            if (kind == 1 && c == '$' && sb.ToString() == "R") k = 1;
            if (kind != 0 && k != kind)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            sb.Append(c);
            kind = k;
        }
        yield return sb.ToString();
    }

    private static bool IsOrdinalSuffix(string raw, int i)
    {
        string rest = raw[i..].ToLowerInvariant();
        return rest is "st" or "nd" or "rd" or "th" or "o" or "a";
    }

    // Siglas en mayúsculas: hasta 3 letras se deletrean («IA», «CEP»), más largas solo si no tienen vocales («NPWS»).
    private static bool IsAcronym(string w) =>
        w.Length >= 2 && w.All(char.IsAsciiLetterUpper) && (w.Length <= 3 || (w.Length <= 6 && !w.Any(c => "AEIOU".Contains(c, StringComparison.Ordinal))));

    // Números romanos en mayúsculas de dos letras o más («II», «XVIII»), escritos de forma canónica.
    private static int? Roman(string w)
    {
        if (w.Length < 2 || !w.All(c => "IVXLCDM".Contains(c, StringComparison.Ordinal))) return null;
        int total = 0;
        for (int i = 0; i < w.Length; i++)
        {
            int v = RomanValue(w[i]);
            total += i + 1 < w.Length && RomanValue(w[i + 1]) > v ? -v : v;
        }
        return total is > 0 and < 4000 && ToRoman(total) == w ? total : null;
    }

    private static int RomanValue(char c) => c switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, _ => 1000 };

    private static string ToRoman(int n)
    {
        (int V, string S)[] table = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        StringBuilder sb = new();
        foreach ((int v, string r) in table)
            while (n >= v) { sb.Append(r); n -= v; }
        return sb.ToString();
    }

    private static string? Symbol(string s, Language l) => s switch
    {
        "%" => l switch { Language.PortugueseBrazil => "por cento", Language.EnglishUs or Language.EnglishUk => "percent", _ => "por ciento" },
        "&" => l switch { Language.PortugueseBrazil => "e", Language.EnglishUs or Language.EnglishUk => "and", _ => "y" },
        "+" => l switch { Language.PortugueseBrazil => "mais", Language.EnglishUs or Language.EnglishUk => "plus", _ => "más" },
        "@" => l switch { Language.PortugueseBrazil => "arroba", Language.EnglishUs or Language.EnglishUk => "at", _ => "arroba" },
        "€" => l switch { Language.PortugueseBrazil => "euros", Language.EnglishUs or Language.EnglishUk => "euros", _ => "euros" },
        "$" => l switch { Language.PortugueseBrazil => "dólares", Language.EnglishUs or Language.EnglishUk => "dollars", _ => "dólares" },
        "#" => l switch { Language.PortugueseBrazil => "número", Language.EnglishUs or Language.EnglishUk => "number", _ => "número" },
        "°" => l switch { Language.PortugueseBrazil => "graus", Language.EnglishUs or Language.EnglishUk => "degrees", _ => "grados" },
        _ => null,
    };

    private static string LetterName(char c, Language l)
    {
        if (l.IsEnglish())
        {
            return c switch
            {
                'a' => "a", 'b' => "bee", 'c' => "see", 'd' => "dee", 'e' => "e", 'f' => "ef", 'g' => "gee", 'h' => "aitch", 'i' => "i",
                'j' => "jay", 'k' => "kay", 'l' => "el", 'm' => "em", 'n' => "en", 'o' => "o", 'p' => "pee", 'q' => "cue", 'r' => "ar",
                's' => "ess", 't' => "tee", 'u' => "you", 'v' => "vee", 'w' => "double you", 'x' => "ex", 'y' => "why", 'z' => "zee",
                _ => c.ToString(),
            };
        }
        if (l == Language.PortugueseBrazil)
        {
            return c switch
            {
                'a' => "á", 'b' => "bê", 'c' => "cê", 'd' => "dê", 'e' => "é", 'f' => "éfe", 'g' => "gê", 'h' => "agá", 'i' => "i",
                'j' => "jota", 'k' => "cá", 'l' => "éle", 'm' => "ême", 'n' => "êne", 'o' => "ó", 'p' => "pê", 'q' => "quê", 'r' => "érre",
                's' => "ésse", 't' => "tê", 'u' => "u", 'v' => "vê", 'w' => "dáblio", 'x' => "xis", 'y' => "ípsilon", 'z' => "zê",
                _ => c.ToString(),
            };
        }
        return c switch
        {
            'a' => "a", 'b' => "be", 'c' => "ce", 'd' => "de", 'e' => "e", 'f' => "efe", 'g' => "ge", 'h' => "hache", 'i' => "i",
            'j' => "jota", 'k' => "ka", 'l' => "ele", 'm' => "eme", 'n' => "ene", 'o' => "o", 'p' => "pe", 'q' => "cu", 'r' => "erre",
            's' => "ese", 't' => "te", 'u' => "u", 'v' => "uve", 'w' => "uve doble", 'x' => "equis", 'y' => "ye", 'z' => "zeta",
            _ => c.ToString(),
        };
    }

    private static List<string> Number(string s, Language l)
    {
        List<string> words = [];
        string? unit = null;
        if (s.StartsWith("R$", StringComparison.Ordinal)) { unit = "reais"; s = s[2..]; }
        else if (s[0] is '$' or '€' or '£') { unit = Currency(s[0], l); s = s[1..]; }
        if (s.Length > 0 && s[^1] is '€' or '$' or '£') { unit = Currency(s[^1], l); s = s[..^1]; }
        if (s.EndsWith('%')) { unit = Symbol("%", l); s = s[..^1]; }
        if (s.EndsWith('°')) { unit = Symbol("°", l); s = s[..^1]; }

        string lower = s.ToLowerInvariant();
        foreach (string suffix in (string[])["º", "ª", "st", "nd", "rd", "th", "o", "a"])
        {
            if (lower.Length > suffix.Length && lower.EndsWith(suffix, StringComparison.Ordinal) && lower[..^suffix.Length].All(char.IsDigit))
            {
                long n = long.Parse(lower[..^suffix.Length], CultureInfo.InvariantCulture);
                bool feminine = suffix is "ª" or "a";
                words.AddRange(NumberWords.Ordinal(n, l, feminine).Split(' '));
                return words;
            }
        }

        if (s.Contains(':', StringComparison.Ordinal))
        {
            // Hora: «10:30» → «diez treinta»; «10:00» → «diez en punto» no se intenta.
            string[] parts = s.Split(':');
            foreach (string p in parts)
            {
                if (p.Length == 0 || !p.All(char.IsDigit)) continue;
                long v = long.Parse(p, CultureInfo.InvariantCulture);
                if (v == 0 && p != parts[0]) continue;
                words.AddRange(NumberWords.Cardinal(v, l).Split(' '));
            }
            return words;
        }

        (string integer, string? fraction) = SplitDecimal(s, l);
        string digits = new(integer.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return words;
        if (digits.Length > 15)
        {
            foreach (char c in digits) words.AddRange(NumberWords.Cardinal(c - '0', l).Split(' '));
        }
        else
        {
            long value = long.Parse(digits, CultureInfo.InvariantCulture);
            words.AddRange(NumberWords.Cardinal(value, l).Split(' '));
        }
        if (fraction is { Length: > 0 })
        {
            words.Add(l switch { Language.PortugueseBrazil => "vírgula", Language.EnglishUs or Language.EnglishUk => "point", _ => "coma" });
            if (fraction.Length <= 2 && fraction[0] != '0' && !l.IsEnglish())
                words.AddRange(NumberWords.Cardinal(long.Parse(fraction, CultureInfo.InvariantCulture), l).Split(' '));
            else
                foreach (char c in fraction) words.AddRange(NumberWords.Cardinal(c - '0', l).Split(' '));
        }
        if (unit is not null) words.AddRange(unit.Split(' '));
        return words;
    }

    private static string Currency(char c, Language l) => c switch
    {
        '€' => "euros",
        '£' => l.IsEnglish() ? "pounds" : "libras",
        _ => l switch { Language.PortugueseBrazil => "dólares", Language.EnglishUs or Language.EnglishUk => "dollars", _ => "dólares" },
    };

    // «1.000» y «1,000» son miles según el idioma; «3,5» y «3.5», decimales.
    private static (string Integer, string? Fraction) SplitDecimal(string s, Language l)
    {
        char thousands = l.IsEnglish() ? ',' : '.';
        char decimals = l.IsEnglish() ? '.' : ',';
        int at = s.LastIndexOf(decimals);
        string integer = at >= 0 ? s[..at] : s;
        string? fraction = at >= 0 ? s[(at + 1)..] : null;
        // Un separador de miles mal puesto («3.5» en español) se lee como decimal.
        if (fraction is null && s.IndexOf(thousands) is int t and >= 0 && s.Split(thousands).Skip(1).Any(g => g.Length != 3))
        {
            integer = s[..t];
            fraction = s[(t + 1)..].Replace(thousands.ToString(), "", StringComparison.Ordinal);
        }
        return (integer, fraction?.All(char.IsDigit) == true ? fraction : null);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9]+(st|nd|rd|th|º|ª|o|a)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex OrdinalPattern();
}
