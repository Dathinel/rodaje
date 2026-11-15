using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

/// <remarks>
/// Ediciones desde el texto (D-061). Los índices de palabra son los del <see cref="TranscriptDocument"/> de
/// <c>guideTrackId</c> (nulo: la pista guía por omisión), que se vuelve a construir al aplicar: es el mismo que ve el panel.
/// </remarks>
public static partial class Edits
{
    /// <summary>«Borrar texto»: quita las palabras con ripple o dejando hueco, en un solo paso del historial.</summary>
    public static SequenceEditCommand DeleteWords(string sequenceId, string? guideTrackId, IEnumerable<int> words, bool ripple,
        RippleScope scope = RippleScope.Track, Func<Time, Time, Time>? cutPoint = null, IIdGenerator? ids = null) =>
        RemoveWords("Borrar texto", sequenceId, guideTrackId, words, ripple, scope, cutPoint, ids);

    /// <summary>«Quitar muletillas»: como <see cref="DeleteWords"/> con las muletillas elegidas.</summary>
    public static SequenceEditCommand RemoveFillers(string sequenceId, string? guideTrackId, IEnumerable<int> words, bool ripple,
        RippleScope scope = RippleScope.Track, Func<Time, Time, Time>? cutPoint = null, IIdGenerator? ids = null) =>
        RemoveWords("Quitar muletillas", sequenceId, guideTrackId, words, ripple, scope, cutPoint, ids);

    /// <summary>«Mover frase»: corta, borra con ripple y pega insertando delante de la palabra <paramref name="before"/>.</summary>
    public static SequenceEditCommand MoveWords(string sequenceId, string? guideTrackId, int first, int count, int before,
        RippleScope scope = RippleScope.Track, IIdGenerator? ids = null) =>
        new("Mover frase", sequenceId, ctx => TranscriptEdits.MoveWords(ctx, Document(ctx, guideTrackId), first, count, before, scope), ids);

    /// <summary>«Quitar pausas»: deja <paramref name="keep"/> de cada pausa de al menos <paramref name="minimum"/>.</summary>
    public static SequenceEditCommand RemovePauses(string sequenceId, string? guideTrackId, Time minimum, Time keep, bool ripple,
        RippleScope scope = RippleScope.Track, IIdGenerator? ids = null) =>
        new("Quitar pausas", sequenceId, ctx => { TranscriptEdits.RemovePauses(ctx, Document(ctx, guideTrackId), minimum, keep, ripple, scope); }, ids);

    private static SequenceEditCommand RemoveWords(string name, string sequenceId, string? guideTrackId, IEnumerable<int> words, bool ripple,
        RippleScope scope, Func<Time, Time, Time>? cutPoint, IIdGenerator? ids)
    {
        int[] copy = [.. words];
        return new(name, sequenceId, ctx => { TranscriptEdits.DeleteWords(ctx, Document(ctx, guideTrackId), copy, ripple, scope, cutPoint); }, ids);
    }

    private static TranscriptDocument Document(EditContext ctx, string? guideTrackId) =>
        TranscriptDocument.Build(ctx.Project, ctx.Sequence, guideTrackId);
}
