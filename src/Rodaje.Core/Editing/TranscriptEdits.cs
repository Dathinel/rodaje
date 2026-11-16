using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>
/// Ediciones desde el texto (D-061): convierten palabras de un <see cref="TranscriptDocument"/> en tramos de la
/// secuencia y los quitan o mueven con las ediciones de siempre (<see cref="AnalysisEdits.RemoveSpans"/>, pegar insertando).
/// </summary>
public static class TranscriptEdits
{
    /// <summary>
    /// Tramos de la secuencia, por clip, que ocupan las palabras dadas (índices de <see cref="TranscriptDocument.Words"/>):
    /// de la mitad del hueco con la palabra anterior a la mitad del hueco con la siguiente, o el punto que elija
    /// <paramref name="cutPoint"/> entre esos dos instantes (el más silencioso, si la app analizó el audio).
    /// </summary>
    public static IReadOnlyList<(string ClipId, Time Start, Time End)> SpansOf(
        TranscriptDocument document, IEnumerable<int> words, Func<Time, Time, Time>? cutPoint = null) =>
        throw new NotImplementedException();

    /// <summary>Quita de la secuencia las palabras dadas, con ripple (según <paramref name="scope"/>) o dejando hueco.</summary>
    /// <returns>Tramos quitados, en tiempo de la secuencia de antes.</returns>
    public static IReadOnlyList<(Time Start, Time End)> DeleteWords(
        EditContext ctx, TranscriptDocument document, IEnumerable<int> words, bool ripple, RippleScope scope,
        Func<Time, Time, Time>? cutPoint = null) =>
        throw new NotImplementedException();

    /// <summary>
    /// Mueve las palabras <c>[first, first + count)</c> delante de la palabra <paramref name="before"/> (o al final si
    /// es <c>Words.Count</c>): corta, borra con ripple y pega insertando, con los clips enlazados.
    /// </summary>
    public static void MoveWords(EditContext ctx, TranscriptDocument document, int first, int count, int before, RippleScope scope) =>
        throw new NotImplementedException();

    /// <summary>
    /// Palabras que parecen muletillas en <paramref name="language"/>: <c>Safe</c> las seguras (eh, um), que se ofrecen
    /// marcadas; las dudosas (bueno, like) también son palabras de verdad y se ofrecen sin marcar.
    /// </summary>
    public static IReadOnlyList<(int Word, bool Safe)> FillerCandidates(TranscriptDocument document, string language) =>
        throw new NotImplementedException();

    /// <summary>Huecos entre palabras seguidas del mismo clip de al menos <paramref name="minimum"/>, en tiempo de la secuencia.</summary>
    public static IReadOnlyList<(Time Start, Time End)> Pauses(TranscriptDocument document, Time minimum) =>
        throw new NotImplementedException();
}
