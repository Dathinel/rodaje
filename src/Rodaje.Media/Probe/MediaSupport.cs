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

    /// <summary>Lee el archivo; devuelve <c>null</c> si se puede usar o el motivo en español si no.</summary>
    public static string? Classify(string path, out MediaInfo? info)
    {
        try
        {
            info = MediaProbe.Probe(path);
        }
        catch (MediaException ex)
        {
            info = null;
            return $"no se pudo leer ({ex.Message})";
        }

        return Problem(info);
    }

    /// <summary><c>null</c> si el medio ya leído se puede usar; si no, el motivo en español.</summary>
    public static string? Problem(MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Video is { } video && !info.HasAudio && (StillImageFormats.Contains(info.ContainerFormat) || video.FrameCount <= 1))
        {
            return "es una imagen fija y Rodaje todavía no usa imágenes como clips; conviértela en un video corto para usarla";
        }

        return info.Duration > Time.Zero ? null : "no tiene duración";
    }
}
