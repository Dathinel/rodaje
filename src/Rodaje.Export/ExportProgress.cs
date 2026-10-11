namespace Rodaje.Export;

/// <summary>En qué va una exportación. El orden es el del trabajo; los tres últimos son finales.</summary>
public enum ExportJobState
{
    Queued,

    /// <summary>Sonda de codificadores, espacio en disco, precarga de shaders y LUT.</summary>
    Preparing,

    /// <summary>Pasada previa de sonoridad (solo si se normaliza).</summary>
    MeasuringLoudness,

    /// <summary>Mezcla del audio al WAV temporal.</summary>
    MixingAudio,

    /// <summary>Componiendo y codificando el video (en dos pasadas, la primera es de análisis).</summary>
    Encoding,

    /// <summary>Esperando a que <c>ffmpeg.exe</c> cierre el archivo y renombrando el temporal.</summary>
    Finalizing,
    Completed,
    Failed,
    Canceled,
}

public static class ExportJobStates
{
    public static bool IsFinished(this ExportJobState state) =>
        state is ExportJobState.Completed or ExportJobState.Failed or ExportJobState.Canceled;
}

/// <summary>Una foto del avance, para Trabajos → Exportaciones.</summary>
/// <param name="FramesDone">Cuadros de video que ya consumió el codificador en la pasada actual (o muestras/48 en el audio).</param>
/// <param name="FrameCount">Cuadros de la salida.</param>
/// <param name="FramesPerSecond">Media de los últimos segundos.</param>
/// <param name="Remaining">Estimación; <c>null</c> mientras no hay datos.</param>
/// <param name="BytesWritten">Tamaño del temporal hasta ahora.</param>
public readonly record struct ExportProgress(
    ExportJobState State,
    long FramesDone,
    long FrameCount,
    double FramesPerSecond,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    long BytesWritten,
    int Pass = 1,
    int PassCount = 1)
{
    /// <summary>Fracción total de 0 a 1, contando las pasadas.</summary>
    public double Fraction => State == ExportJobState.Completed ? 1
        : FrameCount <= 0 || PassCount <= 0 ? 0
        : Math.Clamp(((Pass - 1) + ((double)FramesDone / FrameCount)) / PassCount, 0, 1);
}
