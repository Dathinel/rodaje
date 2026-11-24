using Rodaje.Media.Export;

namespace Rodaje.App.Views.Export;

/// <summary>
/// Qué ofrece el diálogo según los codificadores que pasaron la sonda: solo combinaciones posibles (validación honesta).
/// Todo es puro; el orden de las listas es el de las opciones en pantalla.
/// </summary>
public static class ExportChoices
{
    /// <summary>Contenedores en los que se puede exportar algo con estos codificadores.</summary>
    public static IReadOnlyList<ExportContainer> Containers(IReadOnlyList<EncoderSpec> encoders)
    {
        ArgumentNullException.ThrowIfNull(encoders);
        return [.. Enum.GetValues<ExportContainer>().Where(c =>
            c.VideoCodecs().Any(codec => encoders.Any(e => e.Codec == codec))
            || (c.IsAudioOnly() && c.AudioCodecs().Any(codec => encoders.Any(e => e.Codec == codec))))];
    }

    /// <summary>Códecs de video del contenedor que tienen al menos un codificador.</summary>
    public static IReadOnlyList<ExportCodec> VideoCodecs(ExportContainer container, IReadOnlyList<EncoderSpec> encoders) =>
        [.. container.VideoCodecs().Where(codec => encoders.Any(e => e.Codec == codec))];

    public static IReadOnlyList<ExportCodec> AudioCodecs(ExportContainer container, IReadOnlyList<EncoderSpec> encoders) =>
        [.. container.AudioCodecs().Where(codec => encoders.Any(e => e.Codec == codec))];

    /// <summary>Codificadores del códec: los de por defecto primero y, entre ellos, los de hardware.</summary>
    public static IReadOnlyList<EncoderSpec> EncodersFor(ExportCodec codec, IReadOnlyList<EncoderSpec> encoders) =>
        [.. encoders.Where(e => e.Codec == codec).OrderBy(e => e.InDefaults ? 0 : 1).ThenBy(e => e.IsHardware ? 0 : 1)];

    /// <summary>El que se usa si el usuario no elige: nunca uno fuera de los valores por defecto (kvazaar, OpenH264).</summary>
    public static EncoderSpec? DefaultEncoder(ExportCodec codec, IReadOnlyList<EncoderSpec> encoders) =>
        EncodersFor(codec, encoders).FirstOrDefault(e => e.InDefaults);

    /// <summary>El codificador concreto si existe; si no (o no se pidió), el de por defecto.</summary>
    public static EncoderSpec? Resolve(ExportCodec codec, string? encoderId, IReadOnlyList<EncoderSpec> encoders) =>
        (encoderId is null ? null : encoders.FirstOrDefault(e => e.Id == encoderId && e.Codec == codec)) ?? DefaultEncoder(codec, encoders);

    /// <summary>Bits por canal que admite el codificador (8 y 10; 16 solo en imágenes).</summary>
    public static IReadOnlyList<int> BitDepths(EncoderSpec encoder) =>
        [.. encoder.PixelFormats.Select(f => f.BitDepth()).Distinct().Order()];

    /// <summary>Submuestreos que admite con estos bits.</summary>
    public static IReadOnlyList<ChromaSubsampling> Chromas(EncoderSpec encoder, int bitDepth) =>
        [.. encoder.PixelFormats.Where(f => f.BitDepth() == bitDepth && !f.HasAlpha()).Select(f => f.Chroma()).Distinct().Order()];

    /// <summary>«4:2:0», «4:2:2» o «4:4:4».</summary>
    public static string ChromaName(ChromaSubsampling chroma) => chroma switch
    {
        ChromaSubsampling.Chroma422 => "4:2:2",
        ChromaSubsampling.Chroma444 => "4:4:4",
        _ => "4:2:0",
    };

    /// <summary>Perfiles que valen con este formato de píxel.</summary>
    public static IReadOnlyList<EncoderProfile> Profiles(EncoderSpec encoder, ExportPixelFormat? pixels) =>
        [.. encoder.Profiles.Where(p => pixels is not { } f || p.Allows(f))];

    /// <summary>Modos de tasa que se ofrecen; el tamaño objetivo aparece si admite tasa media con dos pasadas.</summary>
    public static IReadOnlyList<RateChoice> RateChoices(EncoderSpec encoder)
    {
        var choices = new List<RateChoice>();
        foreach (RateControlMode mode in encoder.RateControls)
        {
            if (mode != RateControlMode.Abr)
            {
                choices.Add(RateChoiceOf(mode));
            }
        }

        if (encoder.Supports(RateControlMode.Abr) && encoder.SupportsTwoPass)
        {
            choices.Add(RateChoice.TargetSize);
        }

        return choices;
    }

    public static RateChoice RateChoiceOf(RateControlMode mode, bool targetSize = false) => targetSize ? RateChoice.TargetSize : mode switch
    {
        RateControlMode.Vbr => RateChoice.Vbr,
        RateControlMode.Cbr => RateChoice.Cbr,
        RateControlMode.Abr => RateChoice.Vbr,
        RateControlMode.Lossless => RateChoice.Lossless,
        RateControlMode.ProfileDefined => RateChoice.ProfileDefined,
        _ => RateChoice.Quality,
    };

    public static IReadOnlyList<int> SampleRates(EncoderSpec encoder) =>
        encoder.SampleRates.Count > 0 ? encoder.SampleRates : [44_100, 48_000, 96_000];

    /// <summary>Mono y estéreo siempre; 5.1 si el codificador lo admite.</summary>
    public static IReadOnlyList<int> Channels(EncoderSpec encoder) =>
        encoder.MaxChannels >= 6 ? [1, 2, 6] : encoder.MaxChannels == 1 ? [1] : [1, 2];

    /// <summary>«GPU · NVIDIA», «CPU», «Media Foundation (Windows)», con la nota si la hay. AMF y QSV, sin probar.</summary>
    public static string EncoderLabel(EncoderSpec encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        string where = encoder.Vendor switch
        {
            EncoderVendor.Nvidia => "GPU · NVIDIA",
            EncoderVendor.Amd => "GPU · AMD (sin probar)",
            EncoderVendor.Intel => "GPU · Intel (sin probar)",
            EncoderVendor.Microsoft => "Windows (Media Foundation)",
            _ => "CPU",
        };
        string text = $"{where} — {encoder.Id}";
        return encoder.Note is { Length: > 0 } note ? $"{text} · {note}" : text;
    }

    /// <summary>Nombre del códec en pantalla.</summary>
    public static string CodecName(ExportCodec codec) => codec switch
    {
        ExportCodec.H264 => "H.264",
        ExportCodec.Hevc => "HEVC (H.265)",
        ExportCodec.Av1 => "AV1",
        ExportCodec.Vp9 => "VP9",
        ExportCodec.ProRes => "ProRes",
        ExportCodec.DnxHr => "DNxHR",
        ExportCodec.CineForm => "CineForm",
        ExportCodec.Ffv1 => "FFV1 (sin pérdidas)",
        ExportCodec.Mjpeg => "Motion JPEG",
        ExportCodec.Png => "PNG",
        ExportCodec.QtAnimation => "QuickTime Animation",
        ExportCodec.Gif => "GIF",
        ExportCodec.WebP => "WebP",
        ExportCodec.Tiff => "TIFF",
        ExportCodec.Exr => "OpenEXR",
        ExportCodec.Aac => "AAC",
        ExportCodec.Opus => "Opus",
        ExportCodec.Mp3 => "MP3",
        ExportCodec.Flac => "FLAC (sin pérdidas)",
        ExportCodec.Alac => "ALAC (sin pérdidas)",
        ExportCodec.Pcm => "PCM (sin comprimir)",
        _ => codec.ToString(),
    };

    public static string ContainerName(ExportContainer container) => container switch
    {
        ExportContainer.Mp4 => "MP4",
        ExportContainer.Mov => "MOV (QuickTime)",
        ExportContainer.Mkv => "MKV (Matroska)",
        ExportContainer.WebM => "WebM",
        ExportContainer.Avi => "AVI",
        ExportContainer.Mxf => "MXF OP1a",
        ExportContainer.MpegTs => "MPEG-TS",
        ExportContainer.Gif => "GIF animado",
        ExportContainer.WebP => "WebP animado",
        ExportContainer.ImageSequence => "Secuencia de imágenes",
        ExportContainer.M4a => "M4A (solo audio)",
        ExportContainer.Mp3 => "MP3 (solo audio)",
        ExportContainer.Flac => "FLAC (solo audio)",
        ExportContainer.Wav => "WAV (solo audio)",
        ExportContainer.Ogg => "Ogg Opus (solo audio)",
        ExportContainer.Aiff => "AIFF (solo audio)",
        _ => container.ToString(),
    };

    public static string RateChoiceName(RateChoice choice) => choice switch
    {
        RateChoice.Quality => "Calidad constante",
        RateChoice.Vbr => "Tasa variable (VBR)",
        RateChoice.Cbr => "Tasa constante (CBR)",
        RateChoice.TargetSize => "Tamaño objetivo",
        RateChoice.Lossless => "Sin pérdidas",
        _ => "La fija el perfil",
    };
}

/// <summary>Cómo se elige la tasa en el diálogo (el tamaño objetivo es una tasa media con dos pasadas).</summary>
public enum RateChoice
{
    Quality,
    Vbr,
    Cbr,
    TargetSize,
    Lossless,
    ProfileDefined,
}
