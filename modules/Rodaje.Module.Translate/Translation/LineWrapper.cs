using System.Globalization;
using System.Text;

namespace Rodaje.Module.Translate.Translation;

/// <summary>
/// Parte una traducción en líneas de subtítulo: ninguna pasa de <c>maxChars</c> caracteres (contados como elementos de
/// texto, así una letra con tilde combinada o un emoji cuentan uno). Usa el mínimo de líneas y, si son dos, el corte
/// más parejo con la de arriba más corta a igual ancho (como <c>SubtitleEdits.Wrap</c> de la app). Una palabra más
/// larga que la línea (una URL, un texto sin espacios en chino o japonés) se corta donde llega al límite.
/// </summary>
internal static class LineWrapper
{
    public static string Wrap(string text, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        List<string> words = [];
        foreach (string word in Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            words.AddRange(Pieces(word, maxChars));
        }

        List<string> lines = [];
        StringBuilder line = new();
        int lineLength = 0;
        foreach (string word in words)
        {
            int length = Length(word);
            if (lineLength > 0 && lineLength + 1 + length > maxChars)
            {
                lines.Add(line.ToString());
                line.Clear();
                lineLength = 0;
            }

            if (lineLength > 0)
            {
                line.Append(' ');
                lineLength++;
            }

            line.Append(word);
            lineLength += length;
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        if (lines.Count == 2)
        {
            int best = -1, bestWidth = int.MaxValue;
            for (int cut = 1; cut < words.Count; cut++)
            {
                int top = Length(string.Join(' ', words.Take(cut))), bottom = Length(string.Join(' ', words.Skip(cut)));
                int width = Math.Max(top, bottom);
                if (top <= maxChars && bottom <= maxChars && (width < bestWidth || (width == bestWidth && top <= bottom)))
                {
                    (best, bestWidth) = (cut, width);
                }
            }

            if (best > 0)
            {
                lines = [string.Join(' ', words.Take(best)), string.Join(' ', words.Skip(best))];
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>Largo en elementos de texto (lo que se ve como un carácter).</summary>
    public static int Length(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>Una sola línea, sin caracteres de control ni espacios repetidos.</summary>
    public static string Normalize(string text)
    {
        StringBuilder sb = new(text.Length);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    private static IEnumerable<string> Pieces(string word, int maxChars)
    {
        if (Length(word) <= maxChars)
        {
            yield return word;
            yield break;
        }

        StringBuilder piece = new();
        int count = 0;
        TextElementEnumerator e = StringInfo.GetTextElementEnumerator(word);
        while (e.MoveNext())
        {
            if (count == maxChars)
            {
                yield return piece.ToString();
                piece.Clear();
                count = 0;
            }

            piece.Append(e.GetTextElement());
            count++;
        }

        if (piece.Length > 0)
        {
            yield return piece.ToString();
        }
    }
}
