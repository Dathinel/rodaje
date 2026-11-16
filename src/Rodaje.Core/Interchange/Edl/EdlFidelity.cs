using Rodaje.Core.Model;

namespace Rodaje.Core.Interchange.Edl;

/// <summary>
/// Lo que sobrevive en una EDL: la misma lista que escribe <see cref="EdlWriter"/>, leída como la lee <see cref="EdlReader"/>
/// (una pista de video, dos de audio, medios provisionales con el TC de origen en sus clips, transiciones al inicio).
/// </summary>
internal static class EdlFidelity
{
    public static TimelineDraft Project(Model.Project project, Sequence sequence, InterchangeOptions options, InterchangeReport report)
    {
        // Cada pista de video va en su EDL: el informe cuenta lo que pierden todas, el resultado es la primera.
        IReadOnlyList<int> tracks = options.VideoTracks is { Count: > 0 } chosen ? chosen : [.. Enumerable.Range(0, Math.Max(1, sequence.VideoTracks.Count))];
        EdlList list = EdlList.FromSequence(project, sequence, Single(options, tracks[0], true), report);
        foreach (int track in tracks.Skip(1))
        {
            EdlList.FromSequence(project, sequence, Single(options, track, false), report);
        }

        return list.ToDraft(options.FrameRate ?? sequence.FrameRate, null, options.Limits, new InterchangeReport());
    }

    internal static InterchangeOptions Single(InterchangeOptions options, int track, bool first) => new()
    {
        FrameRate = options.FrameRate,
        DefaultStartTimecode = options.DefaultStartTimecode,
        VideoTracks = [track],
        IncludeAudio = options.IncludeAudio && first,
        LongReelNames = options.LongReelNames,
        Limits = options.Limits,
    };
}
