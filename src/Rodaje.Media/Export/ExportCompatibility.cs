using System.Collections.Frozen;

namespace Rodaje.Media.Export;

/// <summary>Por qué una combinación no vale. Estable: la interfaz elige el texto y la acción por código.</summary>
public enum ExportProblemCode
{
    VideoCodecNotInContainer,
    AudioCodecNotInContainer,
    AudioOnlyContainer,
    NoAudioInContainer,
    PixelFormatNotInEncoder,
    PixelFormatNotInProfile,
    AlphaNotSupported,
    RateControlNotSupported,
    TwoPassNotSupported,
    QualityOutOfRange,
    ProfileUnknown,
    LevelUnknown,
    TooManyBFrames,
    SampleRateNotSupported,
    TooManyChannels,
    NoStreams,
    InvalidValue,
}

/// <summary>Un problema de la combinación elegida, con el motivo en español.</summary>
public sealed record ExportProblem(ExportProblemCode Code, string Message);

/// <summary>
/// Matriz contenedor × códec (y, con <see cref="EncoderSpec"/>, × píxel × perfil): qué se puede ofrecer en el diálogo.
/// </summary>
/// <remarks>
/// Tabla inicial del paso 0 de la fase 6, con lo que el FFmpeg 9 LGPL mezcla sin avisos. La nube la completa y la valida
/// con archivos reales (tarea «matriz de contenedores y validación»); si cambia una fila, cambia su prueba.
/// </remarks>
public static class ExportCompatibility
{
    private static readonly FrozenDictionary<ExportContainer, Row> Rows = new Dictionary<ExportContainer, Row>
    {
        [ExportContainer.Mp4] = new("mp4", "mp4",
            [ExportCodec.H264, ExportCodec.Hevc, ExportCodec.Av1, ExportCodec.Vp9],
            [ExportCodec.Aac, ExportCodec.Opus, ExportCodec.Mp3, ExportCodec.Flac, ExportCodec.Alac]),
        [ExportContainer.Mov] = new("mov", "mov",
            [ExportCodec.H264, ExportCodec.Hevc, ExportCodec.ProRes, ExportCodec.DnxHr, ExportCodec.CineForm, ExportCodec.Mjpeg,
                ExportCodec.Png, ExportCodec.QtAnimation],
            [ExportCodec.Aac, ExportCodec.Alac, ExportCodec.Pcm, ExportCodec.Mp3]),
        [ExportContainer.Mkv] = new("matroska", "mkv",
            [ExportCodec.H264, ExportCodec.Hevc, ExportCodec.Av1, ExportCodec.Vp9, ExportCodec.ProRes, ExportCodec.DnxHr,
                ExportCodec.Ffv1, ExportCodec.Mjpeg, ExportCodec.Png],
            [ExportCodec.Aac, ExportCodec.Opus, ExportCodec.Mp3, ExportCodec.Flac, ExportCodec.Alac, ExportCodec.Pcm]),
        [ExportContainer.WebM] = new("webm", "webm", [ExportCodec.Vp9, ExportCodec.Av1], [ExportCodec.Opus]),
        [ExportContainer.Avi] = new("avi", "avi",
            [ExportCodec.H264, ExportCodec.Mjpeg, ExportCodec.Ffv1, ExportCodec.Png], [ExportCodec.Pcm, ExportCodec.Mp3]),
        [ExportContainer.Mxf] = new("mxf", "mxf", [ExportCodec.DnxHr, ExportCodec.H264], [ExportCodec.Pcm]),
        [ExportContainer.MpegTs] = new("mpegts", "ts", [ExportCodec.H264, ExportCodec.Hevc], [ExportCodec.Aac, ExportCodec.Mp3]),
        [ExportContainer.Gif] = new("gif", "gif", [ExportCodec.Gif], []),
        [ExportContainer.WebP] = new("webp", "webp", [ExportCodec.WebP], []),
        [ExportContainer.ImageSequence] = new("image2", "png",
            [ExportCodec.Png, ExportCodec.Tiff, ExportCodec.Mjpeg, ExportCodec.Exr], []),
        [ExportContainer.M4a] = new("ipod", "m4a", [], [ExportCodec.Aac, ExportCodec.Alac]),
        [ExportContainer.Mp3] = new("mp3", "mp3", [], [ExportCodec.Mp3]),
        [ExportContainer.Flac] = new("flac", "flac", [], [ExportCodec.Flac]),
        [ExportContainer.Wav] = new("wav", "wav", [], [ExportCodec.Pcm]),
        [ExportContainer.Ogg] = new("ogg", "opus", [], [ExportCodec.Opus, ExportCodec.Flac]),
        [ExportContainer.Aiff] = new("aiff", "aiff", [], [ExportCodec.Pcm]),
    }.ToFrozenDictionary();

    /// <summary>Contenedores sin video: solo existen ahí los preajustes de solo audio.</summary>
    public static bool IsAudioOnly(this ExportContainer container) => Get(container).Video.Count == 0;

    /// <summary>Contenedores sin pista de audio (GIF, WebP, secuencias de imágenes).</summary>
    public static bool AllowsAudio(this ExportContainer container) => Get(container).Audio.Count > 0;

    /// <summary>Nombre de <c>-f</c> en FFmpeg.</summary>
    public static string Muxer(this ExportContainer container) => Get(container).Muxer;

    /// <summary>Extensión sin punto; en <see cref="ExportContainer.ImageSequence"/>, la del PNG (la del códec manda).</summary>
    public static string Extension(this ExportContainer container) => Get(container).Extension;

    public static IReadOnlyList<ExportCodec> VideoCodecs(this ExportContainer container) => Get(container).Video;

    public static IReadOnlyList<ExportCodec> AudioCodecs(this ExportContainer container) => Get(container).Audio;

    public static bool Allows(this ExportContainer container, ExportCodec codec) =>
        codec.IsAudio() ? Get(container).Audio.Contains(codec) : Get(container).Video.Contains(codec);

    /// <summary>
    /// Problemas de la combinación contenedor × codificador de video × píxel × perfil × codificador de audio.
    /// Vacía si se puede exportar así. Los ajustes finos (tasas, GOP, sonoridad) los valida <c>Rodaje.Export</c>.
    /// </summary>
    public static IReadOnlyList<ExportProblem> Check(ExportContainer container, EncoderSpec? video, ExportPixelFormat? pixelFormat,
        EncoderSpec? audio, string? profile = null)
    {
        var problems = new List<ExportProblem>();
        Row row = Get(container);
        if (video is null && audio is null)
        {
            problems.Add(new(ExportProblemCode.NoStreams, "No hay ni video ni audio que exportar."));
        }

        if (video is not null)
        {
            CheckVideo(container, row, video, pixelFormat, profile, problems);
        }

        if (audio is not null)
        {
            if (row.Audio.Count == 0)
            {
                problems.Add(new(ExportProblemCode.NoAudioInContainer, $"{Name(container)} no lleva audio."));
            }
            else if (!row.Audio.Contains(audio.Codec))
            {
                problems.Add(new(ExportProblemCode.AudioCodecNotInContainer,
                    $"{Name(container)} no admite audio {audio.Codec}; admite {string.Join(", ", row.Audio)}."));
            }
        }

        return problems;
    }

    /// <summary>Nombre del contenedor que ve el usuario.</summary>
    public static string Name(ExportContainer container) => container switch
    {
        ExportContainer.MpegTs => "MPEG-TS",
        ExportContainer.ImageSequence => "La secuencia de imágenes",
        ExportContainer.WebM => "WebM",
        ExportContainer.WebP => "WebP",
        _ => container.ToString().ToUpperInvariant(),
    };

    private static void CheckVideo(ExportContainer container, Row row, EncoderSpec video, ExportPixelFormat? pixelFormat, string? profile,
        List<ExportProblem> problems)
    {
        if (video.IsAudio)
        {
            throw new ArgumentException($"{video.Id} es un codificador de audio.", nameof(video));
        }

        if (row.Video.Count == 0)
        {
            problems.Add(new(ExportProblemCode.AudioOnlyContainer, $"{Name(container)} es solo de audio."));
            return;
        }

        if (!row.Video.Contains(video.Codec))
        {
            problems.Add(new(ExportProblemCode.VideoCodecNotInContainer,
                $"{Name(container)} no admite {video.Codec}; admite {string.Join(", ", row.Video)}."));
        }

        if (pixelFormat is { } pixels && !video.PixelFormats.Contains(pixels))
        {
            problems.Add(new(pixels.HasAlpha() && !video.SupportsAlpha ? ExportProblemCode.AlphaNotSupported : ExportProblemCode.PixelFormatNotInEncoder,
                $"{video.Id} no acepta {pixels.FfmpegName()}."));
        }

        if (profile is not null)
        {
            EncoderProfile? match = video.Profiles.FirstOrDefault(p => p.Id == profile);
            if (match is null)
            {
                problems.Add(new(ExportProblemCode.ProfileUnknown, $"{video.Id} no tiene el perfil «{profile}»."));
            }
            else if (pixelFormat is { } chosen && !match.Allows(chosen))
            {
                problems.Add(new(ExportProblemCode.PixelFormatNotInProfile, $"El perfil {match.Name} no admite {chosen.FfmpegName()}."));
            }
        }
    }

    private static Row Get(ExportContainer container) =>
        Rows.TryGetValue(container, out Row? row) ? row : throw new ArgumentOutOfRangeException(nameof(container), container, "Contenedor desconocido.");

    private sealed record Row(string Muxer, string Extension, IReadOnlyList<ExportCodec> Video, IReadOnlyList<ExportCodec> Audio);
}
