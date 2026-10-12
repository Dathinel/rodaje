using System.Text.Json;
using Rodaje.Module.Voice.Text;

namespace Rodaje.Module.Voice.Phonemes;

/// <summary>
/// Léxico de misaki (Apache-2.0, hexgrad): <c>us_gold</c>/<c>us_silver</c> o <c>gb_gold</c>/<c>gb_silver</c>, ya en el
/// alfabeto de Kokoro. Sin etiquetador gramatical: de una palabra con varias lecturas se toma la de por defecto.
/// </summary>
internal sealed class EnglishLexicon
{
    private readonly Dictionary<string, string> _words;

    private EnglishLexicon(Dictionary<string, string> words) => _words = words;

    public int Count => _words.Count;

    /// <summary>Lee el oro y luego la plata (el oro gana).</summary>
    public static EnglishLexicon Load(string gold, string silver)
    {
        Dictionary<string, string> words = new(StringComparer.Ordinal);
        Read(silver, words);
        Read(gold, words);
        return new EnglishLexicon(words);
    }

    public static EnglishLexicon FromEntries(IEnumerable<KeyValuePair<string, string>> entries) => new(new Dictionary<string, string>(entries, StringComparer.Ordinal));

    public bool TryGet(string word, out string phonemes)
    {
        if (_words.TryGetValue(word, out phonemes!)) return true;
        string lower = word.ToLowerInvariant();
        if (_words.TryGetValue(lower, out phonemes!)) return true;
        string capital = lower.Length > 0 ? char.ToUpperInvariant(lower[0]) + lower[1..] : lower;
        return _words.TryGetValue(capital, out phonemes!);
    }

    private static void Read(string path, Dictionary<string, string> into)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Utf8JsonReader r = new(bytes);
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("Léxico inválido: " + path);
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            string key = r.GetString()!;
            r.Read();
            if (r.TokenType == JsonTokenType.String)
            {
                into[key] = r.GetString()!;
            }
            else if (r.TokenType == JsonTokenType.StartObject)
            {
                string? fallback = null;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    string pos = r.GetString()!;
                    r.Read();
                    if (r.TokenType == JsonTokenType.String && (pos == "DEFAULT" || fallback is null)) fallback = r.GetString();
                    else if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip();
                }
                if (fallback is not null) into[key] = fallback;
            }
            else
            {
                r.Skip();
            }
        }
    }
}

internal sealed class EnglishPhonemizer(EnglishLexicon lexicon, bool british) : IPhonemizer
{
    public bool British { get; } = british;

    public string Phonemize(IReadOnlyList<Token> tokens) =>
        Phones.Join(tokens.Select(t => (t.Kind == TokenKind.Punct, t.Kind == TokenKind.Punct ? t.Text : Word(t.Text))));

    internal string Word(string word)
    {
        if (lexicon.TryGet(word, out string p)) return p;
        if (Suffixed(word) is { } s) return s;
        // Compuesto de dos palabras del léxico («videoeditor»).
        for (int cut = word.Length - 3; cut >= 3; cut--)
        {
            if (lexicon.TryGet(word[..cut], out string a) && lexicon.TryGet(word[cut..], out string b)) return a + b;
        }
        // Último recurso: deletrear.
        return string.Concat(word.Where(char.IsLetter).Select(c => lexicon.TryGet(c.ToString(), out string l) ? l : ""));
    }

    private string? Suffixed(string word)
    {
        string w = word.ToLowerInvariant();
        if (w.EndsWith("'s", StringComparison.Ordinal) && Stem(w[..^2]) is { } poss) return Plural(poss);
        if (w.EndsWith('s') && w.Length > 3)
        {
            if (Stem(w[..^1]) is { } a) return Plural(a);
            if (w.EndsWith("es", StringComparison.Ordinal) && Stem(w[..^2]) is { } b) return Plural(b);
            if (w.EndsWith("ies", StringComparison.Ordinal) && Stem(w[..^3] + "y") is { } c) return Plural(c);
        }
        if (w.EndsWith("ed", StringComparison.Ordinal) && w.Length > 4)
        {
            string? stem = Stem(w[..^2]) ?? Stem(w[..^1]) ?? (w[^3] == w[^4] ? Stem(w[..^3]) : null)
                ?? (w.EndsWith("ied", StringComparison.Ordinal) ? Stem(w[..^3] + "y") : null);
            if (stem is not null) return Past(stem);
        }
        if (w.EndsWith("ing", StringComparison.Ordinal) && w.Length > 5)
        {
            string? stem = Stem(w[..^3]) ?? Stem(w[..^3] + "e") ?? (w[^4] == w[^5] ? Stem(w[..^4]) : null);
            if (stem is not null) return stem + "ɪŋ";
        }
        if (w.EndsWith("ly", StringComparison.Ordinal) && w.Length > 4 && Stem(w[..^2]) is { } ly) return ly + "li";
        return null;
    }

    private string? Stem(string w) => w.Length >= 2 && lexicon.TryGet(w, out string p) ? p : null;

    private static string Plural(string stem)
    {
        char last = stem.TrimEnd('ˈ', 'ˌ')[^1];
        if ("szʃʒʧʤ".Contains(last, StringComparison.Ordinal)) return stem + "ᵻz";
        if ("ptkfθ".Contains(last, StringComparison.Ordinal)) return stem + "s";
        return stem + "z";
    }

    private static string Past(string stem)
    {
        char last = stem[^1];
        if (last is 't' or 'd') return stem + "ᵻd";
        if ("pkfθʃʧs".Contains(last, StringComparison.Ordinal)) return stem + "t";
        return stem + "d";
    }
}
