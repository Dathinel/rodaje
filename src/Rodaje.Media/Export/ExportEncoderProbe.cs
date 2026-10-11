using Rodaje.Media.Proxies;

namespace Rodaje.Media.Export;

/// <summary>Resultado de probar un codificador en esta máquina.</summary>
/// <param name="Works">Si está compilado en FFmpeg y codificó la prueba sin error.</param>
/// <param name="Reason">Motivo en español cuando <paramref name="Works"/> es falso.</param>
public sealed record ExportProbeResult(bool Works, string? Reason = null)
{
    public static ExportProbeResult Ok { get; } = new(true);

    public static ExportProbeResult Fail(string reason) => new(false, reason);
}

/// <summary>Comprueba si un codificador funciona. Se puede sustituir por un doble en las pruebas.</summary>
public interface IExportEncoderProbe
{
    /// <summary>Debe poder llamarse desde varios hilos a la vez.</summary>
    ExportProbeResult Probe(ExportEncoderInfo encoder);
}

/// <summary>
/// Sonda real: pregunta a <c>ffmpeg.exe</c> qué codificadores trae y codifica 1 s de prueba con cada uno.
/// </summary>
public sealed class FfmpegExportEncoderProbe : IExportEncoderProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    private readonly FfmpegExecutable _ffmpeg;
    private readonly Lazy<HashSet<string>> _compiled;

    public FfmpegExportEncoderProbe(FfmpegExecutable ffmpeg)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        _ffmpeg = ffmpeg;
        _compiled = new Lazy<HashSet<string>>(() => ParseEncoderList(
            _ffmpeg.Run(["-hide_banner", "-encoders"], TimeSpan.FromSeconds(20)).Output));
    }

    public ExportProbeResult Probe(ExportEncoderInfo encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        try
        {
            if (!_compiled.Value.Contains(encoder.Id))
            {
                return ExportProbeResult.Fail($"{encoder.Id} no está compilado en este FFmpeg.");
            }

            (int exitCode, _, string error) = _ffmpeg.Run(ProbeArguments(encoder), ProbeTimeout);
            return exitCode == 0
                ? ExportProbeResult.Ok
                : ExportProbeResult.Fail($"{encoder.Id} no funciona en este equipo: {FfmpegExecutable.Tail(error)}");
        }
        catch (FfmpegNotFoundException ex)
        {
            return ExportProbeResult.Fail(ex.Message);
        }
    }

    /// <summary>1 s de video gris de 640x360 a 25 cuadros por segundo, con calidad baja y la velocidad más alta.</summary>
    internal static IReadOnlyList<string> ProbeArguments(ExportEncoderInfo encoder) =>
    [
        "-hide_banner", "-nostdin", "-loglevel", "error",
        "-f", "lavfi", "-i", "color=c=gray:s=640x360:r=25:d=1",
        "-pix_fmt", encoder.PixelFormat,
        "-c:v", encoder.Id, .. encoder.BaseArguments(ExportQuality.Draft, ExportSpeed.Fast),
        "-f", "null", "-",
    ];

    /// <summary>Ids de codificadores de video de la salida de <c>ffmpeg -encoders</c>.</summary>
    internal static HashSet<string> ParseEncoderList(string output)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n'))
        {
            // " V....D libsvtav1   SVT-AV1 ..." ; la primera columna son banderas de 6 caracteres que empiezan por V; la leyenda « V..... = Video» se descarta.
            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] == 'V' && parts[1] != "=")
            {
                ids.Add(parts[1]);
            }
        }

        return ids;
    }
}
