using System.Text;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Una palabra del documento, ya en tiempo de la secuencia.</summary>
/// <param name="SeqStart">Inicio en la secuencia.</param>
/// <param name="SeqEnd">Fin en la secuencia (no menor que el inicio, también en reversa).</param>
/// <param name="Text">Texto de la palabra.</param>
/// <param name="ClipId">Clip de la pista guía que la muestra.</param>
/// <param name="TranscriptId">Transcripción de la que sale.</param>
/// <param name="WordIndex">Índice en <see cref="MediaTranscript.Words"/>.</param>
/// <param name="SpeakerId">Hablante, si lo tiene.</param>
public readonly record struct DocWord(
    Time SeqStart, Time SeqEnd, string Text, string ClipId, string TranscriptId, int WordIndex, string? SpeakerId);

/// <summary>Párrafo del documento: las palabras <c>[FirstWord, FirstWord + Count)</c> de <see cref="TranscriptDocument.Words"/>.</summary>
public readonly record struct DocParagraph(int FirstWord, int Count, string ClipId, string? SpeakerId);

/// <summary>
/// Lo que se lee de una pista de la secuencia (D-061): las palabras de las transcripciones de sus clips, proyectadas
/// por el <see cref="ClipTimeMap"/> de cada uno y en orden de la secuencia. Es una función pura de la secuencia y las
/// transcripciones: tras cada edición se vuelve a construir.
/// </summary>
public sealed class TranscriptDocument
{
    /// <summary>Pausa que abre párrafo.</summary>
    public static readonly Time ParagraphPause = Time.FromSeconds(1.5);

    /// <summary>Palabras como mucho por párrafo (más, se parte): la vista no virtualiza dentro de un párrafo.</summary>
    public const int MaxParagraphWords = 400;

    // Una palabra más larga que esto entraría tarde en la búsqueda binaria; Whisper no da palabras de más de 30 s.
    private static readonly Time LongestWord = Time.FromSeconds(60);

    private readonly Time[] _starts;
    private (string Text, int[] Offsets)? _folded;

    private TranscriptDocument(string? trackId, List<DocWord> words, List<DocParagraph> paragraphs)
    {
        TrackId = trackId;
        Words = words;
        Paragraphs = paragraphs;
        _starts = [.. words.Select(w => w.SeqStart)];
    }

    /// <summary>Documento sin palabras.</summary>
    public static TranscriptDocument Empty { get; } = new(null, [], []);

    /// <summary>Pista guía leída; nula si no hay ninguna con clips transcritos.</summary>
    public string? TrackId { get; }

    public IReadOnlyList<DocWord> Words { get; }

    public IReadOnlyList<DocParagraph> Paragraphs { get; }

    /// <summary>
    /// Construye el documento de <paramref name="guideTrackId"/> o, si es nulo, de <see cref="DefaultGuideTrack"/>.
    /// Una palabra entra en un clip si su centro cae en <c>[SourceIn, SourceOut)</c>; los congelados, las imágenes fijas,
    /// los generadores y los compuestos no muestran texto.
    /// </summary>
    public static TranscriptDocument Build(Project project, Sequence sequence, string? guideTrackId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        guideTrackId ??= DefaultGuideTrack(project, sequence);
        if (guideTrackId is null || sequence.FindTrack(guideTrackId) is not { } track)
        {
            return Empty;
        }

        var words = new List<DocWord>();
        var paragraphs = new List<DocParagraph>();
        foreach (Clip clip in track.Clips.OrderBy(c => c.Start))
        {
            if (TranscriptOf(project, clip) is not { } transcript)
            {
                continue;
            }

            ClipTimeMap map = clip.TimeMap;
            int first = words.Count;
            List<SpokenWord> source = transcript.Words;
            for (int i = FirstCandidate(source, clip.SourceIn); i < source.Count; i++)
            {
                SpokenWord word = source[i];
                if (word.Start >= clip.SourceOut)
                {
                    break;
                }

                Time center = word.Start + Time.FromTicks((word.End - word.Start).Ticks / 2);
                if (center < clip.SourceIn || center >= clip.SourceOut)
                {
                    continue;
                }

                (Time x, Time y) = map.IsIdentity
                    ? (Time.Max(clip.Start, clip.Start + (word.Start - clip.SourceIn)), Time.Min(clip.End, clip.Start + (word.End - clip.SourceIn)))
                    : (map.TimelineOf(word.Start), map.TimelineOf(word.End));
                words.Add(new DocWord(Time.Min(x, y), Time.Max(x, y), word.Text, clip.Id, transcript.Id, i, word.SpeakerId));
            }

            if (map.Reverse)
            {
                words.Sort(first, words.Count - first, Comparer<DocWord>.Create((a, b) => a.SeqStart.CompareTo(b.SeqStart)));
            }

            AddParagraphs(words, paragraphs, first, transcript, map.Reverse);
        }

        return new TranscriptDocument(track.Id, words, paragraphs);
    }

    /// <summary>La primera pista de audio (después, de video) con algún clip transcrito; nula si no hay.</summary>
    public static string? DefaultGuideTrack(Project project, Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        return project.Transcripts.Count == 0 ? null
            : sequence.AudioTracks.Concat(sequence.VideoTracks)
                .FirstOrDefault(track => track.Clips.Exists(clip => TranscriptOf(project, clip) is not null))?.Id;
    }

    /// <summary>Índice de la última palabra que ya empezó en <paramref name="time"/>; −1 si ninguna.</summary>
    public int ActiveWord(Time time)
    {
        int lo = 0, hi = _starts.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_starts[mid] <= time)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>
    /// Apariciones de <paramref name="query"/> sin distinguir mayúsculas, tildes ni signos de puntuación, como rangos de
    /// palabras <c>(First, Count)</c> en orden y sin solaparse.
    /// </summary>
    public IReadOnlyList<(int First, int Count)> Find(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        string needle = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Fold).Where(w => w.Length > 0));
        var found = new List<(int First, int Count)>();
        if (needle.Length == 0)
        {
            return found;
        }

        (string text, int[] offsets) = _folded ??= FoldWords(Words);
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            int first = WordAt(offsets, at), last = WordAt(offsets, at + needle.Length - 1);
            found.Add((first, last - first + 1));
        }

        return found;
    }

    private static MediaTranscript? TranscriptOf(Project project, Clip clip)
    {
        if (clip.Generator is not null || clip.IsCompound || clip.MediaId.Length == 0 || clip.Speed?.Freeze == true)
        {
            return null;
        }

        return project.FindTranscript(clip.MediaId) is { } transcript && project.FindMedia(clip.MediaId) is { IsStillImage: false }
            ? transcript : null;
    }

    // Las palabras van en orden de inicio y ninguna dura más que el medio: basta saltar las que terminan antes del clip.
    private static int FirstCandidate(List<SpokenWord> words, Time sourceIn)
    {
        int lo = 0, hi = words.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (words[mid].Start < sourceIn - LongestWord)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static void AddParagraphs(List<DocWord> words, List<DocParagraph> paragraphs, int first, MediaTranscript transcript, bool reverse)
    {
        int start = first;
        for (int i = first + 1; i <= words.Count; i++)
        {
            bool split = i == words.Count
                || words[i].SpeakerId != words[i - 1].SpeakerId
                || (!reverse && transcript.Words[words[i].WordIndex].ParagraphStart)
                || words[i].SeqStart - words[i - 1].SeqEnd > ParagraphPause
                || i - start >= MaxParagraphWords;
            if (split && i > start)
            {
                paragraphs.Add(new DocParagraph(start, i - start, words[start].ClipId, words[start].SpeakerId));
                start = i;
            }
        }
    }

    private static int WordAt(int[] offsets, int offset)
    {
        int index = Array.BinarySearch(offsets, offset);
        return index >= 0 ? index : ~index - 1;
    }

    // Texto de todo el documento, la primera vez que se busca: cada palabra plegada y separada por un espacio, con dónde
    // empieza cada una.
    private static (string Text, int[] Offsets) FoldWords(IReadOnlyList<DocWord> words)
    {
        var text = new StringBuilder();
        int[] offsets = new int[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            if (i > 0)
            {
                text.Append(' ');
            }

            offsets[i] = text.Length;
            FoldInto(text, words[i].Text);
        }

        return (text.ToString(), offsets);
    }

    // Tabla propia: la app corre con InvariantGlobalization y no puede contar con la normalización Unicode.
    private static string Fold(string word) => FoldInto(new StringBuilder(word.Length), word).ToString();

    private static StringBuilder FoldInto(StringBuilder folded, string word)
    {
        foreach (char c in word)
        {
            if (char.IsLetterOrDigit(c))
            {
                folded.Append(char.ToLowerInvariant(c) switch
                {
                    'á' or 'à' or 'â' or 'ä' or 'ã' => 'a',
                    'é' or 'è' or 'ê' or 'ë' => 'e',
                    'í' or 'ì' or 'î' or 'ï' => 'i',
                    'ó' or 'ò' or 'ô' or 'ö' or 'õ' => 'o',
                    'ú' or 'ù' or 'û' or 'ü' => 'u',
                    'ñ' => 'n',
                    'ç' => 'c',
                    char lower => lower,
                });
            }
        }

        return folded;
    }
}
