using System.Globalization;
using Rodaje.Media.Proxies;

namespace Rodaje.Media.Export;

/// <summary>Qué pasó al probar un codificador.</summary>
public enum ExportProbeStatus
{
    /// <summary>Está en el FFmpeg y codificó la prueba sin error.</summary>
    Works,

    /// <summary>Este FFmpeg no lo trae. No cambia hasta que cambie el exe: no se reintenta.</summary>
    NotCompiled,

    /// <summary>
    /// Está en el FFmpeg pero la prueba falló: falta el hardware o el controlador (lo normal), o algo pasajero (la GPU
    /// ocupada, un proceso que no respondió, el exe que no se pudo leer). Se reintenta pasado un rato.
    /// </summary>
    Failed,
}

/// <summary>Resultado de probar un codificador en esta máquina.</summary>
/// <param name="Status">Qué pasó.</param>
/// <param name="Reason">Motivo en español cuando no funciona: las líneas útiles del error de FFmpeg.</param>
public sealed record ExportProbeResult(ExportProbeStatus Status, string? Reason = null)
{
    public static ExportProbeResult Ok { get; } = new(ExportProbeStatus.Works);

    public bool Works => Status == ExportProbeStatus.Works;

    public static ExportProbeResult NotCompiled(string reason) => new(ExportProbeStatus.NotCompiled, reason);

    public static ExportProbeResult Fail(string reason) => new(ExportProbeStatus.Failed, reason);
}

/// <summary>Comprueba si un codificador funciona. Se sustituye por un doble en las pruebas.</summary>
public interface IExportEncoderProbe
{
    /// <summary>Debe poder llamarse desde varios hilos a la vez.</summary>
    ExportProbeResult Probe(EncoderSpec encoder);

    /// <summary>Olvida lo que la sonda guarde (la lista de codificadores del exe); la próxima prueba la vuelve a leer.</summary>
    void Reset()
    {
    }
}

/// <summary>
/// Sonda real: pregunta a <c>ffmpeg.exe</c> qué codificadores trae y codifica 1 s de prueba con cada uno, con su propio
/// formato de píxel y su perfil por defecto (o 1 s de audio a su frecuencia).
/// </summary>
/// <remarks>
/// Usa el exe LGPL ya verificado de los proxies (<see cref="FfmpegExecutable"/>). No reutiliza
/// <see cref="FfmpegExecutable.ProbeEncoder"/>: esa prueba es la de los proxies (H.264 con <c>yuv420p</c> o <c>nv12</c>,
/// cacheada para siempre) y no sirve para DNxHR, ProRes o el audio; unificarlas toca <c>Proxies/</c>, fuera de esta tarea.
/// </remarks>
public sealed class FfmpegExportEncoderProbe : IExportEncoderProbe
{
    internal const int TimedOutExitCode = -1;

    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<IReadOnlyList<string>, TimeSpan, (int ExitCode, string Output, string Error)> _run;
    private readonly TimeSpan _retryDelay;
    private readonly object _gate = new();
    private HashSet<string>? _compiled;

    public FfmpegExportEncoderProbe(FfmpegExecutable ffmpeg)
        : this(Runner(ffmpeg), TimeSpan.FromMilliseconds(500))
    {
    }

    /// <summary>Con otro ejecutor de procesos, para las pruebas.</summary>
    internal FfmpegExportEncoderProbe(Func<IReadOnlyList<string>, TimeSpan, (int ExitCode, string Output, string Error)> run, TimeSpan retryDelay)
    {
        ArgumentNullException.ThrowIfNull(run);
        _run = run;
        _retryDelay = retryDelay;
    }

    public ExportProbeResult Probe(EncoderSpec encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        try
        {
            HashSet<string>? compiled = Compiled(out string? listProblem);
            if (compiled is null)
            {
                return ExportProbeResult.Fail(listProblem!);
            }

            if (!compiled.Contains(encoder.Id))
            {
                return ExportProbeResult.NotCompiled($"{encoder.Id} no está compilado en este FFmpeg.");
            }

            IReadOnlyList<string> arguments = ProbeArguments(encoder);
            (int exitCode, _, string error) = _run(arguments, ProbeTimeout);
            if (exitCode == TimedOutExitCode)
            {
                // Lo único claramente pasajero: un proceso que no respondió (la GPU ocupada, el antivirus). Una vez más.
                Thread.Sleep(_retryDelay);
                (exitCode, _, error) = _run(arguments, ProbeTimeout);
            }

            return exitCode == 0
                ? ExportProbeResult.Ok
                : ExportProbeResult.Fail($"{encoder.Id} no funciona en este equipo: {UsefulLines(error, encoder.Id)}");
        }
        catch (FfmpegNotFoundException ex)
        {
            return ExportProbeResult.Fail(ex.Message);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _compiled = null;
        }
    }

    /// <summary>
    /// 1 s de prueba: gris de 640x360 a 25 cuadros por segundo con el primer formato de píxel del codificador y el primer
    /// perfil que lo admite, al preset más rápido; o un tono de 1 s a la primera frecuencia admitida, en estéreo.
    /// </summary>
    internal static IReadOnlyList<string> ProbeArguments(EncoderSpec encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        List<string> args = ["-hide_banner", "-nostdin", "-loglevel", "error"];
        if (encoder.IsAudio)
        {
            int rate = encoder.SampleRates.Count > 0 ? encoder.SampleRates[0] : 48_000;
            string hz = rate.ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate={hz}:duration=1", "-c:a", encoder.Id, .. encoder.BaseArguments]);
            if (encoder.Codec is not (ExportCodec.Pcm or ExportCodec.Flac or ExportCodec.Alac))
            {
                // Sin esto FFmpeg pide su tasa por defecto, que el AAC de Windows no admite (solo 96/128/160/192).
                args.AddRange(["-b:a", "192k"]);
            }

            args.AddRange(["-ar", hz, "-ac", encoder.MaxChannels is > 0 and < 2 ? "1" : "2"]);
        }
        else
        {
            args.AddRange(["-f", "lavfi", "-i", "color=c=gray:s=640x360:r=25:d=1", "-c:v", encoder.Id, .. encoder.BaseArguments]);
            if (encoder.PixelFormats.Count > 0)
            {
                ExportPixelFormat pixels = encoder.PixelFormats[0];
                if (encoder.Profiles.FirstOrDefault(p => p.Allows(pixels)) is { } profile)
                {
                    args.AddRange(["-profile:v", profile.Id]);
                }

                args.AddRange(["-pix_fmt", pixels.FfmpegName()]);
            }

            if (encoder.SpeedPresets.Count > 0)
            {
                args.AddRange(["-preset", encoder.SpeedPresets[0]]);
            }
        }

        args.AddRange(["-f", "null", "-"]);
        return args;
    }

    /// <summary>Ids de codificadores de video y de audio de la salida de <c>ffmpeg -encoders</c>.</summary>
    internal static HashSet<string> ParseEncoderList(string output)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n'))
        {
            // " V....D libsvtav1   SVT-AV1 …": banderas de 6 caracteres que empiezan por V (video) o A (audio). La leyenda
            // (« V..... = Video») se descarta por el «=».
            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] is 'V' or 'A' && parts[1] != "=")
            {
                ids.Add(parts[1]);
            }
        }

        return ids;
    }

    /// <summary>
    /// Lo que explica el fallo: primero los mensajes del propio codificador (<c>[h264_nvenc @ …] No capable devices
    /// found</c>), sin la dirección; si no hay, las líneas que no son el eco genérico de FFmpeg. Como mucho tres.
    /// </summary>
    internal static string UsefulLines(string error, string encoderId)
    {
        string[] lines = error.Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string own = $"[{encoderId} @";
        string[] fromEncoder = [.. lines.Where(l => l.StartsWith(own, StringComparison.Ordinal)).Select(StripContext).Distinct(StringComparer.Ordinal)];
        string[] chosen = fromEncoder.Length > 0
            ? fromEncoder
            : [.. lines.Where(l => !IsGenericNoise(l)).Select(StripContext).Distinct(StringComparer.Ordinal)];
        if (chosen.Length == 0)
        {
            chosen = [.. lines.Select(StripContext)];
        }

        return chosen.Length == 0 ? "sin mensaje de error." : string.Join(" | ", chosen.Take(3));
    }

    private static bool IsGenericNoise(string line) =>
        line.Contains("Conversion failed", StringComparison.Ordinal)
        || line.Contains("Task finished with error code", StringComparison.Ordinal)
        || line.Contains("Terminating thread with return code", StringComparison.Ordinal)
        || line.Contains("Nothing was written into output file", StringComparison.Ordinal)
        || line.Contains("Error sending frames to consumers", StringComparison.Ordinal)
        || line.Contains("Could not open encoder before EOF", StringComparison.Ordinal)
        || line.Contains("Error while filtering", StringComparison.Ordinal)
        || line.StartsWith("Error opening output file ", StringComparison.Ordinal);

    // «[h264_nvenc @ 000001d2] mensaje» → «mensaje».
    private static string StripContext(string line)
    {
        if (line.StartsWith('[') && line.IndexOf("] ", StringComparison.Ordinal) is var end and > 0)
        {
            return line[(end + 2)..];
        }

        return line;
    }

    // La lista solo se guarda si se leyó bien: un fallo al leerla (código distinto de 0, salida vacía, exe que no
    // arranca) se reintenta en la siguiente prueba en vez de dar todo por «no compilado».
    private HashSet<string>? Compiled(out string? problem)
    {
        lock (_gate)
        {
            if (_compiled is not null)
            {
                problem = null;
                return _compiled;
            }
        }

        (int exitCode, string output, string error) = _run(["-hide_banner", "-encoders"], ListTimeout);
        HashSet<string> ids = exitCode == 0 ? ParseEncoderList(output) : [];
        if (ids.Count == 0)
        {
            problem = exitCode == 0
                ? "ffmpeg -encoders no listó ningún codificador."
                : string.Create(CultureInfo.InvariantCulture,
                    $"ffmpeg -encoders terminó con código {exitCode}: {UsefulLines(error, "-")}");
            return null;
        }

        lock (_gate)
        {
            _compiled ??= ids;
            problem = null;
            return _compiled;
        }
    }

    // Cada vez comprueba que el exe sigue siendo el verificado (y lo vuelve a verificar si cambió).
    private static Func<IReadOnlyList<string>, TimeSpan, (int, string, string)> Runner(FfmpegExecutable ffmpeg)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        return (arguments, timeout) => ffmpeg.EnsureUnchanged().Run(arguments, timeout);
    }
}
