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
/// <param name="UsesProxy">El cuadro en pantalla sale de un proxy de vista previa.</param>
/// <param name="Failure">Por qué la capa está vacía: el medio de su clip no se pudo abrir. Nulo si no falla.</param>
/// <param name="Approximated">Refrescos reproduciendo de 16× a 100× que mostraron el keyframe anterior al cuadro debido.</param>
public readonly record struct LayerStats(
    int TrackIndex,
    string TrackId,
    long FramesShown = 0,
    long FramesDropped = 0,
    long LateRefreshes = 0,
    long Cuts = 0,
    long PreRollHits = 0,
    long PreRollMisses = 0,
    long LateAtCuts = 0,
    bool UsesProxy = false,
    string? Failure = null,
    long Approximated = 0);

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
/// <param name="DecodersOpen">Decodificadores de video abiertos ahora (uno o más por medio en uso).</param>
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

    /// <summary>VRAM que pueden ocupar entre todos los pools de D3D11VA de los decodificadores.</summary>
    public long VramBudgetBytes { get; init; }

    /// <summary>VRAM reservada ahora: el pool completo de cada decodificador abierto o abriéndose.</summary>
    public long VramCommittedBytes { get; init; }

    /// <summary>Mayor <see cref="VramCommittedBytes"/> desde la apertura o el último <c>ResetStats</c>.</summary>
    public long VramPeakBytes { get; init; }

    /// <summary>Decodificadores que se abrieron pasando el presupuesto porque una capa visible no tenía otro.</summary>
    public long VramOverruns { get; init; }

    /// <summary>Medios cuyo archivo no se pudo abrir; sus capas quedan vacías.</summary>
    public int FailedMedia { get; init; }

    /// <summary>Decodificadores abiertos sobre un proxy.</summary>
    public int ProxyDecoders { get; init; }

    /// <summary>Refrescos tras un seek que repitieron la última composición completa porque alguna capa no tenía su cuadro.</summary>
    public long HeldRefreshes { get; init; }

    /// <summary>Seeks en los que se agotó la espera y se mostró una composición a medias.</summary>
    public long PartialPresentations { get; init; }

    /// <summary>Transiciones de paquete cuyo contenido falta: se ven como fundido cruzado.</summary>
    public int MissingContent { get; init; }

    /// <summary>Transiciones que empezaron con el primer cuadro del clip que entra ya decodificado.</summary>
    public long TransitionPreRollHits { get; init; }

    /// <summary>Transiciones que empezaron sin ese cuadro: mientras falta se ve solo el clip que sale.</summary>
    public long TransitionPreRollMisses { get; init; }

    /// <summary>Refrescos en una transición en los que el cuadro debido del clip que entra no estaba listo.</summary>
    public long TransitionLateRefreshes { get; init; }

    /// <summary>
    /// Refrescos (sumados por capa) que, reproduciendo de 16× a 100×, mostraron el keyframe anterior al cuadro debido en
    /// lugar de decodificarlo; en pausa y tras un seek el cuadro siempre es exacto.
    /// </summary>
    public long Approximated { get; init; }

    /// <summary>
    /// Refrescos reproduciendo en que un título visible todavía no estaba rasterizado: el cuadro sale sin él. Solo se
    /// cuenta con <see cref="TimelineSessionOptions.Titles"/>.
    /// </summary>
    public long LateTitles { get; init; }

    /// <summary>
    /// Decodificadores que se abrieron con el reloj corriendo, desde la apertura o el último <c>ResetStats</c>. Con
    /// D3D11VA cada apertura crea el decodificador de video con el candado del contexto inmediato tomado, y la composición
    /// espera: es la causa probable de las pausas en los cortes (ver <c>docs/arquitectura/motor.md</c>).
    /// </summary>
    public long DecodersOpenedWhilePlaying { get; init; }

    /// <summary>Fracción de transiciones que empezaron a tiempo; 1 si no hubo.</summary>
    public double TransitionPreRollHitRate =>
        TransitionPreRollHits + TransitionPreRollMisses == 0 ? 1 : (double)TransitionPreRollHits / (TransitionPreRollHits + TransitionPreRollMisses);
}

/// <summary>Un medio no se pudo abrir (<see cref="FrameIndex"/> es -1) o un cuadro no se pudo decodificar.</summary>
public sealed class TimelineDecodeFailedEventArgs(string mediaId, string path, long frameIndex, Exception error) : EventArgs
{
    public string MediaId { get; } = mediaId;

    public string Path { get; } = path;

    public long FrameIndex { get; } = frameIndex;

    public Exception Error { get; } = error;
}
