using Rodaje.Core.Timing;

namespace Rodaje.Media.Probe;

/// <summary>Qué archivos puede usar hoy la línea de tiempo, y por qué no los demás.</summary>
public static class MediaSupport
{
    // Formatos de FFmpeg que solo leen imágenes sueltas.
    private static readonly HashSet<string> StillImageFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "image2", "image2pipe", "png_pipe", "jpeg_pipe", "bmp_pipe", "webp_pipe", "tiff_pipe", "gif_pipe", "dpx_pipe",
        "exr_pipe", "qoi_pipe", "j2k_pipe", "jpegxl_pipe", "pam_pipe", "pgm_pipe", "ppm_pipe", "psd_pipe", "svg_pipe",
    };

    internal static bool IsStillImageDemuxer(string format) => StillImageFormats.Contains(format);

    internal static bool IsStillImageFormat(string format, string codec) => StillImageFormats.Contains(format)
        && codec is "png" or "mjpeg" or "webp" or "bmp" or "tiff";

    /// <summary>Lee el archivo; devuelve <c>null</c> si se puede usar o el motivo en español si no.</summary>
    public static string? Classify(string path, out MediaInfo? info)
    {
        try
        {
            info = MediaProbe.Probe(path);
        }
        catch (FileNotFoundException)
        {
            // Borrado entre elegirlo y leerlo (carpeta vigilada, reenlazar): la importación entera fallaba con la excepción.
            info = null;
            return "el archivo ya no existe";
        }
        catch (MediaException ex)
        {
            info = null;
            return IsLocked(path) ? "otro programa lo tiene abierto sin dejar leerlo; ciérralo y vuelve a importarlo" : $"no se pudo leer ({ex.Message})";
        }

        return Problem(info);
    }

    // FFmpeg dice «No such file or directory» de un archivo que existe pero está bloqueado (Windows).
    private static bool IsLocked(string path)
    {
        try
        {
            using FileStream _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException)
        {
            return File.Exists(path);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary><c>null</c> si el medio ya leído se puede usar; si no, el motivo en español.</summary>
    public static string? Problem(MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.IsStillImage)
        {
            return null;
        }

        if (info.HasVideo && !info.HasAudio && StillImageFormats.Contains(info.ContainerFormat))
        {
            return "el formato de esta imagen fija todavía no es compatible; usa PNG, JPEG, WebP, BMP o TIFF";
        }

        if (info.Video is { FrameCount: <= 1 } && !info.HasAudio)
        {
            return "el medio solo tiene un cuadro y no es un formato de imagen fija compatible";
        }

        return info.Duration > Time.Zero ? null : "no tiene duración";
    }
}
