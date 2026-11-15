namespace Rodaje.Core.Interchange.Otio;

/// <summary>
/// Lee OpenTimelineIO. Un elemento que no entiende se convierte en hueco o en clip sin medio y va al informe; un archivo roto o
/// que pasa de <see cref="InterchangeLimits"/> lanza <see cref="InterchangeException"/>.
/// </summary>
public static class OtioReader
{
    public static TimelineDraft Read(string text, string? baseDirectory, InterchangeOptions options, InterchangeReport report) =>
        throw new NotImplementedException("OpenTimelineIO: lectura pendiente (fase F4 de D-062).");
}
