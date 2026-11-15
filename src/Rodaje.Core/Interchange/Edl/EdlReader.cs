namespace Rodaje.Core.Interchange.Edl;

/// <summary>
/// Lee EDL CMX 3600. Un elemento que no entiende se convierte en hueco o en clip sin medio y va al informe; un archivo roto o
/// que pasa de <see cref="InterchangeLimits"/> lanza <see cref="InterchangeException"/>.
/// </summary>
public static class EdlReader
{
    public static TimelineDraft Read(string text, string? baseDirectory, InterchangeOptions options, InterchangeReport report) =>
        throw new NotImplementedException("EDL CMX 3600: lectura pendiente (fase F4 de D-062).");
}
