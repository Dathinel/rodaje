using System.Text;

namespace Rodaje.Core.Editing;

/// <summary>
/// Muletillas por idioma (D-061). Las seguras casi nunca son palabras de verdad y se ofrecen marcadas; las dudosas sí
/// lo son a menudo y se ofrecen sin marcar. Se comparan en minúsculas y sin puntuación pero con tildes: en portugués
/// «hã» es muletilla y «há» no.
/// </summary>
public static class FillerWords
{
    private static readonly string[] SpanishSafe = ["eh", "em", "ehm", "mm", "mmm", "hmm", "ah"];
    private static readonly string[] SpanishDoubtful = ["o sea", "este", "bueno", "pues", "vale", "digamos", "en plan"];
    private static readonly string[] EnglishSafe = ["uh", "um", "uhm", "hmm", "mm", "er", "erm", "ah"];
    private static readonly string[] EnglishDoubtful = ["like", "you know", "i mean", "basically", "actually", "well"];
    private static readonly string[] PortugueseSafe = ["eh", "hã", "hum", "hmm", "ah", "ãh"];
    private static readonly string[] PortugueseDoubtful = ["é", "tipo", "né", "então", "assim", "quer dizer"];

    /// <summary>Muletillas seguras de <paramref name="language"/> («es», «en», «pt»; otro, ninguna), de una palabra.</summary>
    public static IReadOnlyList<string> Safe(string language) => Lists(language).Safe;

    /// <summary>Muletillas dudosas de <paramref name="language"/>; pueden ser de varias palabras («o sea»).</summary>
    public static IReadOnlyList<string> Doubtful(string language) => Lists(language).Doubtful;

    /// <summary>La palabra como se compara: en minúsculas y solo con letras y cifras.</summary>
    public static string Key(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var key = new StringBuilder(word.Length);
        foreach (char c in word)
        {
            if (char.IsLetterOrDigit(c))
            {
                key.Append(char.ToLowerInvariant(c));
            }
        }

        return key.ToString();
    }

    private static (string[] Safe, string[] Doubtful) Lists(string language) => (language ?? "").ToLowerInvariant() switch
    {
        "es" => (SpanishSafe, SpanishDoubtful),
        "en" => (EnglishSafe, EnglishDoubtful),
        "pt" => (PortugueseSafe, PortugueseDoubtful),
        _ => ([], []),
    };
}
