using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.Core.Commands;

public static partial class Edits
{
    /// <summary>
    /// Reemplaza el medio del clip (y el de sus enlazados con el mismo medio) conservando duración, efectos y keyframes.
    /// Antes conviene <see cref="TimelineEdits.PreviewReplaceMedia"/> para avisar si el clip se acorta.
    /// </summary>
    public static SequenceEditCommand ReplaceMedia(string sequenceId, string clipId, string mediaId, IIdGenerator? ids = null) =>
        new("Reemplazar medio", sequenceId, ctx => { TimelineEdits.ReplaceMedia(ctx, clipId, mediaId); }, ids);
}
