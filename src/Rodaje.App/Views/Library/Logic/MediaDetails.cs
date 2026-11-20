using System.Globalization;
using Rodaje.Engine.Presentation;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Library;

/// <summary>Los datos de un medio para la columna de la biblioteca (resolución, fps y códec), sin WinUI.</summary>
public static class MediaDetails
{
    /// <summary>Lo que se ve mientras el códec se está leyendo del archivo.</summary>
    public const string Pending = "…";

    public static string DurationText(MediaRef media, Subclip? subclip = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        return media.IsStillImage ? "Imagen" : Timecode.Format(subclip?.Duration ?? media.Duration);
    }

    /// <summary>«1920×1080», o «—» si es solo audio.</summary>
    public static string Resolution(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return media.HasVideo && media.Width > 0 && media.Height > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{media.Width}×{media.Height}")
            : "—";
    }

    /// <summary>«30 fps», «29,97 fps», «23,976 fps»; «—» en una foto o un audio.</summary>
    public static string FrameRate(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return media.HasVideo && media.FrameRate is { } rate && LibraryFilter.KindOf(media) == LibraryKind.Video
            ? FrameRate(rate)
            : "—";
    }

    public static string FrameRate(Rational rate)
    {
        double value = rate.ToDouble();
        string text = value.ToString(Math.Abs(value - Math.Round(value)) < 0.0005 ? "0" : "0.###", CultureInfo.InvariantCulture);
        return text.Replace('.', ',') + " fps";
    }

    /// <summary>«48 kHz estéreo», «44,1 kHz mono», o «—» si no tiene sonido.</summary>
    public static string Audio(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (!media.HasAudio || media.SampleRate <= 0)
        {
            return "—";
        }

        string rate = (media.SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + " kHz";
        string channels = media.AudioChannels switch
        {
            1 => "mono",
            2 => "estéreo",
            > 2 => $"{media.AudioChannels} canales",
            _ => "",
        };
        return channels.Length == 0 ? rate : $"{rate} {channels}";
    }

    /// <summary>Nombre conocido del códec según FFmpeg (<c>h264</c> → «H.264»); el de FFmpeg en mayúsculas si no se conoce.</summary>
    public static string CodecName(string? ffmpegName)
    {
        if (string.IsNullOrWhiteSpace(ffmpegName))
        {
            return "—";
        }

        return ffmpegName.Trim().ToLowerInvariant() switch
        {
            "h264" => "H.264",
            "hevc" or "h265" => "H.265",
            "av1" => "AV1",
            "vp8" => "VP8",
            "vp9" => "VP9",
            "prores" => "ProRes",
            "dnxhd" => "DNxHD",
            "mpeg2video" => "MPEG-2",
            "mpeg4" => "MPEG-4",
            "mjpeg" => "JPEG",
            "png" => "PNG",
            "webp" => "WebP",
            "gif" => "GIF",
            "bmp" => "BMP",
            "tiff" => "TIFF",
            "aac" => "AAC",
            "mp3" or "mp3float" => "MP3",
            "opus" => "Opus",
            "vorbis" => "Vorbis",
            "flac" => "FLAC",
            "alac" => "ALAC",
            "ac3" => "AC-3",
            "eac3" => "E-AC-3",
            string other when other.StartsWith("pcm_", StringComparison.Ordinal) => "PCM",
            string other => other.ToUpperInvariant(),
        };
    }

    /// <summary>«H.264 · AAC», «PNG», «MP3»: los códecs de video y audio que haya, o <see cref="Pending"/> si aún no se leyeron.</summary>
    public static string Codecs(string? videoCodec, string? audioCodec, bool known)
    {
        if (!known)
        {
            return Pending;
        }

        string[] names = [.. new[] { videoCodec, audioCodec }.Where(c => !string.IsNullOrWhiteSpace(c)).Select(CodecName)];
        return names.Length == 0 ? "—" : string.Join(" · ", names);
    }

    /// <summary>Texto de ayuda con todos los datos del medio, para la ficha o la fila.</summary>
    public static string Summary(MediaRef media, string durationText, string codecs)
    {
        ArgumentNullException.ThrowIfNull(media);
        var parts = new List<string> { durationText };
        if (media.HasVideo)
        {
            parts.Add(Resolution(media));
            if (FrameRate(media) is { } fps && fps != "—")
            {
                parts.Add(fps);
            }
        }

        if (Audio(media) is { } audio && audio != "—")
        {
            parts.Add(audio);
        }

        if (codecs is { Length: > 0 } && codecs != "—" && codecs != Pending)
        {
            parts.Add(codecs);
        }

        return string.Join(" · ", parts);
    }
}
