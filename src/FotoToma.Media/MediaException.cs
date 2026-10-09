namespace FotoToma.Media;

/// <summary>Error al abrir, demultiplexar o decodificar un medio.</summary>
public sealed class MediaException : Exception
{
    public MediaException()
    {
    }

    public MediaException(string message)
        : base(message)
    {
    }

    public MediaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MediaException(string message, int ffmpegError)
        : base(message) => FfmpegError = ffmpegError;

    /// <summary>Código de error de FFmpeg (negativo), o 0 si el error no vino de FFmpeg.</summary>
    public int FfmpegError { get; }
}
