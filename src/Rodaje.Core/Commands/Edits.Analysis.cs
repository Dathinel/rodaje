using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

public static partial class Edits
{
    /// <summary>«Quitar silencios»: un solo paso del historial, con ripple o dejando los huecos.</summary>
    public static SequenceEditCommand RemoveSilences(
        string sequenceId,
        string clipId,
        IReadOnlyList<(Time Start, Time End)> spans,
        bool ripple,
        RippleScope scope = RippleScope.Track,
        IIdGenerator? ids = null)
    {
        (Time, Time)[] copy = [.. spans];
        return new("Quitar silencios", sequenceId, ctx => AnalysisEdits.RemoveSpans(ctx, clipId, copy, ripple, scope), ids);
    }

    /// <summary>Marcadores en los pulsos detectados.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los marcadores nuevos.</remarks>
    public static SequenceEditCommand AddBeatMarkers(string sequenceId, IEnumerable<Time> beats, string name = "Pulso", IIdGenerator? ids = null)
    {
        Time[] copy = [.. beats];
        return new("Marcadores de ritmo", sequenceId, ctx => AnalysisEdits.AddMarkers(ctx, copy, name), ids);
    }

    /// <summary>«Cortar al ritmo»: corta el clip en los pulsos que caen dentro.</summary>
    public static SequenceEditCommand CutToBeats(string sequenceId, string clipId, IEnumerable<Time> beats, IIdGenerator? ids = null)
    {
        Time[] copy = [.. beats];
        return new("Cortar al ritmo", sequenceId, ctx => AnalysisEdits.SplitAt(ctx, clipId, copy), ids);
    }

    /// <summary>Bajar música bajo la voz (ducking): la misma envolvente en cada clip de música, en un solo paso.</summary>
    public static SequenceEditCommand Duck(
        string sequenceId,
        IEnumerable<string> musicClipIds,
        IReadOnlyList<(Time At, double OffsetDb)> envelope,
        IIdGenerator? ids = null)
    {
        string[] clips = CopySelection(musicClipIds);
        (Time, double)[] copy = [.. envelope];
        return new("Bajar música bajo la voz", sequenceId, ctx =>
        {
            foreach (string clip in clips)
            {
                AnalysisEdits.ApplyGainOffsets(ctx, clip, copy);
            }
        }, ids);
    }
}
