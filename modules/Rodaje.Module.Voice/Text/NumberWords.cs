namespace Rodaje.Module.Voice.Text;

/// <summary>Números en palabras en español, portugués de Brasil e inglés (enteros hasta 10^15 − 1).</summary>
internal static class NumberWords
{
    public static string Cardinal(long n, Language l, bool feminine = false) => l switch
    {
        Language.PortugueseBrazil => Portuguese(n, feminine),
        Language.EnglishUs or Language.EnglishUk => English(n),
        _ => Spanish(n, feminine),
    };

    public static string Ordinal(long n, Language l, bool feminine) => l switch
    {
        Language.PortugueseBrazil => PortugueseOrdinal(n, feminine),
        Language.EnglishUs or Language.EnglishUk => EnglishOrdinal(n),
        _ => SpanishOrdinal(n, feminine),
    };

    // --- Español ---

    private static readonly string[] EsUnits =
    [
        "cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve", "diez", "once", "doce", "trece",
        "catorce", "quince", "dieciséis", "diecisiete", "dieciocho", "diecinueve", "veinte", "veintiuno", "veintidós",
        "veintitrés", "veinticuatro", "veinticinco", "veintiséis", "veintisiete", "veintiocho", "veintinueve",
    ];

    private static readonly string[] EsTens = ["", "", "", "treinta", "cuarenta", "cincuenta", "sesenta", "setenta", "ochenta", "noventa"];

    private static readonly string[] EsHundreds =
        ["", "ciento", "doscientos", "trescientos", "cuatrocientos", "quinientos", "seiscientos", "setecientos", "ochocientos", "novecientos"];

    private static string Spanish(long n, bool feminine)
    {
        if (n < 0) return "menos " + Spanish(-n, feminine);
        if (n == 0) return "cero";
        List<string> parts = [];
        long billions = n / 1_000_000_000_000, millions = n / 1_000_000 % 1_000_000, thousands = n / 1000 % 1000, rest = n % 1000;
        if (billions > 0) parts.Add(billions == 1 ? "un billón" : Apocope(SpanishBelowThousand(billions, false)) + " billones");
        if (millions > 0)
        {
            string m = millions >= 1000 ? SpanishThousands(millions / 1000, millions % 1000, false) : SpanishBelowThousand(millions, false);
            parts.Add(millions == 1 ? "un millón" : Apocope(m) + " millones");
        }
        if (thousands > 0) parts.Add(thousands == 1 ? "mil" : Apocope(SpanishBelowThousand(thousands, feminine)) + " mil");
        if (rest > 0) parts.Add(SpanishBelowThousand(rest, feminine));
        return string.Join(' ', parts);
    }

    private static string SpanishThousands(long thousands, long rest, bool feminine) =>
        (thousands == 1 ? "mil" : Apocope(SpanishBelowThousand(thousands, feminine)) + " mil") + (rest > 0 ? " " + SpanishBelowThousand(rest, feminine) : "");

    // «uno» delante de un sustantivo: «veintiún mil», «un millón».
    private static string Apocope(string s) =>
        s.EndsWith("veintiuno", StringComparison.Ordinal) ? s[..^9] + "veintiún"
        : s.EndsWith("uno", StringComparison.Ordinal) ? s[..^1] : s;

    private static string SpanishBelowThousand(long n, bool feminine)
    {
        if (n == 100) return "cien";
        List<string> parts = [];
        long h = n / 100, r = n % 100;
        if (h > 0)
        {
            string hw = EsHundreds[h];
            if (feminine && h > 1) hw = hw[..^2] + "as";
            parts.Add(hw);
        }
        if (r > 0)
        {
            string rw = r < 30 ? EsUnits[r] : EsTens[r / 10] + (r % 10 > 0 ? " y " + EsUnits[r % 10] : "");
            if (feminine && rw.EndsWith("uno", StringComparison.Ordinal)) rw = rw[..^1] + "a";
            parts.Add(rw);
        }
        return string.Join(' ', parts);
    }

    private static string SpanishOrdinal(long n, bool feminine)
    {
        string[] ordinals = ["", "primero", "segundo", "tercero", "cuarto", "quinto", "sexto", "séptimo", "octavo", "noveno", "décimo"];
        if (n is >= 1 and <= 10)
        {
            string o = ordinals[n];
            return feminine ? o[..^1] + "a" : o;
        }
        return Spanish(n, feminine);
    }

    // --- Portugués de Brasil ---

    private static readonly string[] PtUnits =
    [
        "zero", "um", "dois", "três", "quatro", "cinco", "seis", "sete", "oito", "nove", "dez", "onze", "doze", "treze",
        "catorze", "quinze", "dezesseis", "dezessete", "dezoito", "dezenove",
    ];

    private static readonly string[] PtTens = ["", "", "vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta", "oitenta", "noventa"];

    private static readonly string[] PtHundreds =
        ["", "cento", "duzentos", "trezentos", "quatrocentos", "quinhentos", "seiscentos", "setecentos", "oitocentos", "novecentos"];

    private static string Portuguese(long n, bool feminine)
    {
        if (n < 0) return "menos " + Portuguese(-n, feminine);
        if (n == 0) return "zero";
        List<(long Value, string Words)> groups = [];
        long billions = n / 1_000_000_000_000, milliards = n / 1_000_000_000 % 1000, millions = n / 1_000_000 % 1000, thousands = n / 1000 % 1000, rest = n % 1000;
        if (billions > 0) groups.Add((billions, PortugueseBelowThousand(billions, false) + (billions == 1 ? " trilhão" : " trilhões")));
        if (milliards > 0) groups.Add((milliards, PortugueseBelowThousand(milliards, false) + (milliards == 1 ? " bilhão" : " bilhões")));
        if (millions > 0) groups.Add((millions, PortugueseBelowThousand(millions, false) + (millions == 1 ? " milhão" : " milhões")));
        if (thousands > 0) groups.Add((thousands, thousands == 1 ? "mil" : PortugueseBelowThousand(thousands, feminine) + " mil"));
        if (rest > 0) groups.Add((rest, PortugueseBelowThousand(rest, feminine)));
        string result = groups[0].Words;
        for (int i = 1; i < groups.Count; i++)
        {
            // «mil e duzentos», «mil duzentos e trinta»: «e» antes del último grupo si es redondo o menor que cien.
            bool last = i == groups.Count - 1;
            bool joinWithE = last && (groups[i].Value < 100 || groups[i].Value % 100 == 0);
            result += (joinWithE ? " e " : " ") + groups[i].Words;
        }
        return result;
    }

    private static string PortugueseBelowThousand(long n, bool feminine)
    {
        if (n == 100) return "cem";
        List<string> parts = [];
        long h = n / 100, r = n % 100;
        if (h > 0)
        {
            string hw = PtHundreds[h];
            if (feminine && h > 1) hw = hw[..^2] + "as";
            parts.Add(hw);
        }
        if (r > 0)
        {
            string rw = r < 20 ? PtUnits[r] : PtTens[r / 10] + (r % 10 > 0 ? " e " + PtUnits[r % 10] : "");
            if (feminine) rw = rw.EndsWith("um", StringComparison.Ordinal) ? rw[..^2] + "uma" : rw.EndsWith("dois", StringComparison.Ordinal) ? rw[..^4] + "duas" : rw;
            parts.Add(rw);
        }
        return string.Join(" e ", parts);
    }

    private static string PortugueseOrdinal(long n, bool feminine)
    {
        string[] ordinals = ["", "primeiro", "segundo", "terceiro", "quarto", "quinto", "sexto", "sétimo", "oitavo", "nono", "décimo"];
        if (n is >= 1 and <= 10)
        {
            string o = ordinals[n];
            return feminine ? o[..^1] + "a" : o;
        }
        return Portuguese(n, feminine);
    }

    // --- Inglés ---

    private static readonly string[] EnUnits =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
        "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] EnTens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static string English(long n)
    {
        if (n < 0) return "minus " + English(-n);
        if (n == 0) return "zero";
        List<string> parts = [];
        (long Div, string Name)[] scales = [(1_000_000_000_000, "trillion"), (1_000_000_000, "billion"), (1_000_000, "million"), (1000, "thousand")];
        foreach ((long div, string name) in scales)
        {
            long g = n / div % 1000;
            if (g > 0) parts.Add(EnglishBelowThousand(g) + " " + name);
        }
        if (n % 1000 > 0) parts.Add(EnglishBelowThousand(n % 1000));
        return string.Join(' ', parts);
    }

    private static string EnglishBelowThousand(long n)
    {
        List<string> parts = [];
        if (n / 100 > 0) parts.Add(EnUnits[n / 100] + " hundred");
        long r = n % 100;
        if (r > 0) parts.Add(r < 20 ? EnUnits[r] : EnTens[r / 10] + (r % 10 > 0 ? "-" + EnUnits[r % 10] : ""));
        return string.Join(' ', parts).Replace('-', ' ');
    }

    private static string EnglishOrdinal(long n)
    {
        string c = English(n);
        string[] words = c.Split(' ');
        string last = words[^1];
        last = last switch
        {
            "one" => "first", "two" => "second", "three" => "third", "five" => "fifth", "eight" => "eighth", "nine" => "ninth",
            "twelve" => "twelfth",
            _ when last.EndsWith('y') => last[..^1] + "ieth",
            _ => last + "th",
        };
        words[^1] = last;
        return string.Join(' ', words);
    }
}
