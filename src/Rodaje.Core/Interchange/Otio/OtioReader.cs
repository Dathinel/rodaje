namespace Rodaje.Core.Interchange.Otio;

/// <summary>Lee OpenTimelineIO; lo que no entiende va al informe y nunca rompe la lectura.</summary>
public static class OtioReader
{
    public static TimelineDraft Read(string text, string? baseDirectory, InterchangeOptions options, InterchangeReport report) =>
        throw new NotImplementedException("OpenTimelineIO: lectura pendiente (fase F4 de D-062).");
}
