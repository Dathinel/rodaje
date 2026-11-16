using Rodaje.Core.Model;

namespace Rodaje.Core.Interchange.Fcpxml;

/// <summary>Lo que sobrevive en este formato: la regla de <see cref="Fidelity.Project"/>.</summary>
internal static class FcpxmlFidelity
{
    public static TimelineDraft Project(Model.Project project, Sequence sequence, InterchangeOptions options, InterchangeReport report) =>
        throw new NotImplementedException("Regla de fidelidad pendiente (fase F4 de D-062).");
}
