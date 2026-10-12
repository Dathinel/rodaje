using System.Globalization;
using Rodaje.Core.Timing;
using Rodaje.Export.Tuning;
using Rodaje.Media.Export;

namespace Rodaje.Export;

/// <summary>Argumentos de <c>ffmpeg.exe</c> para <see cref="ExportSinkRequest"/> y cuántas pasadas hacen falta.</summary>
/// <param name="Video">Van en <see cref="ExportSinkRequest.VideoArguments"/>; incluyen los del contenedor si hay video.</param>
/// <param name="Audio">Van en <see cref="ExportSinkRequest.AudioArguments"/>; incluyen los del contenedor si solo hay audio.</param>
/// <param name="PassCount">1, o 2 si el codificador hace dos pasadas con <c>-pass</c> (NVENC las hace dentro de una).</param>
public sealed record EncoderArguments(IReadOnlyList<string> Video, IReadOnlyList<string> Audio, int PassCount);

/// <summary>
/// Traduce <see cref="ExportSettings"/> a la línea de comandos de cada familia de codificadores (NVENC, AMF, QSV, Media
/// Foundation, SVT-AV1, libaom, libvpx, ProRes, DNxHR…). Los ajustes ya pasaron <see cref="ExportSettingsValidator"/>;
/// aquí no se valida, solo se arma. Los fijos de cada codificador vienen en <see cref="EncoderSpec.BaseArguments"/>.
/// </summary>
public static class FfmpegArguments
{
    /// <summary>Tasas que admite el AAC de Media Foundation, en kbps.</summary>
    public static IReadOnlyList<int> MediaFoundationAacKbps { get; } = [96, 128, 160, 192];

    /// <summary>Todos los argumentos de una exportación.</summary>
    /// <param name="settings">Ajustes ya validados.</param>
    /// <param name="video">Codificador de video, si hay video.</param>
    /// <param name="pixels">Formato de píxel que llegará al sink (<see cref="EncoderSpec.PixelFormatFor"/>).</param>
    /// <param name="audio">Codificador de audio, si hay audio.</param>
    /// <param name="frameRate">Tasa de la salida (para el GOP en segundos).</param>
    /// <param name="duration">Duración del rango (para el tamaño objetivo).</param>
    /// <param name="loudnessFilter">Filtro de la segunda pasada de sonoridad (<see cref="FfmpegLoudness.Filter"/>), o <c>null</c>.</param>
    public static EncoderArguments Build(ExportSettings settings, EncoderSpec? video, ExportPixelFormat? pixels, EncoderSpec? audio,
        Rational frameRate, TimeSpan duration, string? loudnessFilter = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<string> videoArgs = [];
        List<string> audioArgs = [];
        int passes = 1;
        if (settings.Audio is { } a && audio is not null)
        {
            audioArgs.AddRange(Audio(a, audio, loudnessFilter));
        }

        if (settings.Video is { } v && video is not null)
        {
            ExportPixelFormat format = pixels ?? video.PixelFormatFor(v.BitDepth, v.Chroma, v.Alpha)
                ?? throw new ArgumentException($"{video.Id} no tiene formato de píxel para estos ajustes.", nameof(pixels));
            int audioKbps = settings.Audio is null ? 0 : AudioKbps(settings.Audio, audio);
            videoArgs.AddRange(Video(v, video, format, frameRate, duration, audioKbps, settings.Performance));
            passes = PassCount(v.RateControl, video);
            if (video.Codec == ExportCodec.Hevc && settings.Container is ExportContainer.Mp4 or ExportContainer.Mov)
            {
                // «hvc1» es lo que piden QuickTime, Safari y Películas y TV para HEVC en MP4/MOV.
                videoArgs.AddRange(["-tag:v", "hvc1"]);
            }

            if (settings.Container == ExportContainer.Mov)
            {
                videoArgs.AddRange(RangeInBitstream(video, v.Color?.FullRange ?? format.IsRgb()));
            }
        }

        (videoArgs.Count > 0 || settings.Video is not null ? videoArgs : audioArgs).AddRange(Container(settings));
        return new EncoderArguments(videoArgs, audioArgs, passes);
    }

    /// <summary>Argumentos del contenedor: <c>+faststart</c> en MP4, MOV y M4A.</summary>
    public static IReadOnlyList<string> Container(ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.FastStart && settings.Container is ExportContainer.Mp4 or ExportContainer.Mov or ExportContainer.M4a
            ? ["-movflags", "+faststart"]
            : [];
    }

    /// <summary>
    /// Filtro de flujo que escribe el rango en el VUI de H.264 o HEVC cuando el codificador no lo hace: <c>h264_mf</c> y
    /// <c>hevc_mf</c> no lo escriben, y en MOV la caja «colr» es «nclc», que lleva matriz, primarios y curva pero no rango
    /// (en MP4 es «nclx», que sí lo lleva). Sin esto un MOV de Media Foundation sale sin rango. Vacío en los demás.
    /// </summary>
    /// <param name="encoder">Codificador de video.</param>
    /// <param name="fullRange">El rango de <see cref="ExportColor"/>: completo o limitado.</param>
    public static IReadOnlyList<string> RangeInBitstream(EncoderSpec encoder, bool fullRange)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        if (Family(encoder) != EncoderFamily.MediaFoundation)
        {
            return [];
        }

        string? filter = encoder.Codec switch
        {
            ExportCodec.H264 => "h264_metadata",
            ExportCodec.Hevc => "hevc_metadata",
            _ => null,
        };
        // «:0»: solo el video principal; la miniatura adjunta es otro flujo de video (copia de una imagen).
        return filter is null ? [] : ["-bsf:v:0", $"{filter}=video_full_range_flag={(fullRange ? 1 : 0)}"];
    }

    /// <summary>2 si hay que componer el video dos veces (dos pasadas con <c>-pass</c>); 1 en lo demás.</summary>
    public static int PassCount(RateControl rate, EncoderSpec encoder)
    {
        ArgumentNullException.ThrowIfNull(rate);
        ArgumentNullException.ThrowIfNull(encoder);
        return rate.Passes == 2 && encoder.SupportsTwoPass && Family(encoder) is not (EncoderFamily.Nvenc or EncoderFamily.Amf or EncoderFamily.Qsv)
            ? 2
            : 1;
    }

    /// <summary>Control de tasa, perfil, nivel, preset, tune, GOP, cuadros B, referencias y alfa.</summary>
    /// <param name="performance">Sin preset elegido, el afinado por fabricante para el modo (<see cref="ExportHardwareTuning"/>).</param>
    public static IReadOnlyList<string> Video(VideoExportSettings settings, EncoderSpec encoder, ExportPixelFormat pixels, Rational frameRate,
        TimeSpan duration, int audioKbps, ExportPerformance performance = ExportPerformance.Background)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(encoder);
        EncoderFamily family = Family(encoder);
        var args = new List<string>();
        RateArguments(args, settings.RateControl, encoder, family, duration, audioKbps);

        string? profile = settings.Profile ?? encoder.Profiles.FirstOrDefault(p => p.Allows(pixels))?.Id;
        if (profile is not null)
        {
            args.AddRange(["-profile:v", profile]);
        }

        if (settings.Level is { } level)
        {
            args.AddRange(["-level:v", level]);
        }

        if ((settings.SpeedPreset ?? ExportHardwareTuning.SpeedPreset(encoder, performance)) is { } preset)
        {
            args.AddRange(["-preset", preset]);
        }

        if (settings.Tune is { } tune)
        {
            args.AddRange(family == EncoderFamily.SvtAv1 ? ["-svtav1-params", $"tune={tune}"] : ["-tune", tune]);
        }

        if (!encoder.IntraOnly)
        {
            if (settings.Gop.LengthAt(frameRate) is { } gop)
            {
                args.AddRange(["-g", Text(gop)]);
            }

            if (settings.Gop.Closed && family is not (EncoderFamily.Vpx or EncoderFamily.Aom))
            {
                // Cada cuadro clave es IDR; SVT-AV1 lo traduce a «intra refresh» cerrado.
                args.AddRange(["-flags", "+cgop"]);
            }

            if (settings.Gop.BFrames is { } bFrames && encoder.MaxBFrames > 0)
            {
                args.AddRange(["-bf", Text(Math.Clamp(bFrames, 0, encoder.MaxBFrames))]);
            }

            if (settings.Gop.References is { } references)
            {
                args.AddRange(["-refs", Text(references)]);
            }
        }

        if (pixels.HasAlpha())
        {
            switch (family)
            {
                case EncoderFamily.ProRes:
                    args.AddRange(["-alpha_bits", "16"]);
                    break;
                case EncoderFamily.Vpx:
                    // libvpx no codifica alfa con cuadros de referencia alternativos.
                    args.AddRange(["-auto-alt-ref", "0"]);
                    break;
            }
        }

        return args;
    }

    /// <summary>Tasa o calidad, perfil, frecuencia, canales, muestras y sonoridad.</summary>
    public static IReadOnlyList<string> Audio(AudioExportSettings settings, EncoderSpec encoder, string? loudnessFilter = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(encoder);
        var args = new List<string>();
        RateControl rate = settings.RateControl;
        int? kbps = rate.BitrateKbps;
        switch (encoder.Id)
        {
            case "aac_mf":
                args.AddRange(["-b:a", Kbps(NearestMediaFoundationAac(kbps ?? 192))]);
                break;
            case "libopus":
                if (kbps is { } opus)
                {
                    args.AddRange(["-b:a", Kbps(opus)]);
                }

                args.AddRange(["-vbr", rate.Mode switch { RateControlMode.Cbr => "off", RateControlMode.Abr => "constrained", _ => "on" }]);
                break;
            case "libmp3lame":
                if (rate.Mode is RateControlMode.ConstantQuality or RateControlMode.Vbr && rate.Quality is { } lameQuality)
                {
                    args.AddRange(["-q:a", Text(lameQuality)]);
                }
                else if (kbps is { } mp3)
                {
                    args.AddRange(["-b:a", Kbps(mp3)]);
                    if (rate.Mode == RateControlMode.Abr)
                    {
                        args.AddRange(["-abr", "1"]);
                    }
                }

                break;
            case "flac":
                args.AddRange(settings.PcmFormat == ExportSampleFormat.Pcm16 ? ["-sample_fmt", "s16"] : ["-sample_fmt", "s32", "-bits_per_raw_sample", "24"]);
                break;
            case "alac":
                args.AddRange(["-sample_fmt", settings.PcmFormat == ExportSampleFormat.Pcm16 ? "s16p" : "s32p"]);
                break;
            default:
                if (rate.Mode == RateControlMode.ConstantQuality && rate.Quality is { } quality && encoder.Quality is { } scale)
                {
                    args.AddRange([scale.Argument, Text(quality)]);
                }
                else if (kbps is { } bitrate && encoder.Codec != ExportCodec.Pcm)
                {
                    args.AddRange(["-b:a", Kbps(bitrate)]);
                }

                break;
        }

        if (settings.Profile is { } profile)
        {
            args.AddRange(["-profile:a", profile]);
        }

        if (loudnessFilter is not null)
        {
            args.AddRange(["-af", loudnessFilter]);
        }

        // Tras el filtro: loudnorm trabaja a 192 kHz y hay que volver a la frecuencia pedida.
        args.AddRange(["-ar", Text(settings.SampleRate), "-ac", Text(settings.Channels)]);
        return args;
    }

    /// <summary>La tasa de AAC de Media Foundation más cercana a la pedida.</summary>
    public static int NearestMediaFoundationAac(int kbps) => MediaFoundationAacKbps.MinBy(k => Math.Abs(k - kbps));

    private static void RateArguments(List<string> args, RateControl rate, EncoderSpec encoder, EncoderFamily family, TimeSpan duration,
        int audioKbps)
    {
        RateControlMode mode = rate.Mode;
        int? average = rate.BitrateKbps;
        if (rate.TargetSizeBytes is { } target)
        {
            average = RateControl.BitrateForTargetSize(target, duration, audioKbps);
            mode = RateControlMode.Abr;
        }

        int? max = rate.MaxBitrateKbps;
        int? buffer = rate.BufferKbps;
        switch (mode)
        {
            case RateControlMode.ConstantQuality:
                double quality = rate.Quality ?? encoder.Quality?.Default ?? 23;
                switch (family)
                {
                    case EncoderFamily.Nvenc:
                        args.AddRange(["-rc", "vbr", encoder.Quality?.Argument ?? "-cq", Text(quality), "-b:v", "0"]);
                        break;
                    case EncoderFamily.Amf:
                        args.AddRange(["-rc", "qvbr", encoder.Quality?.Argument ?? "-qvbr_quality_level", Text(quality)]);
                        break;
                    case EncoderFamily.Qsv:
                        args.AddRange([encoder.Quality?.Argument ?? "-global_quality", Text(quality)]);
                        break;
                    case EncoderFamily.MediaFoundation:
                        args.AddRange(["-rate_control", "quality", encoder.Quality?.Argument ?? "-quality", Text(quality)]);
                        break;
                    case EncoderFamily.Vpx or EncoderFamily.Aom:
                        args.AddRange([encoder.Quality?.Argument ?? "-crf", Text(quality), "-b:v", "0"]);
                        break;
                    default:
                        args.AddRange([encoder.Quality?.Argument ?? "-crf", Text(quality)]);
                        break;
                }

                if (max is { } cap)
                {
                    args.AddRange(["-maxrate", Kbps(cap), "-bufsize", Kbps(buffer ?? cap * 2)]);
                }

                break;
            case RateControlMode.Vbr:
                RequireBitrate(average, encoder, mode);
                args.AddRange(family switch
                {
                    EncoderFamily.Nvenc => ["-rc", "vbr"],
                    EncoderFamily.Amf => ["-rc", max is null ? "vbr_latency" : "vbr_peak"],
                    EncoderFamily.MediaFoundation => ["-rate_control", max is null ? "u_vbr" : "pc_vbr"],
                    _ => [],
                });
                args.AddRange(["-b:v", Kbps(average!.Value)]);
                if (max is { } peak && family != EncoderFamily.SvtAv1)
                {
                    args.AddRange(["-maxrate", Kbps(peak), "-bufsize", Kbps(buffer ?? peak * 2)]);
                }

                break;
            case RateControlMode.Cbr:
                RequireBitrate(average, encoder, mode);
                string kbps = Kbps(average!.Value);
                args.AddRange(family switch
                {
                    EncoderFamily.Nvenc or EncoderFamily.Amf => ["-rc", "cbr", "-b:v", kbps, "-maxrate", kbps],
                    EncoderFamily.MediaFoundation => ["-rate_control", "cbr", "-b:v", kbps],
                    EncoderFamily.SvtAv1 => ["-b:v", kbps, "-svtav1-params", "rc=2:pred-struct=1"],
                    _ => ["-b:v", kbps, "-minrate", kbps, "-maxrate", kbps],
                });
                args.AddRange(["-bufsize", Kbps(buffer ?? average.Value * 2)]);
                break;
            case RateControlMode.Abr:
                RequireBitrate(average, encoder, mode);
                args.AddRange(family switch
                {
                    EncoderFamily.Nvenc => ["-rc", "vbr"],
                    EncoderFamily.MediaFoundation => ["-rate_control", "u_vbr"],
                    _ => [],
                });
                args.AddRange(["-b:v", Kbps(average!.Value)]);
                break;
            case RateControlMode.Lossless:
                args.AddRange(family switch
                {
                    EncoderFamily.Nvenc => ["-tune", "lossless"],
                    EncoderFamily.Vpx => ["-lossless", "1"],
                    EncoderFamily.Aom => ["-aom-params", "lossless=1"],
                    _ => [],
                });
                break;
        }

        if (rate.Passes == 2 && family == EncoderFamily.Nvenc && encoder.SupportsTwoPass)
        {
            // NVENC hace la pasada de análisis dentro de la misma codificación.
            args.AddRange(["-multipass", "fullres"]);
        }
    }

    private static void RequireBitrate(int? kbps, EncoderSpec encoder, RateControlMode mode)
    {
        if (kbps is not > 0)
        {
            throw new ArgumentException($"{mode} en {encoder.Id} necesita una tasa en kbps.");
        }
    }

    private static int AudioKbps(AudioExportSettings settings, EncoderSpec? encoder) =>
        encoder?.Id == "aac_mf" ? NearestMediaFoundationAac(settings.RateControl.BitrateKbps ?? 192)
        : settings.RateControl.BitrateKbps
            ?? (settings.Codec is ExportCodec.Pcm or ExportCodec.Flac or ExportCodec.Alac
                ? settings.SampleRate * settings.Channels * 24 / 1000
                : 192);

    private static EncoderFamily Family(EncoderSpec encoder) => encoder.Id switch
    {
        var id when id.EndsWith("_nvenc", StringComparison.Ordinal) => EncoderFamily.Nvenc,
        var id when id.EndsWith("_amf", StringComparison.Ordinal) => EncoderFamily.Amf,
        var id when id.EndsWith("_qsv", StringComparison.Ordinal) => EncoderFamily.Qsv,
        var id when id.EndsWith("_mf", StringComparison.Ordinal) => EncoderFamily.MediaFoundation,
        "libsvtav1" => EncoderFamily.SvtAv1,
        "libaom-av1" => EncoderFamily.Aom,
        "libvpx-vp9" or "libvpx" => EncoderFamily.Vpx,
        "prores_ks" or "prores" or "prores_aw" => EncoderFamily.ProRes,
        _ => EncoderFamily.Other,
    };

    private static string Kbps(int kbps) => Text(kbps) + "k";

    private static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private enum EncoderFamily
    {
        Other,
        Nvenc,
        Amf,
        Qsv,
        MediaFoundation,
        SvtAv1,
        Aom,
        Vpx,
        ProRes,
    }
}
