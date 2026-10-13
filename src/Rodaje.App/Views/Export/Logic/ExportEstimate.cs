using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Views.Export;

/// <summary>Lo que el diálogo calcula antes de exportar. <see cref="Approximate"/> en calidad constante: el tamaño depende de la imagen.</summary>
public readonly record struct ExportEstimate(long Bytes, bool Approximate, TimeSpan Time)
{
    /// <summary>«≈ 245 MB · unos 1 min 05 s».</summary>
    public string Text => $"{(Approximate ? "≈ " : "")}{ExportText.Bytes(Bytes)} · unos {ExportText.Duration(Time)}";
}

/// <summary>
/// Tamaño y tiempo orientativos. Con tasa fija o tamaño objetivo el tamaño es casi exacto; en calidad constante sale de
/// una tasa típica por códec y resolución (el CRF cambia la tasa al doble cada 6 puntos). El tiempo usa las cifras del
/// prototipo de la fase 6 (unos 250 cuadros 1080p por segundo con NVENC, 40–80 con SVT-AV1).
/// </summary>
public static class ExportEstimator
{
    /// <summary>Lo que el contenedor añade (índices, cabeceras).</summary>
    private const double ContainerOverhead = 1.01;

    private const double Pixels1080p30 = 1920.0 * 1080 * 30;

    public static ExportEstimate Estimate(ExportSettings settings, EncoderSpec? videoEncoder, int width, int height, double fps, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        double seconds = Math.Max(0, duration.TotalSeconds);
        double audioBytes = settings.Audio is { } audio ? AudioBytesPerSecond(audio) * seconds : 0;
        bool approximate = false;
        double videoBytes = 0;
        if (settings.Video is { } video)
        {
            RateControl rate = video.RateControl;
            if (rate.TargetSizeBytes is { } target)
            {
                return new(target, false, Time(settings, videoEncoder, width, height, fps, seconds));
            }

            (double perSecond, approximate) = VideoBytesPerSecond(video, videoEncoder, width, height, fps);
            videoBytes = perSecond * seconds;
        }

        return new((long)Math.Round((videoBytes + audioBytes) * ContainerOverhead), approximate, Time(settings, videoEncoder, width, height, fps, seconds));
    }

    /// <summary>Bytes por segundo de video y si es una aproximación.</summary>
    public static (double BytesPerSecond, bool Approximate) VideoBytesPerSecond(VideoExportSettings video, EncoderSpec? encoder, int width, int height,
        double fps)
    {
        ArgumentNullException.ThrowIfNull(video);
        RateControl rate = video.RateControl;
        double scale = width * (double)height * fps / Pixels1080p30;
        switch (rate.Mode)
        {
            case RateControlMode.Vbr or RateControlMode.Cbr or RateControlMode.Abr when rate.BitrateKbps is { } kbps:
                return (kbps * 1000 / 8.0, false);
            case RateControlMode.Lossless:
                double raw = width * (double)height * fps * (video.Chroma switch
                {
                    ChromaSubsampling.Chroma420 => 1.5,
                    ChromaSubsampling.Chroma422 => 2,
                    _ => 3,
                }) * (video.BitDepth > 8 ? 2 : 1);
                return (raw * (video.Codec == ExportCodec.Png ? 0.6 : 0.45), true);
            case RateControlMode.ProfileDefined:
                return (ProfileMbps(video) * 1e6 / 8 * scale, video.Codec is ExportCodec.Gif or ExportCodec.WebP);
            default:
                double kbpsAt = ExportSettingsEditor.SuggestedVideoKbps(video.Codec, width, height, fps);
                if (encoder?.Quality is { } quality)
                {
                    double q = rate.Quality ?? quality.Default;
                    double steps = quality.LowerIsBetter ? (quality.Default - q) / 6 : (q - quality.Default) / 15;
                    kbpsAt *= Math.Pow(2, Math.Clamp(steps, -6, 6));
                }

                return (kbpsAt * 1000 / 8.0, true);
        }
    }

    public static double AudioBytesPerSecond(AudioExportSettings audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.RateControl.Mode == RateControlMode.Lossless || audio.Codec is ExportCodec.Pcm or ExportCodec.Flac or ExportCodec.Alac)
        {
            int bits = audio.PcmFormat switch
            {
                ExportSampleFormat.Pcm16 => 16,
                ExportSampleFormat.Pcm24 => 24,
                _ => 32,
            };
            double pcm = audio.SampleRate * audio.Channels * bits / 8.0;
            return audio.Codec == ExportCodec.Pcm ? pcm : pcm * 16 / bits * 0.6;
        }

        return (audio.RateControl.BitrateKbps ?? 192) * 1000 / 8.0;
    }

    /// <summary>Tasa de los códecs de perfil a 1080p30, en Mbps (tablas públicas de Apple y Avid).</summary>
    private static double ProfileMbps(VideoExportSettings video) => (video.Codec, video.Profile) switch
    {
        (ExportCodec.ProRes, "proxy") => 45,
        (ExportCodec.ProRes, "lt") => 102,
        (ExportCodec.ProRes, "hq") => 220,
        (ExportCodec.ProRes, "4444") => 330,
        (ExportCodec.ProRes, _) => 147,
        (ExportCodec.DnxHr, "dnxhr_lb") => 45,
        (ExportCodec.DnxHr, "dnxhr_hq" or "dnxhr_hqx") => 220,
        (ExportCodec.DnxHr, "dnxhr_444") => 440,
        (ExportCodec.DnxHr, _) => 145,
        (ExportCodec.CineForm, _) => 180,
        (ExportCodec.Gif or ExportCodec.WebP, _) => 60,
        _ => 100,
    };

    /// <summary>Tiempo orientativo; con bajo rendimiento cuenta con algo menos de la mitad del equipo.</summary>
    private static TimeSpan Time(ExportSettings settings, EncoderSpec? encoder, int width, int height, double fps, double seconds)
    {
        if (settings.Video is not { } video)
        {
            return TimeSpan.FromSeconds(seconds / 200);
        }

        double framesPerSecond1080 = video.Codec switch
        {
            ExportCodec.Av1 when encoder is { IsHardware: false } => 60,
            ExportCodec.Vp9 => 25,
            ExportCodec.H264 or ExportCodec.Hevc or ExportCodec.Av1 when encoder is { IsHardware: true } => 250,
            ExportCodec.ProRes or ExportCodec.DnxHr => 120,
            _ => 90,
        };
        double pixelScale = Math.Max(0.05, width * (double)height / (1920.0 * 1080));
        double speed = framesPerSecond1080 / pixelScale * (settings.Performance == ExportPerformance.Fastest ? 1 : 0.6);
        int passes = video.RateControl.Passes;
        // Un preajuste importado puede traer pasadas o tamaños absurdos (el diálogo los avisa como problema): la estimación
        // no puede lanzar OverflowException por ello.
        double total = seconds * fps / speed * passes;
        return double.IsNaN(total) || total <= 0 ? TimeSpan.Zero
            : total >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue
            : TimeSpan.FromSeconds(total);
    }
}
