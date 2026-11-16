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
        EdlList list = EdlList.FromSequence(project, sequence, options, report);
        return list.ToDraft(options.FrameRate ?? sequence.FrameRate, null, options.Limits, new InterchangeReport());
    }
}
