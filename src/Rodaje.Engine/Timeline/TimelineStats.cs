namespace Rodaje.Engine.Timeline;

/// <summary>Contadores de una capa de video (una pista visible).</summary>
/// <param name="TrackIndex">Índice de la pista; 0 es la capa de más abajo.</param>
/// <param name="FramesShown">Cuadros distintos que llegaron a pantalla.</param>
/// <param name="FramesDropped">Cuadros de un mismo clip que el reloj dejó atrás sin mostrarse.</param>
/// <param name="LateRefreshes">Refrescos en reproducción en los que el cuadro debido no estaba listo (sin contar la espera de un seek).</param>
/// <param name="Cuts">Cortes atravesados reproduciendo: la capa pasó a otro clip o salió de un hueco.</param>
/// <param name="PreRollHits">Cortes cuyo primer cuadro ya estaba decodificado al llegar.</param>
/// <param name="PreRollMisses">Cortes que llegaron sin su primer cuadro.</param>
/// <param name="LateAtCuts">Refrescos tarde entre un corte sin su cuadro y la llegada de ese cuadro.</param>
public readonly record struct LayerStats(
    int TrackIndex,
    string TrackId,
    long FramesShown = 0,
    long FramesDropped = 0,
    long LateRefreshes = 0,
    long Cuts = 0,
    long PreRollHits = 0,
    long PreRollMisses = 0,
    long LateAtCuts = 0);

/// <summary>Estado del audio mezclado.</summary>
/// <param name="DecodersOpen">Decodificadores de audio abiertos ahora.</param>
/// <param name="Underruns">Lecturas del dispositivo que no encontraron la mezcla lista.</param>
/// <param name="Errors">Medios que fallaron al abrirse o decodificarse; quedan en silencio.</param>
public readonly record struct TimelineAudioStats(
    bool HasDevice,
    string Status,
    int DecodersOpen,
    long Underruns,
    long Errors);

/// <summary>Estado medible de un <see cref="TimelineSession"/>.</summary>
/// <param name="Layers">Una entrada por capa visible, de abajo hacia arriba.</param>
/// <param name="DecodersOpen">Decodificadores de video abiertos ahora (uno por medio en uso).</param>
/// <param name="DecodersClosed">Decodificadores cerrados por falta de uso.</param>
/// <param name="CacheEvictions">Cuadros expulsados o descartados por el presupuesto de memoria o el tope por origen.</param>
/// <param name="DecoderSeeks">Búsquedas de keyframe de los decodificadores abiertos.</param>
public sealed record TimelineStats(
    IReadOnlyList<LayerStats> Layers,
    long FramesShown,
    long FramesDropped,
    long LateRefreshes,
    long Cuts,
    long PreRollHits,
    long PreRollMisses,
    long LateAtCuts,
    int DecodersOpen,
    int DecodersClosed,
    long CacheBytes,
    int CacheFrames,
    long CacheBudgetBytes,
    long CacheEvictions,
    long DecoderSeeks,
    long DecodeErrors,
    long Seeks,
    TimelineAudioStats Audio)
{
    /// <summary>Fracción de cortes que llegaron con su primer cuadro listo; 1 si no hubo cortes.</summary>
    public double PreRollHitRate => PreRollHits + PreRollMisses == 0 ? 1 : (double)PreRollHits / (PreRollHits + PreRollMisses);
}

/// <summary>Un medio no se pudo abrir (<see cref="FrameIndex"/> es -1) o un cuadro no se pudo decodificar.</summary>
public sealed class TimelineDecodeFailedEventArgs(string mediaId, string path, long frameIndex, Exception error) : EventArgs
{
    public string MediaId { get; } = mediaId;

    public string Path { get; } = path;

    public long FrameIndex { get; } = frameIndex;

    public Exception Error { get; } = error;
}
