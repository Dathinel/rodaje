using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.Core.Commands;

public static partial class Edits
{
    /// <summary>Pone una transcripción como subtítulos: un solo paso del historial, aunque sean cientos de clips.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los clips en orden y, al final, la pista si se creó.</remarks>
    public static SequenceEditCommand AddSubtitles(
        string sequenceId,
        IReadOnlyList<SpeechSegment> segments,
        ClipGenerator template,
        SubtitleLayout? layout = null,
        string newTrackName = "Subtítulos",
        IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(template);
        List<SpeechSegment> copy = [.. segments];
        ClipGenerator style = template.Clone();
        return new("Añadir subtítulos", sequenceId, ctx => SubtitleEdits.AddSubtitles(ctx, copy, style, layout, newTrackName), ids);
    }
}
