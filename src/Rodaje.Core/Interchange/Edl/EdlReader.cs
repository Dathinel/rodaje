namespace Rodaje.Core.Interchange.Edl;

/// <summary>Lee EDL CMX 3600; lo que no entiende va al informe y nunca rompe la lectura.</summary>
public static class EdlReader
{
    public static TimelineDraft Read(string text, string? baseDirectory, InterchangeOptions options, InterchangeReport report) =>
        throw new NotImplementedException("EDL CMX 3600: lectura pendiente (fase F4 de D-062).");
}
