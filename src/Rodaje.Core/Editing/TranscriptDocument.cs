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

    private TranscriptDocument(string? trackId, IReadOnlyList<DocWord> words, IReadOnlyList<DocParagraph> paragraphs)
    {
        TrackId = trackId;
        Words = words;
        Paragraphs = paragraphs;
    }

    /// <summary>Documento sin palabras.</summary>
    public static TranscriptDocument Empty { get; } = new(null, [], []);

    /// <summary>Pista guía leída; nula si no hay ninguna con clips transcritos.</summary>
    public string? TrackId { get; }

    public IReadOnlyList<DocWord> Words { get; }

    public IReadOnlyList<DocParagraph> Paragraphs { get; }

    /// <summary>
    /// Construye el documento de <paramref name="guideTrackId"/> o, si es nulo, de <see cref="DefaultGuideTrack"/>.
    /// </summary>
    public static TranscriptDocument Build(Project project, Sequence sequence, string? guideTrackId = null) =>
        throw new NotImplementedException();

    /// <summary>La primera pista de audio con algún clip transcrito; nula si no hay.</summary>
    public static string? DefaultGuideTrack(Project project, Sequence sequence) => throw new NotImplementedException();

    /// <summary>Índice de la última palabra que ya empezó en <paramref name="time"/>; −1 si ninguna.</summary>
    public int ActiveWord(Time time) => throw new NotImplementedException();

    /// <summary>
    /// Apariciones de <paramref name="query"/> sin distinguir mayúsculas ni tildes, como rangos de palabras
    /// <c>(First, Count)</c> en orden.
    /// </summary>
    public IReadOnlyList<(int First, int Count)> Find(string query) => throw new NotImplementedException();
}
