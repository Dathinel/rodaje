using Xunit;

namespace FotoToma.TestMedia;

/// <summary>
/// Prueba que necesita ffmpeg y, opcionalmente, un codificador de video y uno de audio que
/// funcionen. Si faltan, la prueba se omite con el motivo en vez de fallar.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        Skip = SkipReason(null, AudioCodec.None);
    }

    public FfmpegFactAttribute(VideoCodec video)
    {
        Skip = SkipReason(video, AudioCodec.None);
    }

    public FfmpegFactAttribute(VideoCodec video, AudioCodec audio)
    {
        Skip = SkipReason(video, audio);
    }

    internal static string? SkipReason(VideoCodec? video, AudioCodec audio)
    {
        FfmpegTool? ffmpeg = FfmpegTool.Default;
        if (ffmpeg is null)
        {
            return "Omitida: " + FfmpegTool.UnavailableReason;
        }

        if (video is { } codec && ffmpeg.FindVideoEncoder(codec) is null)
        {
            string tried = string.Join(", ", FfmpegTool.VideoEncoderCandidates(codec));
            return $"Omitida: ningún codificador de {codec} funciona con {ffmpeg.ExecutablePath} (se probaron {tried}).";
        }

        if (audio != AudioCodec.None && ffmpeg.ProbeEncoder(FfmpegTool.AudioEncoderName(audio)) is { } reason)
        {
            return "Omitida: " + reason;
        }

        return null;
    }
}
