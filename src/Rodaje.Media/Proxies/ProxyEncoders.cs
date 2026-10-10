using System.Globalization;
using Rodaje.Core.Timing;

namespace Rodaje.Media.Proxies;

/// <summary>Qué necesita el generador del origen para armar la línea de <c>ffmpeg.exe</c>.</summary>
/// <param name="SourcePath">Ruta absoluta del original.</param>
/// <param name="StreamIndex">Flujo de video que elige <see cref="Probe.MediaProbe"/> (el mismo que decodifica Rodaje).</param>
/// <param name="DisplayWidth">Ancho en pantalla del original, ya rotado y con el SAR aplicado.</param>
/// <param name="DisplayHeight">Alto en pantalla del original.</param>
/// <param name="FrameRate">Tasa con la que Rodaje numera los cuadros del original.</param>
public sealed record ProxyEncodeInput(string SourcePath, int StreamIndex, int DisplayWidth, int DisplayHeight, Rational FrameRate);

/// <summary>Codificadores de H.264 para proxies y la línea de comandos de cada uno.</summary>
/// <remarks>
/// Orden de preferencia: NVENC (GPU NVIDIA), Media Foundation (el codificador de Windows, normalmente por
/// hardware) y OpenH264 (software, incluido en la compilación LGPL). Ninguno es GPL.
/// </remarks>
public static class ProxyEncoders
{
    public static IReadOnlyList<string> Preference { get; } = ["h264_nvenc", "h264_mf", "libopenh264"];

    /// <summary>Formato de píxel que acepta cada codificador (Media Foundation solo negocia NV12).</summary>
    public static IReadOnlyList<string> PixelFormatArguments(string encoder) =>
        encoder == "h264_mf" ? ["-pix_fmt", "nv12"] : ["-pix_fmt", "yuv420p"];

    /// <summary>
    /// Argumentos completos de <c>ffmpeg.exe</c> para un proxy: mismos cuadros y mismas marcas que el original.
    /// </summary>
    /// <remarks>
    /// <c>-copyts</c> conserva las marcas de entrada (incluido el desplazamiento inicial), <c>-fps_mode passthrough</c>
    /// no duplica ni descarta cuadros y <c>-enc_time_base:v demux</c> evita que el codificador redondee las marcas a
    /// la rejilla de la tasa. Así el cuadro <c>n</c> del proxy es el cuadro <c>n</c> del original.
    /// </remarks>
    public static IReadOnlyList<string> BuildArguments(
        ProxyEncodeInput input, ProxySettings settings, string encoder, string outputPath, bool hardwareDecoding)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(encoder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        settings.Validate();
        (int width, int height) = settings.OutputSize(input.DisplayWidth, input.DisplayHeight);
        string bitrate = Kbps(Bitrate(settings, input.FrameRate));
        string maxrate = Kbps(Bitrate(settings, input.FrameRate) * 2);
        string gop = settings.GopLength.ToString(CultureInfo.InvariantCulture);

        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        if (hardwareDecoding)
        {
            args.AddRange(["-hwaccel", "d3d11va"]);
        }

        args.AddRange([
            "-copyts", "-i", input.SourcePath,
            "-map", "0:" + input.StreamIndex.ToString(CultureInfo.InvariantCulture),
            "-an", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
            "-fps_mode", "passthrough", "-enc_time_base:v", "demux",
            "-vf", string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height}:flags=bicubic,setsar=1"),
            "-c:v", encoder,
        ]);
        args.AddRange(encoder switch
        {
            "h264_nvenc" => ["-preset", "p4", "-tune", "hq", "-profile:v", "high", "-rc", "vbr", "-b:v", bitrate,
                "-maxrate", maxrate, "-bufsize", maxrate, "-no-scenecut", "1", "-forced-idr", "1"],
            "h264_mf" => ["-rate_control", "pc_vbr", "-b:v", bitrate, "-maxrate", maxrate],
            "libopenh264" => ["-rc_mode", "bitrate", "-b:v", bitrate, "-maxrate", maxrate, "-allow_skip_frames", "0"],
            _ => ["-b:v", bitrate],
        });
        args.AddRange(["-g", gop, "-bf", "0"]);
        args.AddRange(PixelFormatArguments(encoder));
        args.AddRange(["-progress", "pipe:1", "-stats_period", "0.25", "-nostats", "-f", "mp4", outputPath]);
        return args;
    }

    internal static int Bitrate(ProxySettings settings, Rational frameRate)
    {
        double scale = Math.Clamp(frameRate.ToDouble() / 30.0, 0.5, 2.0);
        return (int)Math.Round(settings.BitrateKbpsAt30Fps * scale);
    }

    private static string Kbps(int kbps) => kbps.ToString(CultureInfo.InvariantCulture) + "k";
}
