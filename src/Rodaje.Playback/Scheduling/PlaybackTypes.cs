using Rodaje.Core.Timing;

namespace Rodaje.Playback;

public sealed class PlaybackOptions
{
    /// <summary>Cuadros que se piden por adelantado, contando el que toca mostrar.</summary>
    /// <remarks>
    /// La caché debería caber al menos <c>2 * PrefetchFrames + 2</c> cuadros: es LRU y mostrar un cuadro lo renueva,
    /// así que un cuadro adelantado envejece mientras se muestran los anteriores y, con menos lugar, se expulsa antes de verse.
    /// </remarks>
    public int PrefetchFrames { get; init; } = 8;
}

/// <summary>Lo que el programador decidió mostrar en un refresco de pantalla.</summary>
/// <param name="Frame">Cuadro a pintar, o <c>null</c> si todavía no hay ninguno. Válido hasta el siguiente <see cref="PlaybackScheduler.Tick"/>.</param>
/// <param name="TargetFrame">Cuadro que correspondía según el reloj.</param>
/// <param name="SourceTime">Posición del reloj llevada al tiempo del origen.</param>
/// <param name="IsExact">Si <paramref name="Frame"/> es justo <paramref name="TargetFrame"/>.</param>
public readonly record struct FramePresentation(DecodedFrame? Frame, long TargetFrame, Time SourceTime, bool IsExact);

/// <param name="FramesShown">Cuadros distintos que llegaron a pantalla.</param>
/// <param name="FramesDropped">Cuadros que el reloj dejó atrás durante la reproducción sin que se mostraran.</param>
/// <param name="LateRefreshes">Refrescos en reproducción en los que el cuadro debido no estaba listo.</param>
/// <param name="MaxAvOffset">Mayor distancia entre el reloj y el PTS del cuadro mostrado; en sincronía es menor que un cuadro.</param>
/// <param name="CacheHits">Veces que el cuadro debido ya estaba en la caché al necesitarlo.</param>
/// <param name="CacheMisses">Veces que no estaba.</param>
/// <param name="DecodeErrors">Decodificaciones que fallaron o devolvieron otro cuadro.</param>
public readonly record struct PlaybackStats(
    long FramesShown,
    long FramesDropped,
    long LateRefreshes,
    Time MaxAvOffset,
    long CacheHits,
    long CacheMisses,
    long DecodeErrors);

public sealed class FrameReadyEventArgs(long frameIndex) : EventArgs
{
    public long FrameIndex { get; } = frameIndex;
}

public sealed class DecodeFailedEventArgs(long frameIndex, Exception error) : EventArgs
{
    public long FrameIndex { get; } = frameIndex;

    public Exception Error { get; } = error;
}
