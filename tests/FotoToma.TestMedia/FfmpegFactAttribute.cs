using Xunit;

namespace FotoToma.TestMedia;

/// <summary>
/// Prueba que necesita ffmpeg y, opcionalmente, un codificador de video y uno de audio que
/// funcionen. Si faltan, la prueba se omite con el motivo en vez de fallar.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FfmpegFactAttribute : FactAttribute
{
    private bool _longRunning;

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

    /// <summary>Prueba larga: solo corre con <c>FOTOTOMA_PRUEBAS_LARGAS=1</c> (ABRIR.bat aceptacion).</summary>
    public bool LongRunning
    {
        get => _longRunning;
        set
        {
            _longRunning = value;
            if (value && Skip is null && Environment.GetEnvironmentVariable("FOTOTOMA_PRUEBAS_LARGAS") != "1")
            {
                Skip = "Omitida: prueba larga; se activa con FOTOTOMA_PRUEBAS_LARGAS=1 (ABRIR.bat aceptacion).";
            }
        }
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
