namespace Rodaje.Core.Interchange.Fcpxml;

/// <summary>Lee FCPXML 1.10; lo que no entiende va al informe y nunca rompe la lectura.</summary>
public static class FcpxmlReader
{
    public static TimelineDraft Read(string text, string? baseDirectory, InterchangeOptions options, InterchangeReport report) =>
        throw new NotImplementedException("FCPXML 1.10: lectura pendiente (fase F4 de D-062).");
}
