using System.Globalization;
using Rodaje.Media.Export;

namespace Rodaje.Export;

/// <summary>
/// Dice si unos ajustes se pueden exportar con los codificadores elegidos y, si no, por qué. El diálogo solo ofrece lo
/// que pasa por aquí (validación honesta, <c>docs/fase-6.md</c>).
/// </summary>
/// <remarks>Primera versión del paso 0: la matriz contenedor × códec × píxel × perfil y las capacidades de <see cref="EncoderSpec"/>.</remarks>
public static class ExportSettingsValidator
{
    /// <param name="video">Codificador que se usará para <see cref="ExportSettings.Video"/>; obligatorio si hay video.</param>
    /// <param name="audio">Codificador que se usará para <see cref="ExportSettings.Audio"/>; obligatorio si hay audio.</param>
    public static IReadOnlyList<ExportProblem> Validate(ExportSettings settings, EncoderSpec? video, EncoderSpec? audio)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if ((settings.Video is null) != (video is null) || (settings.Audio is null) != (audio is null))
        {
            throw new ArgumentException("Cada pista de los ajustes necesita su codificador, y solo ella.");
        }

        ExportPixelFormat? pixels = null;
        var problems = new List<ExportProblem>();
        if (settings.Video is { } v && video is not null)
        {
            CheckCodec(v.Codec, video, problems);
            pixels = video.PixelFormatFor(v.BitDepth, v.Chroma, v.Alpha);
            if (pixels is null)
            {
                problems.Add(new(v.Alpha && !video.SupportsAlpha ? ExportProblemCode.AlphaNotSupported : ExportProblemCode.PixelFormatNotInEncoder,
                    string.Create(CultureInfo.InvariantCulture,
                        $"{video.Id} no codifica {v.BitDepth} bits {Name(v.Chroma)}{(v.Alpha ? " con alfa" : "")}.")));
            }

            CheckRate(v.RateControl, video, problems);
            CheckVideoDetails(v, video, problems);
        }

        if (settings.Audio is { } a && audio is not null)
        {
            CheckCodec(a.Codec, audio, problems);
            CheckRate(a.RateControl, audio, problems);
            CheckAudioDetails(a, audio, problems);
        }

        problems.InsertRange(0, ExportCompatibility.Check(settings.Container, video, pixels, audio, settings.Video?.Profile));
        return problems;
    }

    private static void CheckCodec(ExportCodec codec, EncoderSpec encoder, List<ExportProblem> problems)
    {
        if (encoder.Codec != codec)
        {
            problems.Add(new(ExportProblemCode.InvalidValue, $"{encoder.Id} codifica {encoder.Codec}, no {codec}."));
        }
    }

    private static void CheckRate(RateControl rate, EncoderSpec encoder, List<ExportProblem> problems)
    {
        if (!encoder.Supports(rate.Mode))
        {
            problems.Add(new(ExportProblemCode.RateControlNotSupported, $"{encoder.Id} no admite el control de tasa {rate.Mode}."));
        }

        if (rate.Passes is < 1 or > 2)
        {
            problems.Add(new(ExportProblemCode.InvalidValue, "Las pasadas son 1 o 2."));
        }
        else if (rate.Passes == 2 && !encoder.SupportsTwoPass)
        {
            problems.Add(new(ExportProblemCode.TwoPassNotSupported, $"{encoder.Id} no hace dos pasadas."));
        }

        if (rate.Quality is { } quality && encoder.Quality is { } scale && !scale.Contains(quality))
        {
            problems.Add(new(ExportProblemCode.QualityOutOfRange, string.Create(CultureInfo.InvariantCulture,
                $"{scale.Name} {quality} está fuera de {scale.Min}–{scale.Max} en {encoder.Id}.")));
        }

        bool needsBitrate = rate.Mode is RateControlMode.Vbr or RateControlMode.Cbr
            || (rate.Mode == RateControlMode.Abr && rate.TargetSizeBytes is null);
        if ((needsBitrate && rate.BitrateKbps is not > 0) || rate.BitrateKbps <= 0 || rate.MaxBitrateKbps < rate.BitrateKbps
            || rate.TargetSizeBytes <= 0)
        {
            problems.Add(new(ExportProblemCode.InvalidValue, "La tasa de bits falta o no es coherente (la máxima no puede ser menor que la media)."));
        }
    }

    private static void CheckVideoDetails(VideoExportSettings v, EncoderSpec encoder, List<ExportProblem> problems)
    {
        if (v.Level is { } level && encoder.Levels.Count > 0 && !encoder.Levels.Contains(level))
        {
            problems.Add(new(ExportProblemCode.LevelUnknown, $"{encoder.Id} no tiene el nivel {level}."));
        }

        if (v.Gop.BFrames > encoder.MaxBFrames)
        {
            problems.Add(new(ExportProblemCode.TooManyBFrames, string.Create(CultureInfo.InvariantCulture,
                $"{encoder.Id} admite hasta {encoder.MaxBFrames} cuadros B.")));
        }

        if (v.Width <= 0 || v.Height <= 0 || v.Width > ExportVideoFormat.MaxDimension || v.Height > ExportVideoFormat.MaxDimension
            || v.Crop is { Width: <= 0 } or { Height: <= 0 } or { X: < 0 } or { Y: < 0 } || v.Gop.KeyframeSeconds <= 0 || v.Gop.Length <= 0)
        {
            problems.Add(new(ExportProblemCode.InvalidValue, "El tamaño, el recorte o el GOP tienen un valor imposible."));
        }
    }

    private static void CheckAudioDetails(AudioExportSettings a, EncoderSpec encoder, List<ExportProblem> problems)
    {
        if (encoder.SampleRates.Count > 0 && !encoder.SampleRates.Contains(a.SampleRate))
        {
            problems.Add(new(ExportProblemCode.SampleRateNotSupported, string.Create(CultureInfo.InvariantCulture,
                $"{encoder.Id} no admite {a.SampleRate} Hz.")));
        }

        if (a.Channels < 1 || (encoder.MaxChannels > 0 && a.Channels > encoder.MaxChannels))
        {
            problems.Add(new(ExportProblemCode.TooManyChannels, string.Create(CultureInfo.InvariantCulture,
                $"{encoder.Id} no admite {a.Channels} canales.")));
        }

        if (a.Loudness is { } loudness && (loudness.Lufs is < -70 or > 0 || loudness.MaxTruePeakDbtp is < -20 or > 0))
        {
            problems.Add(new(ExportProblemCode.InvalidValue, "El objetivo de sonoridad está fuera de −70…0 LUFS o el pico fuera de −20…0 dBTP."));
        }
    }

    private static string Name(ChromaSubsampling chroma) => chroma switch
    {
        ChromaSubsampling.Chroma420 => "4:2:0",
        ChromaSubsampling.Chroma422 => "4:2:2",
        _ => "4:4:4",
    };
}
