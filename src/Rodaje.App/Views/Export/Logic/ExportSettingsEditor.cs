using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Views.Export;

/// <summary>Unos ajustes llevados a lo que se puede exportar, y qué se cambió para lograrlo (en español).</summary>
public sealed record CoercedSettings(ExportSettings Settings, IReadOnlyList<string> Changes);

/// <summary>
/// Lleva cualquier combinación (un preajuste, lo recordado de otro equipo o un cambio en el diálogo) a una que los
/// codificadores disponibles pueden hacer, y dice qué cambió y por qué. Después <see cref="ExportSettingsValidator"/>
/// solo encuentra lo que el usuario puede corregir (una tasa incoherente, un tamaño imposible).
/// </summary>
public static class ExportSettingsEditor
{
    public static CoercedSettings Coerce(ExportSettings settings, IReadOnlyList<EncoderSpec> encoders)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(encoders);
        var changes = new List<string>();
        ExportContainer container = settings.Container;
        IReadOnlyList<ExportContainer> containers = ExportChoices.Containers(encoders);
        if (!containers.Contains(container) && containers.Count > 0)
        {
            ExportContainer next = containers.Contains(ExportContainer.Mp4) ? ExportContainer.Mp4 : containers[0];
            changes.Add($"No hay codificadores para {ExportChoices.ContainerName(container)} en este equipo: se usa {ExportChoices.ContainerName(next)}.");
            container = next;
        }

        VideoExportSettings? video = CoerceVideo(settings.Video, container, encoders, changes);
        AudioExportSettings? audio = CoerceAudio(settings.Audio, container, encoders, changes);
        if (audio is null && video is null && container.IsAudioOnly() && ExportChoices.AudioCodecs(container, encoders) is [var first, ..])
        {
            audio = DefaultAudio(first, encoders);
        }

        return new(settings with { Container = container, Video = video, Audio = audio }, changes);
    }

    /// <summary>Codificadores que se usarán (los de los ajustes ya ajustados por <see cref="Coerce"/>).</summary>
    public static (EncoderSpec? Video, EncoderSpec? Audio) Encoders(ExportSettings settings, IReadOnlyList<EncoderSpec> encoders)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EncoderSpec? video = settings.Video is { } v ? ExportChoices.Resolve(v.Codec, v.EncoderId, encoders) : null;
        EncoderSpec? audio = settings.Audio is { } a ? ExportChoices.Resolve(a.Codec, a.EncoderId, encoders) : null;
        return (video, audio);
    }

    /// <summary>Sin GPU compatible ni Media Foundation (Windows N sin Media Feature Pack, Windows Server) no hay H.264 ni HEVC (D-028).</summary>
    public const string NoH26xMessage = "Este equipo no puede exportar H.264 ni HEVC: no tiene GPU compatible ni el codificador de Windows (Media Foundation falta en las ediciones N sin el Media Feature Pack y en Windows Server). Elige AV1 o VP9 en Avanzado.";

    /// <summary>Problemas que quedan; vacío si se puede exportar.</summary>
    public static IReadOnlyList<ExportProblem> Validate(ExportSettings settings, IReadOnlyList<EncoderSpec> encoders)
    {
        (EncoderSpec? video, EncoderSpec? audio) = Encoders(settings, encoders);
        if ((settings.Video is not null && video is null) || (settings.Audio is not null && audio is null))
        {
            return [new ExportProblem(ExportProblemCode.InvalidValue, settings.Video?.Codec is ExportCodec.H264 or ExportCodec.Hevc && video is null
                ? NoH26xMessage
                : "Este equipo no tiene un codificador para el formato elegido.")];
        }

        return ExportSettingsValidator.Validate(settings, video, audio);
    }

    /// <summary>Ajustes de audio razonables para el códec: 192 kbps, Opus 128, o sin pérdidas.</summary>
    public static AudioExportSettings DefaultAudio(ExportCodec codec, IReadOnlyList<EncoderSpec> encoders)
    {
        EncoderSpec? spec = ExportChoices.DefaultEncoder(codec, encoders);
        return new AudioExportSettings { Codec = codec, RateControl = DefaultAudioRate(spec, codec) };
    }

    /// <summary>Ajustes de video razonables para el códec en el contenedor (los de por defecto del codificador).</summary>
    public static VideoExportSettings DefaultVideo(ExportCodec codec, IReadOnlyList<EncoderSpec> encoders)
    {
        EncoderSpec? spec = ExportChoices.DefaultEncoder(codec, encoders);
        var video = new VideoExportSettings { Codec = codec };
        return spec is null ? video : video with { RateControl = DefaultVideoRate(spec) };
    }

    /// <summary>Tasa de video orientativa para 1080p30 según el códec, en kbps; se escala con los píxeles y la tasa.</summary>
    public static int SuggestedVideoKbps(ExportCodec codec, int width, int height, double fps)
    {
        double factor = codec switch
        {
            ExportCodec.Hevc or ExportCodec.Vp9 => 0.6,
            ExportCodec.Av1 => 0.5,
            _ => 1,
        };
        double scale = (double)width * height * Math.Max(1, fps) / (1920.0 * 1080 * 30);
        return (int)Math.Clamp(Math.Round(12_000 * factor * Math.Pow(scale, 0.85) / 100) * 100, 300, 400_000);
    }

    private static VideoExportSettings? CoerceVideo(VideoExportSettings? video, ExportContainer container, IReadOnlyList<EncoderSpec> encoders,
        List<string> changes)
    {
        string where = ExportChoices.ContainerName(container);
        if (container.IsAudioOnly())
        {
            return null;
        }

        IReadOnlyList<ExportCodec> codecs = ExportChoices.VideoCodecs(container, encoders);
        if (codecs.Count == 0)
        {
            return null;
        }

        if (video is null)
        {
            if (!container.AllowsAudio())
            {
                return DefaultVideo(codecs[0], encoders);
            }

            return null;
        }

        ExportCodec codec = video.Codec;
        if (!codecs.Contains(codec))
        {
            codec = codecs[0];
            changes.Add($"{where} no admite {ExportChoices.CodecName(video.Codec)} en este equipo: se usa {ExportChoices.CodecName(codec)}.");
            video = DefaultVideo(codec, encoders) with
            {
                Width = video.Width, Height = video.Height, Fit = video.Fit, FrameRate = video.FrameRate,
                FrameRateConversion = video.FrameRateConversion, Crop = video.Crop, ScaleFilter = video.ScaleFilter,
            };
        }

        EncoderSpec spec = ExportChoices.Resolve(codec, video.EncoderId, encoders)!;
        string? encoderId = video.EncoderId == spec.Id ? spec.Id : null;
        int bitDepth = video.BitDepth;
        IReadOnlyList<int> depths = ExportChoices.BitDepths(spec);
        if (!depths.Contains(bitDepth))
        {
            bitDepth = depths.Contains(8) ? 8 : depths[0];
            changes.Add($"{spec.Id} no codifica {video.BitDepth} bits: se usan {bitDepth}.");
        }

        bool alpha = video.Alpha && spec.PixelFormats.Any(f => f.HasAlpha() && f.BitDepth() == bitDepth);
        if (video.Alpha && !alpha)
        {
            changes.Add($"{spec.Id} no guarda transparencia: se exporta sin alfa.");
        }

        ChromaSubsampling chroma = video.Chroma;
        ExportPixelFormat? pixels = spec.PixelFormatFor(bitDepth, chroma, alpha);
        if (pixels is null)
        {
            ExportPixelFormat? found = spec.PixelFormats.Select(f => (ExportPixelFormat?)f)
                .FirstOrDefault(f => f!.Value.BitDepth() == bitDepth && f.Value.HasAlpha() == alpha);
            if (found is { } f)
            {
                chroma = f.Chroma();
                pixels = f;
            }
        }

        string? profile = video.Profile;
        if (profile is not null && (spec.Profiles.FirstOrDefault(p => p.Id == profile) is not { } known || (pixels is { } px && !known.Allows(px))))
        {
            string? next = ExportChoices.Profiles(spec, pixels) is [var firstProfile, ..] ? firstProfile.Id : null;
            changes.Add(next is null
                ? $"{spec.Id} no tiene el perfil «{profile}»: se deja el de por defecto."
                : $"El perfil «{profile}» no vale aquí: se usa «{spec.Profiles.First(p => p.Id == next).Name}».");
            profile = next;
        }

        RateControl rate = CoerceRate(video.RateControl, spec, changes);
        GopSettings gop = video.Gop;
        if (gop.BFrames > spec.MaxBFrames)
        {
            gop = gop with { BFrames = null };
        }

        return video with
        {
            Codec = codec,
            EncoderId = encoderId,
            BitDepth = bitDepth,
            Alpha = alpha,
            Chroma = chroma,
            Profile = profile,
            RateControl = rate,
            Gop = gop,
            Level = video.Level is { } level && spec.Levels.Contains(level) ? level : null,
            SpeedPreset = video.SpeedPreset is { } preset && spec.SpeedPresets.Contains(preset) ? preset : null,
            Tune = video.Tune is { } tune && spec.Tunes.Contains(tune) ? tune : null,
        };
    }

    private static AudioExportSettings? CoerceAudio(AudioExportSettings? audio, ExportContainer container, IReadOnlyList<EncoderSpec> encoders,
        List<string> changes)
    {
        string where = ExportChoices.ContainerName(container);
        if (audio is null)
        {
            return null;
        }

        if (!container.AllowsAudio())
        {
            changes.Add($"{where} no lleva sonido: se exporta sin audio.");
            return null;
        }

        IReadOnlyList<ExportCodec> codecs = ExportChoices.AudioCodecs(container, encoders);
        if (codecs.Count == 0)
        {
            changes.Add($"No hay codificador de audio para {where}: se exporta sin audio.");
            return null;
        }

        if (!codecs.Contains(audio.Codec))
        {
            ExportCodec codec = codecs[0];
            changes.Add($"{where} no admite audio {ExportChoices.CodecName(audio.Codec)}: se usa {ExportChoices.CodecName(codec)}.");
            audio = DefaultAudio(codec, encoders) with { Channels = audio.Channels, SampleRate = audio.SampleRate, Loudness = audio.Loudness };
        }

        EncoderSpec spec = ExportChoices.Resolve(audio.Codec, audio.EncoderId, encoders)!;
        IReadOnlyList<int> rates = ExportChoices.SampleRates(spec);
        int sampleRate = rates.Contains(audio.SampleRate) ? audio.SampleRate : rates.MinBy(r => Math.Abs(r - audio.SampleRate));
        IReadOnlyList<int> channels = ExportChoices.Channels(spec);
        int channelCount = channels.Contains(audio.Channels) ? audio.Channels : channels.Where(c => c <= audio.Channels).DefaultIfEmpty(channels[0]).Max();
        RateControl rate = audio.RateControl;
        if (!spec.Supports(rate.Mode))
        {
            RateControl fallback = DefaultAudioRate(spec, audio.Codec);
            rate = fallback.BitrateKbps is not null && rate.BitrateKbps is > 0 ? fallback with { BitrateKbps = rate.BitrateKbps } : fallback;
        }

        return audio with
        {
            EncoderId = audio.EncoderId == spec.Id ? spec.Id : null,
            SampleRate = sampleRate,
            Channels = channelCount,
            RateControl = rate,
        };
    }

    private static RateControl CoerceRate(RateControl rate, EncoderSpec spec, List<string> changes)
    {
        if (!spec.Supports(rate.Mode) || (rate.TargetSizeBytes is not null && !spec.SupportsTwoPass))
        {
            RateControl next = DefaultVideoRate(spec);
            if (rate.Mode is not (RateControlMode.Lossless or RateControlMode.ProfileDefined)
                || next.Mode is not (RateControlMode.Lossless or RateControlMode.ProfileDefined))
            {
                changes.Add($"{spec.Id} no admite «{ExportChoices.RateChoiceName(ExportChoices.RateChoiceOf(rate.Mode, rate.TargetSizeBytes is not null))}»: se usa «{ExportChoices.RateChoiceName(ExportChoices.RateChoiceOf(next.Mode))}».");
            }

            return next;
        }

        if (rate.Quality is { } quality && spec.Quality is { } scale && !scale.Contains(quality))
        {
            rate = rate with { Quality = null };
        }

        if (rate.Quality is not null && spec.Quality is null)
        {
            rate = rate with { Quality = null };
        }

        if (rate.Passes == 2 && !spec.SupportsTwoPass)
        {
            rate = rate with { Passes = 1 };
        }

        return rate;
    }

    private static RateControl DefaultVideoRate(EncoderSpec spec) => (spec.RateControls.Count > 0 ? spec.RateControls[0] : RateControlMode.ConstantQuality) switch
    {
        RateControlMode.Vbr => RateControl.Vbr(12_000, 18_000),
        RateControlMode.Cbr => RateControl.Cbr(12_000),
        RateControlMode.Abr => new RateControl { Mode = RateControlMode.Abr, BitrateKbps = 12_000 },
        RateControlMode.Lossless => RateControl.Lossless,
        RateControlMode.ProfileDefined => RateControl.ProfileDefined,
        _ => RateControl.ConstantQuality(),
    };

    private static RateControl DefaultAudioRate(EncoderSpec? spec, ExportCodec codec)
    {
        if (codec is ExportCodec.Pcm or ExportCodec.Flac or ExportCodec.Alac || spec?.RateControls is [RateControlMode.Lossless, ..])
        {
            return RateControl.Lossless;
        }

        int kbps = codec == ExportCodec.Opus ? 128 : 192;
        RateControlMode mode = spec?.RateControls is [var firstMode, ..] ? firstMode : RateControlMode.Cbr;
        return new RateControl { Mode = mode == RateControlMode.Vbr ? RateControlMode.Vbr : RateControlMode.Cbr, BitrateKbps = kbps };
    }
}
