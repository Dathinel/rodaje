using System.Globalization;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;

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
/// <para>El proxy es H.264 8 bits 4:2:0 en todos los equipos: es el único perfil que decodifican por hardware todas las
/// GPU de NVIDIA, AMD e Intel de los últimos diez años, y a 540p la CPU lo decodifica a cientos de cuadros por segundo
/// donde no hay GPU. HEVC o AV1 no decodificarían más rápido y dejarían fuera a las GPU viejas.</para>
/// <para>Lo que cambia por fabricante es quién lo codifica (<see cref="PreferenceFor"/>): el de la GPU principal (NVENC,
/// AMF o Quick Sync), luego el de otra GPU del equipo, luego Media Foundation (CPU) y OpenH264. Ninguno es GPL.</para>
/// </remarks>
public static class ProxyEncoders
{
    /// <summary>El orden de un equipo NVIDIA (y el de siempre).</summary>
    public static IReadOnlyList<string> Preference { get; } = ["h264_nvenc", "h264_mf", "libopenh264"];

    /// <summary>Codificadores de proxy del más al menos preferido en este equipo: primero los de su GPU principal.</summary>
    public static IReadOnlyList<string> PreferenceFor(GpuProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var order = new List<string>();
        IEnumerable<GpuVendor> vendors = profile.Adapters.Where(a => !a.IsSoftware).Select(a => a.Vendor).Distinct();
        foreach (GpuVendor vendor in vendors)
        {
            string? id = vendor switch
            {
                GpuVendor.Nvidia => "h264_nvenc",
                GpuVendor.Amd => "h264_amf",
                GpuVendor.Intel => "h264_qsv",
                _ => null,
            };
            if (id is not null && profile.Adapters.First(a => !a.IsSoftware && a.Vendor == vendor).Generation is var generation
                && (!generation.Known || generation.Encodes(HardwareEncodeCodecs.H264)))
            {
                order.Add(id);
            }
        }

        order.AddRange(["h264_mf", "libopenh264"]);
        return order;
    }

    /// <summary>Formato de píxel que acepta cada codificador (Media Foundation, AMF y Quick Sync, NV12).</summary>
    public static IReadOnlyList<string> PixelFormatArguments(string encoder) =>
        encoder is "h264_mf" or "h264_amf" or "h264_qsv" ? ["-pix_fmt", "nv12"] : ["-pix_fmt", "yuv420p"];

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
            "h264_amf" => ["-usage", "transcoding", "-quality", "speed", "-profile:v", "high", "-rc", "vbr_peak", "-b:v", bitrate,
                "-maxrate", maxrate, "-bufsize", maxrate],
            "h264_qsv" => ["-preset", "veryfast", "-profile:v", "high", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", maxrate],
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
