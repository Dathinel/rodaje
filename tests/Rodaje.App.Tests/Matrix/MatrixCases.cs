using System.Security.Cryptography;
using System.Text;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Matrix;

internal enum MatrixKind
{
    Video,
    AudioOnly,
    Image,
    ImageSequence,
}

/// <summary>
/// Un archivo de la matriz de formatos: cuadros de <c>SyntheticVideo</c> (con su número dibujado) y pitidos de
/// 440 Hz cada medio segundo, codificados con el <c>ffmpeg.exe</c> del entorno con los argumentos del caso.
/// </summary>
internal sealed record MatrixCase(string Name, string Container, string VideoLabel, string AudioLabel, string Extension)
{
    public MatrixKind Kind { get; init; } = MatrixKind.Video;
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public Rational Rate { get; init; } = Rational.Fps30;
    public double Seconds { get; init; } = 2.5;

    /// <summary>Argumentos de video tras las entradas (filtro, códec, formato de píxel, color).</summary>
    public string[] Video { get; init; } = [];

    /// <summary>Argumentos de audio; vacío si no lleva.</summary>
    public string[] Audio { get; init; } = [];

    public string[] Output { get; init; } = [];

    /// <summary>Rotación de pantalla escrita en los metadatos al remultiplexar (grados).</summary>
    public int Rotation { get; init; }

    /// <summary>Marcas de tiempo irregulares, como un teléfono.</summary>
    public bool VariableRate { get; init; }

    /// <summary>Filtro que da las marcas irregulares: en 1/90000 s, o en la base del codificador (1/30), que repite marcas.</summary>
    public string VariableRateFilter { get; init; } = "settb=1/90000,setpts=(N+0.3*sin(N))/(FRAME_RATE*TB)";

    /// <summary>Codificador que debe funcionar para generar el caso (si no, se anota y se omite).</summary>
    public string? Requires { get; init; }

    /// <summary>Si Rodaje lo rechaza a propósito: texto que debe aparecer en el motivo.</summary>
    public string? ExpectedProblem { get; init; }

    /// <summary>Archivo de voz real (de <c>.entorno\corpus</c>) en lugar de los pitidos, desde <see cref="VoiceOffset"/> s.</summary>
    public string? Voice { get; init; }

    public double VoiceOffset { get; init; }

    /// <summary>Los cuadros llevan su número dibujado (falso en los videos del autor).</summary>
    public bool FrameCodes { get; init; } = true;

    public bool HasAudio => Audio.Length > 0;

    public bool HasVideo => Kind != MatrixKind.AudioOnly;

    public override string ToString() => Name;

    public string CacheKey()
    {
        string canonical = string.Join('|', MatrixMedia.FormatVersion, Kind, Width, Height, Rate, Seconds, string.Join(' ', Video),
            string.Join(' ', Audio), string.Join(' ', Output), Rotation, VariableRate ? VariableRateFilter : string.Empty, Extension,
            Voice is null ? string.Empty : Path.GetFileName(Voice) + "@" + VoiceOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)).AsSpan(0, 6));
    }
}

internal static class MatrixCases
{
    private static readonly string[] Aac = ["-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2"];
    private static readonly string[] Pcm16 = ["-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2"];
    private static readonly string[] Pcm24 = ["-c:a", "pcm_s24le", "-ar", "48000", "-ac", "2"];
    private static readonly string[] Opus = ["-c:a", "libopus", "-b:a", "128k", "-ac", "2"];
    private static readonly string[] Bt709 = ["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv"];
    private static readonly string[] Nvenc = ["-c:v", "h264_nvenc", "-preset", "p4", "-cq", "18", "-g", "30", "-bf", "2", "-pix_fmt", "yuv420p", .. Bt709];
    private static readonly string[] Hevc10 = ["-c:v", "hevc_nvenc", "-preset", "p4", "-cq", "20", "-g", "30", "-profile:v", "main10", "-pix_fmt", "p010le", "-tag:v", "hvc1"];

    public static IReadOnlyList<MatrixCase> All { get; } =
    [
        // Contenedores con H.264 y el audio de cada uno.
        new("mp4-h264-aac", "MP4", "H.264 8 bits 4:2:0 con B", "AAC estéreo 48 kHz", ".mp4") { Video = Nvenc, Audio = Aac, Requires = "h264_nvenc" },
        new("mov-h264-pcm16", "MOV", "H.264 8 bits 4:2:0", "PCM 16 estéreo 48 kHz", ".mov") { Video = Nvenc, Audio = Pcm16, Requires = "h264_nvenc" },
        new("mkv-h264-flac96", "MKV", "H.264 8 bits 4:2:0", "FLAC estéreo 96 kHz", ".mkv") { Video = Nvenc, Audio = ["-c:a", "flac", "-ar", "96000", "-ac", "2"], Requires = "h264_nvenc" },
        new("m4v-h264-aac", "M4V", "H.264 8 bits 4:2:0", "AAC estéreo 48 kHz", ".m4v") { Video = Nvenc, Audio = Aac, Output = ["-f", "ipod"], Requires = "h264_nvenc" },
        new("mp4-h264-mp3-mono44", "MP4", "H.264 8 bits 4:2:0", "MP3 mono 44,1 kHz", ".mp4") { Video = Nvenc, Audio = ["-c:a", "libmp3lame", "-b:a", "128k", "-ar", "44100", "-ac", "1"], Requires = "h264_nvenc" },
        new("mkv-h264-aac51", "MKV", "H.264 8 bits 4:2:0", "AAC 5.1 48 kHz", ".mkv") { Video = Nvenc, Audio = ["-c:a", "aac", "-b:a", "384k", "-ar", "48000", "-ac", "6"], Requires = "h264_nvenc" },
        new("mov-h264-444", "MOV", "H.264 High 4:4:4", "AAC estéreo 48 kHz", ".mov")
        {
            Video = ["-c:v", "h264_nvenc", "-preset", "p4", "-cq", "18", "-g", "30", "-pix_fmt", "yuv444p", .. Bt709], Audio = Aac, Requires = "h264_nvenc",
        },
        new("mov-h264-23976", "MOV", "H.264 8 bits a 23,976", "AAC estéreo 48 kHz", ".mov") { Rate = Rational.Ntsc24, Video = Nvenc, Audio = Aac, Requires = "h264_nvenc" },
        new("mp4-h264-sin-audio", "MP4", "H.264 8 bits 4:2:0", "sin audio", ".mp4") { Video = Nvenc, Requires = "h264_nvenc" },

        // HEVC, AV1, VP9.
        new("mkv-hevc8-opus", "MKV", "HEVC 8 bits", "Opus estéreo 48 kHz", ".mkv")
        {
            Video = ["-c:v", "hevc_nvenc", "-preset", "p4", "-cq", "20", "-g", "30", "-pix_fmt", "yuv420p", .. Bt709], Audio = Opus, Requires = "hevc_nvenc",
        },
        new("mp4-hevc10-aac", "MP4", "HEVC Main 10", "AAC estéreo 48 kHz", ".mp4") { Video = [.. Hevc10, .. Bt709], Audio = Aac, Requires = "hevc_nvenc" },
        new("mkv-av1-opus", "MKV", "AV1 8 bits (SVT-AV1)", "Opus estéreo 48 kHz", ".mkv")
        {
            Video = ["-c:v", "libsvtav1", "-preset", "8", "-crf", "20", "-g", "30", "-pix_fmt", "yuv420p", .. Bt709], Audio = Opus, Requires = "libsvtav1",
        },
        new("webm-vp9-opus", "WebM", "VP9 8 bits", "Opus estéreo 48 kHz", ".webm")
        {
            Video = ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-crf", "32", "-b:v", "0", "-g", "30", "-pix_fmt", "yuv420p", .. Bt709],
            Audio = Opus, Requires = "libvpx-vp9",
        },

        // Intermedios de edición.
        new("mov-prores422-pcm24", "MOV", "ProRes 422 HQ 10 bits", "PCM 24 estéreo 48 kHz", ".mov")
        {
            Width = 1920, Height = 1080, Video = ["-c:v", "prores_ks", "-profile:v", "3", "-pix_fmt", "yuv422p10le", "-vendor", "apl0", .. Bt709], Audio = Pcm24,
        },
        new("mov-prores4444-alfa", "MOV", "ProRes 4444 con alfa", "PCM 24 estéreo 48 kHz", ".mov")
        {
            Video = ["-vf", "format=yuva444p10le,geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='if(lt(X,W/2),1023,512)'",
                "-c:v", "prores_ks", "-profile:v", "4", "-pix_fmt", "yuva444p10le", "-alpha_bits", "16", .. Bt709],
            Audio = Pcm24,
        },
        new("mxf-dnxhr-pcm", "MXF", "DNxHR HQ 4:2:2", "PCM 16 estéreo 48 kHz", ".mxf")
        {
            Width = 1920, Height = 1080, Rate = new Rational(25, 1), Video = ["-c:v", "dnxhd", "-profile:v", "dnxhr_hq", "-pix_fmt", "yuv422p", .. Bt709], Audio = Pcm16,
        },
        new("avi-mjpeg-pcm", "AVI", "MJPEG 4:2:0", "PCM 16 estéreo 48 kHz", ".avi") { Video = ["-c:v", "mjpeg", "-q:v", "3", "-pix_fmt", "yuvj420p"], Audio = Pcm16 },
        new("ts-mpeg2-ac3", "MPEG-TS", "MPEG-2", "AC-3 5.1 48 kHz", ".ts")
        {
            Rate = new Rational(25, 1),
            Video = ["-c:v", "mpeg2video", "-q:v", "3", "-g", "12", "-bf", "2", "-pix_fmt", "yuv420p", .. Bt709],
            Audio = ["-c:a", "ac3", "-b:a", "384k", "-ar", "48000", "-ac", "6"], Requires = "ac3",
        },

        // Cámara y teléfono.
        new("mp4-vfr-telefono", "MP4", "H.264 1080×1920 VFR", "AAC estéreo 48 kHz", ".mp4")
        {
            Width = 1080, Height = 1920, Video = [.. Nvenc, "-fps_mode", "passthrough", "-enc_time_base", "1/90000", "-video_track_timescale", "90000"], Audio = Aac, VariableRate = true, Requires = "h264_nvenc",
        },
        new("mp4-vfr-marcas-repetidas", "MP4", "H.264 VFR con marcas repetidas", "AAC estéreo 48 kHz", ".mp4")
        {
            Video = [.. Nvenc, "-fps_mode", "passthrough", "-video_track_timescale", "90000"], Audio = Aac, VariableRate = true, Requires = "h264_nvenc",
            VariableRateFilter = "setpts=(N+0.3*sin(N))/(FRAME_RATE*TB)",
        },
        new("mp4-rotado-90", "MP4", "H.264 con rotación 90° en metadatos", "AAC estéreo 48 kHz", ".mp4")
        {
            Width = 1920, Height = 1080, Video = Nvenc, Audio = Aac, Rotation = 90, Requires = "h264_nvenc",
        },
        new("mp4-4k60", "MP4", "H.264 3840×2160 a 60", "AAC estéreo 48 kHz", ".mp4")
        {
            Width = 3840, Height = 2160, Rate = new Rational(60, 1), Seconds = 2, Video = Nvenc, Audio = Aac, Requires = "h264_nvenc",
        },
        new("mp4-720p-aac44", "MP4", "H.264 1280×720", "AAC estéreo 44,1 kHz", ".mp4") { Video = Nvenc, Audio = ["-c:a", "aac", "-ar", "44100", "-ac", "2"], Requires = "h264_nvenc" },
        new("mp4-vertical", "MP4", "H.264 1080×1920", "AAC estéreo 48 kHz", ".mp4") { Width = 1080, Height = 1920, Video = Nvenc, Audio = Aac, Requires = "h264_nvenc" },
        new("mp4-hdr10-pq", "MP4", "HEVC Main 10 HDR10 (PQ, BT.2020)", "AAC estéreo 48 kHz", ".mp4")
        {
            Width = 1920, Height = 1080, Audio = Aac, Requires = "hevc_nvenc",
            Video = ["-vf", "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=tv", .. Hevc10],
        },
        new("mov-hlg", "MOV", "HEVC Main 10 HLG (BT.2020)", "AAC estéreo 48 kHz", ".mov")
        {
            Width = 1920, Height = 1080, Audio = Aac, Requires = "hevc_nvenc",
            Video = ["-vf", "setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc:range=tv", .. Hevc10],
        },

        // Solo audio.
        new("wav-pcm16-48", "WAV", "—", "PCM 16 estéreo 48 kHz", ".wav") { Kind = MatrixKind.AudioOnly, Audio = Pcm16 },
        new("wav-pcm24-96", "WAV", "—", "PCM 24 estéreo 96 kHz", ".wav") { Kind = MatrixKind.AudioOnly, Audio = ["-c:a", "pcm_s24le", "-ar", "96000", "-ac", "2"] },
        new("flac-mono-44", "FLAC", "—", "FLAC mono 44,1 kHz", ".flac") { Kind = MatrixKind.AudioOnly, Audio = ["-c:a", "flac", "-ar", "44100", "-ac", "1"] },
        new("mp3-estereo-44", "MP3", "—", "MP3 estéreo 44,1 kHz", ".mp3") { Kind = MatrixKind.AudioOnly, Audio = ["-c:a", "libmp3lame", "-b:a", "192k", "-ar", "44100", "-ac", "2"] },
        new("m4a-aac", "M4A", "—", "AAC estéreo 48 kHz", ".m4a") { Kind = MatrixKind.AudioOnly, Audio = Aac },
        new("opus-ogg", "Ogg", "—", "Opus estéreo 48 kHz", ".opus") { Kind = MatrixKind.AudioOnly, Audio = Opus },
        new("ac3-51", "AC-3", "—", "AC-3 5.1 48 kHz", ".ac3") { Kind = MatrixKind.AudioOnly, Audio = ["-c:a", "ac3", "-b:a", "448k", "-ar", "48000", "-ac", "6"], Requires = "ac3" },

        // Imágenes: un cuadro reutilizado durante toda la duración del clip.
        new("imagen-png", "PNG", "PNG RGB", "—", ".png") { Kind = MatrixKind.Image, Video = ["-c:v", "png", "-pix_fmt", "rgb24"] },
        new("imagen-jpeg", "JPEG", "JPEG", "—", ".jpg") { Kind = MatrixKind.Image, Video = ["-c:v", "mjpeg", "-q:v", "2", "-pix_fmt", "yuvj420p"] },
        new("imagen-webp", "WebP", "WebP", "—", ".webp") { Kind = MatrixKind.Image, Video = ["-c:v", "libwebp", "-quality", "90"], Requires = "libwebp" },
        new("secuencia-png", "PNG numeradas", "Secuencia de 30 PNG", "—", ".png")
        {
            Kind = MatrixKind.ImageSequence, Seconds = 1, Video = ["-c:v", "png", "-pix_fmt", "rgb24"],
        },
    ];

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (MatrixCase c in All)
        {
            data.Add(c.Name);
        }

        return data;
    }

    public static MatrixCase Get(string name) => All.Single(c => c.Name == name);
}
