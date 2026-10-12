using System.Globalization;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;

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
/// Salen del catálogo de la exportación (<see cref="ExportEncoderCatalog"/>): los de H.264 en su orden de preferencia
/// y con su formato de píxel, de los que aquí se sabe armar la tasa de un proxy. Hoy son NVENC (GPU NVIDIA), Media
/// Foundation (el codificador de Windows, que sin <c>-hw_encoding</c> usa el de la GPU si lo hay) y OpenH264 (software,
/// incluido en la compilación LGPL). Ninguno es GPL. Los argumentos fijos del catálogo no se copian: los de un proxy
/// (tasa, GOP corto, sin cuadros B) son otros, y el <c>-hw_encoding 0</c> de Media Foundation lo haría más lento.
/// </remarks>
public static class ProxyEncoders
{
    // Los que tienen tasa de proxy en BuildArguments; los demás de H.264 del catálogo (AMF, Quick Sync) no se usan aquí.
    private static readonly string[] WithProxyRate = ["h264_nvenc", "h264_mf", "libopenh264"];

    public static IReadOnlyList<string> Preference { get; } =
        [.. ExportEncoderCatalog.Video.Where(e => e.Codec == ExportCodec.H264 && WithProxyRate.Contains(e.Id)).Select(e => e.Id)];

    /// <summary>
    /// Formato de píxel del proxy con cada codificador: el primero de 8 bits 4:2:0 que el catálogo le da (NV12 en NVENC y
    /// en Media Foundation, que solo negocia NV12; <c>yuv420p</c> en OpenH264).
    /// </summary>
    public static IReadOnlyList<string> PixelFormatArguments(string encoder)
    {
        ExportPixelFormat pixels = ExportEncoderCatalog.Find(encoder)?.PixelFormats
            .FirstOrDefault(f => f.BitDepth() == 8 && f.Chroma() == ChromaSubsampling.Chroma420 && !f.HasAlpha(), ExportPixelFormat.Yuv420P)
            ?? ExportPixelFormat.Yuv420P;
        return ["-pix_fmt", pixels.FfmpegName()];
    }

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
