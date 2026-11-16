using Rodaje.Core.Model;

namespace Rodaje.Core.Interchange.Fcpxml;

/// <summary>Escribe FCPXML 1.10 con lo que declara <see cref="Fidelity.Project"/> para este formato.</summary>
public static class FcpxmlWriter
{
    public static IReadOnlyList<InterchangeOutput> Write(Project project, Sequence sequence, string? projectDirectory, InterchangeOptions options) =>
        throw new NotImplementedException("FCPXML 1.10: escritura pendiente (fase F4 de D-062).");
}
