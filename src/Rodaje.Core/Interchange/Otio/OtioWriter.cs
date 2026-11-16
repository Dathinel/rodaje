using Rodaje.Core.Model;

namespace Rodaje.Core.Interchange.Otio;

/// <summary>Escribe OpenTimelineIO con lo que declara <see cref="Fidelity.Project"/> para este formato.</summary>
public static class OtioWriter
{
    public static IReadOnlyList<InterchangeOutput> Write(Project project, Sequence sequence, string? projectDirectory, InterchangeOptions options) =>
        throw new NotImplementedException("OpenTimelineIO: escritura pendiente (fase F4 de D-062).");
}
