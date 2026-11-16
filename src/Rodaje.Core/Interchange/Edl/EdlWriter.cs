using Rodaje.Core.Model;

namespace Rodaje.Core.Interchange.Edl;

/// <summary>Escribe EDL CMX 3600 con lo que declara <see cref="Fidelity.Project"/> para este formato.</summary>
public static class EdlWriter
{
    public static IReadOnlyList<InterchangeOutput> Write(Project project, Sequence sequence, string? projectDirectory, InterchangeOptions options) =>
        throw new NotImplementedException("EDL CMX 3600: escritura pendiente (fase F4 de D-062).");
}
