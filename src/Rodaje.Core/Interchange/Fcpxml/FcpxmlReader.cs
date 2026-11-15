namespace Rodaje.Core.Interchange.Fcpxml;

/// <summary>
/// Lee FCPXML 1.10. Un elemento que no entiende se convierte en hueco o en clip sin medio y va al informe; un archivo roto o
/// que pasa de <see cref="InterchangeLimits"/> lanza <see cref="InterchangeException"/>.
/// </summary>
public static class FcpxmlReader
{
    public static TimelineDraft Read(string text, string? baseDirectory, InterchangeOptions options, InterchangeReport report) =>
        throw new NotImplementedException("FCPXML 1.10: lectura pendiente (fase F4 de D-062).");
}
