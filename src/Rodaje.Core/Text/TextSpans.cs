using Rodaje.Core.Model;

namespace Rodaje.Core.Text;

/// <summary>
/// Reglas de los tramos con formato de un título (<see cref="ClipGenerator.Spans"/>): siempre en orden, sin solaparse, sin
/// tramos vacíos ni sin formato, y con los vecinos iguales unidos. Lo usan la pestaña Texto, la edición en el visor y el
/// validador.
/// </summary>
public static class TextSpans
{
    public const int MaxSpans = 512;
    public const double MinScale = 0.1;
    public const double MaxScale = 10;

    /// <summary>
    /// Cambia el formato de <c>[start, start + length)</c> con <paramref name="change"/> (que recibe el formato que tenía
    /// cada trozo) y devuelve la lista normalizada, o nula si no queda ninguno.
    /// </summary>
    public static List<TextSpan>? Apply(IReadOnlyList<TextSpan>? spans, int textLength, int start, int length, Func<TextSpan, TextSpan> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        start = Math.Clamp(start, 0, textLength);
        int end = Math.Clamp(start + Math.Max(0, length), start, textLength);
        var cuts = new SortedSet<int> { 0, textLength, start, end };
        foreach (TextSpan span in spans ?? [])
        {
            cuts.Add(Math.Clamp(span.Start, 0, textLength));
            cuts.Add(Math.Clamp(span.End, 0, textLength));
        }

        var pieces = new List<TextSpan>();
        int previous = -1;
        foreach (int cut in cuts)
        {
            if (previous >= 0 && cut > previous)
            {
                TextSpan format = At(spans, previous) ?? new TextSpan();
                TextSpan piece = format with { Start = previous, Length = cut - previous };
                if (previous >= start && cut <= end)
                {
                    piece = change(piece) with { Start = previous, Length = cut - previous };
                }

                pieces.Add(piece);
            }

            previous = cut;
        }

        return Normalize(pieces, textLength);
    }

    /// <summary>El tramo que cubre el carácter <paramref name="index"/>, o nulo.</summary>
    public static TextSpan? At(IReadOnlyList<TextSpan>? spans, int index)
    {
        foreach (TextSpan span in spans ?? [])
        {
            if (index >= span.Start && index < span.End)
            {
                return span;
            }
        }

        return null;
    }

    /// <summary>Acota al texto, quita los vacíos o sin formato, ordena, recorta solapes (gana el que empieza después) y une vecinos iguales.</summary>
    public static List<TextSpan>? Normalize(IEnumerable<TextSpan>? spans, int textLength)
    {
        var result = new List<TextSpan>();
        foreach (TextSpan raw in (spans ?? []).OrderBy(s => s.Start))
        {
            int start = Math.Clamp(raw.Start, 0, textLength);
            int end = Math.Clamp(raw.End, start, textLength);
            TextSpan span = raw with { Start = start, Length = end - start, Scale = raw.Scale is { } scale ? Math.Clamp(scale, MinScale, MaxScale) : null };
            if (span.Length <= 0 || span.IsPlain)
            {
                continue;
            }

            if (result.Count > 0 && result[^1].End > span.Start)
            {
                TextSpan last = result[^1];
                result[^1] = last with { Length = span.Start - last.Start };
                if (result[^1].Length <= 0)
                {
                    result.RemoveAt(result.Count - 1);
                }
            }

            if (result.Count > 0 && result[^1].End == span.Start && result[^1].SameFormat(span))
            {
                result[^1] = result[^1] with { Length = result[^1].Length + span.Length };
            }
            else
            {
                result.Add(span);
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Mueve los tramos de <paramref name="oldText"/> a <paramref name="newText"/> tras una edición en un solo lugar (lo
    /// que cambia entre el prefijo y el sufijo comunes). Lo que se escribe al final de un tramo lo continúa; lo que se
    /// escribe justo antes de su comienzo, no. Lo borrado se va con sus tramos.
    /// </summary>
    public static List<TextSpan>? Remap(string oldText, string newText, IReadOnlyList<TextSpan>? spans)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        if (spans is not { Count: > 0 } || oldText == newText)
        {
            return Normalize(spans, newText.Length);
        }

        int prefix = 0;
        int max = Math.Min(oldText.Length, newText.Length);
        while (prefix < max && oldText[prefix] == newText[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < max - prefix && oldText[^(suffix + 1)] == newText[^(suffix + 1)])
        {
            suffix++;
        }

        int oldEnd = oldText.Length - suffix;
        int newEnd = newText.Length - suffix;
        int delta = newText.Length - oldText.Length;
        bool insertion = oldEnd == prefix;

        int MapStart(int x) => x < prefix ? x
            : x > oldEnd ? x + delta
            : x == prefix && !insertion ? prefix
            : newEnd;

        int MapEnd(int x) => x < prefix ? x
            : x > oldEnd ? x + delta
            : x == oldEnd ? newEnd
            : prefix;

        var moved = new List<TextSpan>(spans.Count);
        foreach (TextSpan span in spans)
        {
            int start = MapStart(span.Start);
            int end = MapEnd(span.End);
            if (end > start)
            {
                moved.Add(span with { Start = start, Length = end - start });
            }
        }

        return Normalize(moved, newText.Length);
    }

    /// <summary>Los tramos de un trozo del texto (<c>[start, end)</c>), con las posiciones relativas a ese trozo.</summary>
    public static List<TextSpan>? Slice(IReadOnlyList<TextSpan>? spans, int start, int end)
    {
        var result = new List<TextSpan>();
        foreach (TextSpan span in spans ?? [])
        {
            int from = Math.Max(span.Start, start);
            int to = Math.Min(span.End, end);
            if (to > from)
            {
                result.Add(span with { Start = from - start, Length = to - from });
            }
        }

        return Normalize(result, end - start);
    }
}
