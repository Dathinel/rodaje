namespace Rodaje.Module.Voice.Text;

internal enum Language
{
    Spanish,
    SpanishLatam,
    PortugueseBrazil,
    EnglishUs,
    EnglishUk,
}

internal static class Languages
{
    public static readonly (string Id, string Name, Language Value)[] All =
    [
        ("es", "Español (España)", Language.Spanish),
        ("es-419", "Español (Latinoamérica)", Language.SpanishLatam),
        ("pt-BR", "Português (Brasil)", Language.PortugueseBrazil),
        ("en-US", "English (US)", Language.EnglishUs),
        ("en-GB", "English (UK)", Language.EnglishUk),
    ];

    public static bool TryParse(string? id, out Language language)
    {
        foreach (var l in All)
        {
            if (string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                language = l.Value;
                return true;
            }
        }
        language = default;
        return false;
    }

    public static string Id(Language language) => All.First(l => l.Value == language).Id;

    public static bool IsSpanish(this Language l) => l is Language.Spanish or Language.SpanishLatam;

    public static bool IsEnglish(this Language l) => l is Language.EnglishUs or Language.EnglishUk;
}
