namespace Rodaje.Media.Export;

/// <summary>Traduce la salida de error de <c>ffmpeg.exe</c> a un <see cref="ExportErrorCode"/> y un mensaje en español.</summary>
public static class FfmpegMessages
{
    // De lo más concreto a lo más general: la primera coincidencia manda.
    private static readonly (string Needle, ExportErrorCode Code, string Message)[] Patterns =
    [
        ("No space left on device", ExportErrorCode.DiskFull, "El disco de destino se llenó."),
        ("There is not enough space on the disk", ExportErrorCode.DiskFull, "El disco de destino se llenó."),
        ("Disk quota exceeded", ExportErrorCode.DiskFull, "Se agotó la cuota del disco de destino."),
        ("Permission denied", ExportErrorCode.OutputNotWritable, "No hay permiso para escribir en la carpeta de destino."),
        ("Read-only file system", ExportErrorCode.OutputNotWritable, "La carpeta de destino es de solo lectura."),
        ("Unknown encoder", ExportErrorCode.EncoderMissing, "Este ffmpeg.exe no trae el codificador elegido."),
        ("Encoder not found", ExportErrorCode.EncoderMissing, "Este ffmpeg.exe no trae el codificador elegido."),
        ("No capable devices found", ExportErrorCode.EncoderRejected, "La GPU no ofrece este codificador (¿controlador antiguo o gráfica sin soporte?)."),
        ("OpenEncodeSessionEx failed", ExportErrorCode.EncoderRejected, "La GPU no abrió otra sesión de codificación; cierra otras grabaciones o exportaciones."),
        ("out of memory", ExportErrorCode.EncoderRejected, "El codificador se quedó sin memoria."),
        ("Cannot load nvcuda.dll", ExportErrorCode.EncoderRejected, "No hay controlador de NVIDIA para NVENC."),
        ("Provided device doesn't support required NVENC features", ExportErrorCode.EncoderRejected, "La GPU no admite estos ajustes de NVENC."),
        ("10 bit encode not supported", ExportErrorCode.EncoderRejected, "El codificador no admite 10 bits en esta GPU."),
        ("Unrecognized option", ExportErrorCode.EncoderRejected, "Este ffmpeg.exe no conoce una de las opciones del codificador."),
        ("Option not found", ExportErrorCode.EncoderRejected, "Este ffmpeg.exe no conoce una de las opciones del codificador."),
        ("Could not find tag for codec", ExportErrorCode.EncoderRejected, "El contenedor no admite este códec."),
        ("not currently supported in container", ExportErrorCode.EncoderRejected, "El contenedor no admite este códec."),
        ("Invalid pixel format", ExportErrorCode.EncoderRejected, "El codificador no admite este formato de píxel."),
        ("Specified pixel format", ExportErrorCode.EncoderRejected, "El codificador no admite este formato de píxel."),
        ("Specified sample rate", ExportErrorCode.EncoderRejected, "El codificador de audio no admite esta frecuencia de muestreo."),
        ("Specified sample format", ExportErrorCode.EncoderRejected, "El codificador de audio no admite este formato de muestras."),
        ("channel layout", ExportErrorCode.EncoderRejected, "El codificador de audio no admite esta cantidad de canales."),
        ("Invalid level", ExportErrorCode.EncoderRejected, "El codificador no admite este nivel."),
        ("Invalid profile", ExportErrorCode.EncoderRejected, "El codificador no admite este perfil."),
        ("No such file or directory", ExportErrorCode.OutputNotWritable, "No se encontró un archivo o carpeta de la exportación."),
        ("Error while opening encoder", ExportErrorCode.EncoderRejected, "El codificador rechazó los ajustes."),
        ("Error initializing output stream", ExportErrorCode.EncoderRejected, "El codificador rechazó los ajustes."),
        ("Invalid argument", ExportErrorCode.EncoderRejected, "El codificador rechazó un valor de los ajustes."),
        ("Broken pipe", ExportErrorCode.EncoderFailed, "ffmpeg.exe cerró la tubería de cuadros antes de tiempo."),
        ("Conversion failed", ExportErrorCode.EncoderFailed, "ffmpeg.exe no pudo terminar la conversión."),
    ];

    /// <summary>Código y mensaje para una salida de error de ffmpeg; el detalle técnico va al final entre paréntesis.</summary>
    /// <param name="stderr">Salida de error de ffmpeg (las últimas líneas bastan).</param>
    /// <param name="exitCode">Código de salida del proceso.</param>
    /// <param name="encoder">Codificador de video o audio en uso, para nombrarlo en el mensaje.</param>
    public static (ExportErrorCode Code, string Message) Translate(string? stderr, int exitCode, string? encoder = null)
    {
        string log = stderr ?? "";
        string detail = Detail(log);
        foreach ((string needle, ExportErrorCode code, string message) in Patterns)
        {
            if (log.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return (code, Compose(message, encoder, code, detail));
            }
        }

        string generic = exitCode == 0
            ? "ffmpeg.exe terminó sin escribir lo esperado."
            : $"ffmpeg.exe terminó con el código {exitCode}.";
        return (ExportErrorCode.EncoderFailed, Compose(generic, encoder, ExportErrorCode.EncoderFailed, detail));
    }

    /// <summary>Las últimas líneas útiles de la salida de error, en una sola línea.</summary>
    public static string Detail(string stderr, int maxLines = 4)
    {
        string[] lines = stderr.Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - maxLines)));
    }

    private static string Compose(string message, string? encoder, ExportErrorCode code, string detail)
    {
        string who = encoder is not null && code is ExportErrorCode.EncoderRejected or ExportErrorCode.EncoderMissing
            ? $" Codificador: {encoder}."
            : "";
        return detail.Length == 0 ? message + who : $"{message}{who} (ffmpeg: {detail})";
    }
}
