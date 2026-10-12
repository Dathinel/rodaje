using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rodaje.Module.Voice.Text;

internal enum TokenKind
{
    Word,
    Punct,
}

/// <summary>Una palabra ya normalizada (minúsculas, sin cifras) o un signo de puntuación.</summary>
internal readonly record struct Token(TokenKind Kind, string Text);

/// <summary>Una frase para el sintetizador: sus palabras, el texto original y la pausa que va después.</summary>
internal sealed record Sentence(IReadOnlyList<Token> Tokens, string Source, double PauseAfter, bool EndsClause = false);

/// <summary>
/// Convierte el texto del usuario en frases: marcas de pausa (<c>[pausa]</c>, <c>[pausa 1.5]</c>), cifras, símbolos y
/// abreviaturas leídas en el idioma, y cortes en el punto, el signo de cierre, el salto de línea y, si se pide, la coma.
/// </summary>
internal static partial class Script
{
    public const double DefaultMarkupPause = 0.5;
    public const double MaxPause = 10;

    public static List<Sentence> Parse(string text, Language language, double sentencePause, double commaPause)
    {
        List<Sentence> sentences = [];
        List<Token> current = [];
        StringBuilder source = new();
        int pos = 0;
        foreach (Match m in PauseMarkup().Matches(text))
        {
            AddText(text[pos..m.Index]);
            Close(ParsePause(m.Groups[1].Value), force: true);
            pos = m.Index + m.Length;
        }
        AddText(text[pos..]);
        Close(sentencePause, force: false);
        return sentences;

        void AddText(string chunk)
        {
            foreach (RawToken raw in Tokenize(chunk, language))
            {
                if (raw.IsBreak)
                {
                    if (current.Count > 0 && current[^1].Kind == TokenKind.Word) current.Add(new Token(TokenKind.Punct, "."));
                    Close(sentencePause, force: false);
                    continue;
                }
                source.Append(raw.Source);
                if (raw.Text.Length == 0) continue;
                if (raw.Kind == TokenKind.Word)
                {
                    foreach (string w in Normalizer.Expand(raw.Text, language)) current.Add(new Token(TokenKind.Word, w));
                    continue;
                }
                current.Add(new Token(TokenKind.Punct, raw.Text));
                if (raw.Text is "." or "!" or "?" or "…") Close(sentencePause, force: false);
                else if (commaPause > 0 && raw.Text is "," or ";" or ":") Close(commaPause, force: false, clause: true);
            }
        }

        void Close(double pause, bool force, bool clause = false)
        {
            if (current.Any(t => t.Kind == TokenKind.Word))
            {
                sentences.Add(new Sentence([.. current], source.ToString().Trim(), pause, clause));
            }
            else if (force && sentences.Count > 0)
            {
                Sentence last = sentences[^1];
                sentences[^1] = last with { PauseAfter = last.PauseAfter + pause };
            }
            else if (force)
            {
                sentences.Add(new Sentence([], "", pause));
            }
            current.Clear();
            source.Clear();
        }
    }

    private static double ParsePause(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultMarkupPause;
        string v = value.Trim().TrimEnd('s', 'S').Trim().Replace(',', '.');
        bool ms = v.EndsWith('m');
        if (ms) v = v[..^1];
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && double.IsFinite(s)
            ? Math.Clamp(ms ? s / 1000 : s, 0, MaxPause)
            : DefaultMarkupPause;
    }

    private readonly record struct RawToken(TokenKind Kind, string Text, string Source, bool IsBreak = false);

    private static IEnumerable<RawToken> Tokenize(string text, Language language)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n' || c == '\r')
            {
                int j = i;
                while (j < text.Length && text[j] is '\n' or '\r') j++;
                yield return new RawToken(TokenKind.Punct, "", text[i..j], IsBreak: true);
                i = j;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                yield return new RawToken(TokenKind.Punct, "", " ");
                i++;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c is '$' or '€' or '£' or '%' or '&' or '@' or '+' or '#' or '°' or 'º' or 'ª')
            {
                int j = i + 1;
                while (j < text.Length && IsWordChar(text, j)) j++;
                string word = text[i..j];
                // Abreviatura con punto («Sr.», «etc.», «EE. UU.»): el punto no cierra la frase.
                if (j < text.Length && text[j] == '.' && Normalizer.IsAbbreviation(word + ".", language))
                {
                    yield return new RawToken(TokenKind.Word, word + ".", text[i..(j + 1)]);
                    i = j + 1;
                    continue;
                }
                yield return new RawToken(TokenKind.Word, word, word);
                i = j;
                continue;
            }
            string? p = c switch
            {
                '.' when i + 2 < text.Length && text[i + 1] == '.' && text[i + 2] == '.' => "…",
                '.' or '…' => c == '…' ? "…" : ".",
                ',' or ';' or ':' => c.ToString(),
                '!' or '¡' => c == '!' ? "!" : null,
                '?' or '¿' => c == '?' ? "?" : null,
                '—' or '–' => "—",
                '(' or ')' => ",",
                '"' or '“' or '”' or '«' or '»' => null,
                _ => null,
            };
            int len = p == "…" && c == '.' ? 3 : 1;
            if (p is not null && p.Length > 0) yield return new RawToken(TokenKind.Punct, p, text.Substring(i, len));
            else yield return new RawToken(TokenKind.Punct, "", text.Substring(i, len));
            i += len;
        }
    }

    private static bool IsWordChar(string text, int j)
    {
        char c = text[j];
        if (char.IsLetterOrDigit(c) || c is '%' or '€' or '$' or 'º' or 'ª' or '°') return true;
        // Apóstrofo o separador dentro de una palabra o una cifra: «don't», «1.000», «3,5», «10:30», «e-mail».
        if (c is '\'' or '’' or '.' or ',' or ':' or '-' or '/' && j + 1 < text.Length && j > 0)
        {
            char a = text[j - 1], b = text[j + 1];
            if (c is '.' or ',' or ':' or '/') return char.IsDigit(a) && char.IsDigit(b);
            if (c == '-') return char.IsLetterOrDigit(a) && char.IsLetterOrDigit(b);
            return char.IsLetter(a) && char.IsLetter(b);
        }
        return false;
    }

    [GeneratedRegex(@"\[\s*(?:pausa|pause)\s*([0-9.,]*\s*(?:ms|s)?)\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PauseMarkup();
}
