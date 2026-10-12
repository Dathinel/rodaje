using System.Text.Json.Serialization;

namespace Rodaje.Module.Subtitles;

/// <summary>Resultado de <c>subtitulos</c>; tiempos en segundos del audio recibido (formato en el README del módulo).</summary>
public sealed record Transcript(string Language, string Device, IReadOnlyList<TranscriptSegment> Segments);

public sealed record TranscriptSegment(double Start, double End, string Text, IReadOnlyList<TranscriptWord> Words);

/// <summary>Una palabra; <paramref name="P"/> es la probabilidad media de sus tokens.</summary>
public sealed record TranscriptWord(double Start, double End, string Text, double P);

/// <summary>Un token de Whisper con sus tiempos en segundos; <paramref name="Dtw"/> es negativo si no hay DTW.</summary>
internal readonly record struct TokenTiming(string Text, double Start, double End, double Dtw, double P);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Transcript))]
internal sealed partial class TranscriptJson : JsonSerializerContext;

/// <summary>Agrupa los tokens de un segmento en palabras con tiempos.</summary>
internal static class WordBuilder
{
    /// <summary>
    /// Una palabra empieza en el token que trae espacio delante (o en el primero). Su inicio es el DTW de su primer token
    /// (o el inicio por marcas si no hay DTW) y su fin, el inicio de la siguiente acotado por el fin del último token.
    /// Todo queda dentro de [<paramref name="segmentStart"/>, <paramref name="segmentEnd"/>] y en orden.
    /// </summary>
    public static List<TranscriptWord> Build(IReadOnlyList<TokenTiming> tokens, double segmentStart, double segmentEnd, string segmentText)
    {
        List<(string Text, double Start, double LastEnd, double PSum, int Count)> raw = [];
        foreach (TokenTiming t in tokens)
        {
            if (IsSpecial(t.Text) || t.Text.Length == 0)
            {
                continue;
            }

            double start = t.Dtw >= 0 ? t.Dtw : t.Start;
            if (raw.Count == 0 || char.IsWhiteSpace(t.Text[0]))
            {
                raw.Add((t.Text, start, t.End, t.P, 1));
            }
            else
            {
                var last = raw[^1];
                raw[^1] = (last.Text + t.Text, last.Start, t.End, last.PSum + t.P, last.Count + 1);
            }
        }

        raw.RemoveAll(w => string.IsNullOrWhiteSpace(w.Text));

        // Los tokens pueden partir un carácter UTF-8; si el texto del segmento tiene las mismas palabras, manda él.
        string[] fromSegment = segmentText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool useSegment = fromSegment.Length == raw.Count;

        List<TranscriptWord> words = new(raw.Count);
        double floor = segmentStart;
        for (int i = 0; i < raw.Count; i++)
        {
            double start = Math.Clamp(raw[i].Start, floor, segmentEnd);
            double next = i + 1 < raw.Count ? Math.Clamp(raw[i + 1].Start, start, segmentEnd) : segmentEnd;
            double end = raw[i].LastEnd > start && raw[i].LastEnd < next ? raw[i].LastEnd : next;
            string text = useSegment ? fromSegment[i] : raw[i].Text.Trim();
            words.Add(new TranscriptWord(Round(start), Round(end), text, Math.Round(raw[i].PSum / raw[i].Count, 3)));
            floor = start;
        }

        return words;
    }

    /// <summary>Marcas especiales de whisper.cpp: <c>[_BEG_]</c>, <c>[_TT_150]</c>, <c>&lt;|endoftext|&gt;</c>…</summary>
    public static bool IsSpecial(string text) =>
        (text.StartsWith("[_", StringComparison.Ordinal) && text.EndsWith(']')) || text.StartsWith("<|", StringComparison.Ordinal);

    public static double Round(double seconds) => Math.Round(seconds, 3);
}
