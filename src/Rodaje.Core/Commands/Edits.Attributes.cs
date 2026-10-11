using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.Core.Commands;

/// <summary>Separar el audio de un clip y pegar atributos copiados de otro.</summary>
public static partial class Edits
{
    /// <summary>Crea el clip de audio enlazado de un clip de video (<see cref="TimelineEdits.DetachAudio"/>).</summary>
    public static SequenceEditCommand DetachAudio(string sequenceId, string clipId, string? audioTrackId = null, IIdGenerator? ids = null) =>
        new("Separar audio", sequenceId, ctx => (IReadOnlyList<string>)[TimelineEdits.DetachAudio(ctx, clipId, audioTrackId)], ids);
}
