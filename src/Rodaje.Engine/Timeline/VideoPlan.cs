using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>
/// Clip de video listo para programar: su mapeo (<see cref="ClipTimeMap"/>), el tramo que se ve y sus efectos. En una
/// transición el clip se ve más allá de su duración: fuera de ella el origen sigue a la velocidad del borde
/// (<see cref="ClipTimeMap.ExtendedSource"/>) y, donde se acaba el material, se congela el cuadro del borde.
/// </summary>
/// <remarks>
/// Los cuadros «mostrados» son los que algún cuadro de la secuencia (a <see cref="SequenceRate"/>) llega a ver: con
/// velocidad, o con un origen más rápido que la secuencia, los demás se decodifican de paso pero no se guardan.
/// </remarks>
internal sealed class VideoClipPlan
{
    private readonly Time _materialEnd;
    private readonly long _firstGrid;
    private readonly long _lastGrid;
    private readonly bool _everyFrameShown;

    /// <param name="start">Desde cuándo se ve, contando la mitad de una transición antes del clip.</param>
    /// <param name="end">Hasta cuándo se ve (sin incluir), contando la mitad de una transición después.</param>
    /// <param name="materialEnd">Hasta dónde llega el material del medio; nunca menos que <c>map.SourceOut</c>.</param>
    public VideoClipPlan(string clipId, SourceKey source, ClipTimeMap map, Time start, Time end, Time materialEnd, Rational frameRate,
        Rational sequenceRate, EffectPlan? effects, int displayWidth = 0, int displayHeight = 0, bool isStillImage = false)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(end, start);
        IsStillImage = isStillImage;
        ClipId = clipId;
        Source = source;
        TimeMap = map;
        Start = start;
        End = end;
        _materialEnd = Time.Max(materialEnd, map.SourceOut);
        FrameRate = frameRate;
        SequenceRate = sequenceRate;
        Effects = effects;
        DisplayWidth = displayWidth;
        DisplayHeight = displayHeight;

        // Un origen que no va más rápido que la secuencia muestra todos sus cuadros: cada uno dura al menos un cuadro de ella.
        _everyFrameShown = map.Speed.IsIdentity && !map.Freeze
            && (Int128)frameRate.Num * sequenceRate.Den <= (Int128)sequenceRate.Num * frameRate.Den;
        long first = start.ToFrames(sequenceRate);
        _firstGrid = Time.FromFrames(first, sequenceRate) < start ? first + 1 : first;
        _lastGrid = (end - Time.FromTicks(1)).ToFrames(sequenceRate);
        MaxRate = map.RateRange(start, end).Max;
        bool seen = _firstGrid <= _lastGrid;
        FirstFrame = FrameAt(seen ? Time.FromFrames(_firstGrid, sequenceRate) : start);
        LastFrame = FrameAt(seen ? Time.FromFrames(_lastGrid, sequenceRate) : start);
    }

    public bool IsStillImage { get; }

    public string ClipId { get; }

    public SourceKey Source { get; }

    /// <summary>Mapeo del clip del modelo, sin los tramos de transición.</summary>
    public ClipTimeMap TimeMap { get; }

    /// <summary>Desde cuándo se ve.</summary>
    public Time Start { get; }

    /// <summary>Hasta cuándo se ve (sin incluir).</summary>
    public Time End { get; }

    /// <summary>Tasa del medio de origen, que puede diferir de la de la secuencia.</summary>
    public Rational FrameRate { get; }

    /// <summary>Tasa de la secuencia: la rejilla de instantes en que se muestra un cuadro.</summary>
    public Rational SequenceRate { get; }

    public EffectPlan? Effects { get; }

    /// <summary>La transición del corte donde termina este tramo, si la hay: el clip sale mientras dura.</summary>
    public TransitionPlan? Outgoing { get; init; }

    /// <summary>Tamaño en pantalla del original según el proyecto (0 si no se conoce); un proxy se ajusta con este.</summary>
    public int DisplayWidth { get; }

    public int DisplayHeight { get; }

    /// <summary>Primer cuadro del origen que se muestra.</summary>
    public long FirstFrame { get; }

    /// <summary>Último cuadro del origen que se muestra.</summary>
    public long LastFrame { get; }

    /// <summary>El cuadro de la secuencia en que el clip se ve por primera vez.</summary>
    public long FirstSequenceFrame => _firstGrid;

    /// <summary>Mayor velocidad en valor absoluto en el tramo que se ve (en una curva, su pico real); 0 congelado.</summary>
    public double MaxRate { get; }

    /// <summary>Los cuadros mostrados crecen con el tiempo; falso en reversa, donde decrecen.</summary>
    public bool Forward => !TimeMap.Reverse;

    /// <summary>El menor cuadro mostrado: <see cref="FirstFrame"/> hacia adelante, <see cref="LastFrame"/> en reversa.</summary>
    public long LowestFrame => Forward ? FirstFrame : LastFrame;

    /// <summary>El mayor cuadro mostrado.</summary>
    public long HighestFrame => Forward ? LastFrame : FirstFrame;

    public bool Covers(Time timeline) => timeline >= Start && timeline < End;

    /// <summary>Instante del origen para un instante de la línea de tiempo, acotado al tramo visible y al material.</summary>
    public Time ToSource(Time timeline)
    {
        Time t = timeline < Start ? Start : timeline >= End ? End - Time.FromTicks(1) : timeline;
        Time source = TimeMap.ExtendedSource(t);
        Time last = _materialEnd - Time.FromTicks(1);
        return source < Time.Zero ? Time.Zero : source > last ? last : source;
    }

    /// <summary>Cuadro del origen que contiene el instante: el último que empieza en o antes de él.</summary>
    public long FrameAt(Time timeline) => IsStillImage ? 0 : ToSource(timeline).ToFrames(FrameRate);

    /// <summary>
    /// Instante en que se evalúan los keyframes de los efectos: el del origen (<see cref="ClipTimeMap.EffectTime"/> dentro
    /// del clip, el material de la transición fuera) o, en un congelado, el virtual.
    /// </summary>
    public Time EffectTimeAt(Time timeline) =>
        IsStillImage || TimeMap.Freeze || (timeline >= TimeMap.Start && timeline < TimeMap.End) ? TimeMap.EffectTime(timeline) : ToSource(timeline);

    /// <summary>Velocidad en el instante (acotado al clip): negativa en reversa y 0 en un congelado.</summary>
    public double RateAt(Time timeline) => TimeMap.RateAt(timeline);

    /// <summary>Menor velocidad, en valor absoluto, entre <paramref name="from"/> y <paramref name="to"/> (sin incluir).</summary>
    public double MinRate(Time from, Time to) => TimeMap.RateRange(from, to).Min;

    /// <summary>
    /// Primer instante en que se ve el cuadro <paramref name="index"/> del origen o uno que va después en la reproducción
    /// (posterior hacia adelante, anterior en reversa); <see cref="End"/> si no llega.
    /// </summary>
    public Time ShownAt(long index)
    {
        if (!Forward)
        {
            return ShownAtReverse(index);
        }

        if (index <= FirstFrame)
        {
            return Start;
        }

        if (index > LastFrame)
        {
            return End;
        }

        Time source = Time.FromFrames(index, FrameRate);
        ClipTimeMap map = TimeMap;
        if (!map.HasSpeed)
        {
            return Time.Max(Start, map.Start + (source - map.SourceIn));
        }

        if (map.Start > Start && source < map.SourceIn)
        {
            return Search(Start, map.Start, source);
        }

        if (source <= map.Source(map.End - Time.FromTicks(1)))
        {
            return Time.Max(Start, map.TimelineOf(source));
        }

        return Search(Time.Max(Start, map.End), End, source);
    }

    /// <summary>
    /// El menor cuadro mostrado que no es anterior a <paramref name="index"/>; <see cref="long.MaxValue"/> si no hay. Va
    /// por los índices, no por el orden de reproducción: en reversa es el mostrado más cercano por encima.
    /// </summary>
    public long NextShown(long index)
    {
        if (!Forward)
        {
            return NextShownReverse(index);
        }

        if (index <= FirstFrame)
        {
            return FirstFrame;
        }

        if (index > LastFrame)
        {
            return long.MaxValue;
        }

        if (_everyFrameShown)
        {
            return index;
        }

        Time at = ShownAt(index);
        long grid = at.ToFrames(SequenceRate);
        if (Time.FromFrames(grid, SequenceRate) < at)
        {
            grid++;
        }

        return grid > _lastGrid ? long.MaxValue : FrameAt(Time.FromFrames(grid, SequenceRate));
    }

    /// <summary>Algún cuadro de la secuencia muestra el cuadro <paramref name="index"/> del origen.</summary>
    public bool IsShown(long index) => index >= LowestFrame && index <= HighestFrame && NextShown(index) == index;

    /// <summary>El mayor cuadro mostrado que no pasa de <paramref name="index"/>; -1 si no hay.</summary>
    public long PreviousShown(long index)
    {
        if (index < LowestFrame)
        {
            return -1;
        }

        if (index >= HighestFrame)
        {
            return HighestFrame;
        }

        if (_everyFrameShown)
        {
            return index;
        }

        if (!Forward)
        {
            // El primer cuadro de la secuencia que muestra uno que no pasa de index.
            long first = CeilingGrid(ShownAtReverse(index));
            return first > _lastGrid ? LastFrame : FrameAt(Time.FromFrames(first, SequenceRate));
        }

        // El último cuadro de la secuencia antes del primero que muestra uno mayor que index.
        long grid = CeilingGrid(ShownAt(index + 1)) - 1;
        return grid < _firstGrid ? FirstFrame : FrameAt(Time.FromFrames(Math.Min(grid, _lastGrid), SequenceRate));
    }

    /// <summary>Cuadros del origen entre <paramref name="after"/> y <paramref name="before"/>, sin contarlos, que la secuencia muestra.</summary>
    /// <remarks>Un origen más rápido que la secuencia, o con velocidad, salta cuadros por diseño; esos no son pérdidas.</remarks>
    /// <remarks>Va por los índices: en reversa, el cuadro nuevo es <paramref name="after"/> y el anterior <paramref name="before"/>.</remarks>
    public long ShownBetween(long after, long before)
    {
        long count = 0;
        long last = Math.Min(before - 1, HighestFrame);
        for (long i = NextShown(Math.Max(after + 1, LowestFrame)); i <= last; i = NextShown(i + 1))
        {
            count++;
        }

        return count;
    }

    /// <summary>Cota de los cuadros que quedan por mostrar desde el cuadro <paramref name="index"/> hasta el final del tramo.</summary>
    /// <remarks>En reversa, desde <paramref name="index"/> hacia abajo.</remarks>
    public long ShownFrom(long index)
    {
        if (Forward ? index > LastFrame : index < LastFrame)
        {
            return 0;
        }

        long grids = _lastGrid + 1 - Math.Max(_firstGrid, ShownAt(index).ToFrames(SequenceRate));
        long frames = Forward ? LastFrame + 1 - Math.Max(index, FirstFrame) : Math.Min(index, FirstFrame) + 1 - LastFrame;
        return Math.Max(0, Math.Min(frames, grids));
    }

    /// <summary>El cuadro mostrado en el cuadro <paramref name="sequenceFrame"/> de la secuencia, acotado al tramo visible.</summary>
    public long ShownUntil(long sequenceFrame) =>
        sequenceFrame >= _lastGrid ? LastFrame
        : sequenceFrame <= _firstGrid ? FirstFrame
        : FrameAt(Time.FromFrames(sequenceFrame, SequenceRate));

    /// <summary>Mismo origen y mismos tiempos: los cuadros que muestra son los mismos.</summary>
    public bool SameMapping(VideoClipPlan other) =>
        ClipId == other.ClipId && Source == other.Source && Start == other.Start && End == other.End && FrameRate == other.FrameRate
        && IsStillImage == other.IsStillImage && SequenceRate == other.SequenceRate && _materialEnd == other._materialEnd && TimeMap.Equals(other.TimeMap);

    /// <summary>En reversa: primer instante en que se ve el cuadro <paramref name="index"/> o uno menor.</summary>
    private Time ShownAtReverse(long index)
    {
        if (index >= FirstFrame)
        {
            return Start;
        }

        if (index < LastFrame)
        {
            return End;
        }

        // El cuadro mostrado no pasa de index cuando el origen no pasa del último tick de index.
        Time target = Time.FromFrames(index + 1, FrameRate) - Time.FromTicks(1);
        ClipTimeMap map = TimeMap;
        if (map.Start > Start && target >= map.SourceOut - Time.FromTicks(1))
        {
            return SearchDown(Start, map.Start, target);
        }

        if (target >= map.Source(map.End - Time.FromTicks(1)))
        {
            return Time.Max(Start, map.TimelineOf(target));
        }

        return SearchDown(Time.Max(Start, map.End), End, target);
    }

    /// <summary>En reversa: el menor cuadro mostrado que no es anterior a <paramref name="index"/>.</summary>
    private long NextShownReverse(long index)
    {
        if (index > FirstFrame)
        {
            return long.MaxValue;
        }

        if (index <= LastFrame)
        {
            return LastFrame;
        }

        if (_everyFrameShown)
        {
            return index;
        }

        // El último cuadro de la secuencia antes de que se vea uno menor que index muestra el buscado.
        long grid = CeilingGrid(ShownAtReverse(index - 1)) - 1;
        return grid < _firstGrid ? FirstFrame : FrameAt(Time.FromFrames(Math.Min(grid, _lastGrid), SequenceRate));
    }

    /// <summary>El primer cuadro de la secuencia que empieza en o después de <paramref name="time"/>.</summary>
    private long CeilingGrid(Time time)
    {
        long grid = time.ToFrames(SequenceRate);
        return Time.FromFrames(grid, SequenceRate) < time ? grid + 1 : grid;
    }

    /// <summary>Primer instante de <c>[from, to)</c> cuyo origen no pasa de <paramref name="source"/>, en reversa; <paramref name="to"/> si ninguno.</summary>
    private Time SearchDown(Time from, Time to, Time source)
    {
        long lo = from.Ticks;
        long hi = to.Ticks;
        while (lo < hi)
        {
            long mid = lo + ((hi - lo) / 2);
            if (ToSource(Time.FromTicks(mid)) <= source)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return Time.FromTicks(lo);
    }

    /// <summary>Primer instante de <c>[from, to)</c> cuyo origen alcanza <paramref name="source"/>; <paramref name="to"/> si ninguno.</summary>
    private Time Search(Time from, Time to, Time source)
    {
        long lo = from.Ticks;
        long hi = to.Ticks;
        while (lo < hi)
        {
            long mid = lo + ((hi - lo) / 2);
            if (ToSource(Time.FromTicks(mid)) >= source)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return Time.FromTicks(lo);
    }
}

/// <summary>
/// Un clip compuesto que se compone aparte: sus carriles (los de su secuencia, recortados a lo que se ve y llevados a la
/// línea de tiempo) se componen en una capa que recibe sus efectos, su transformación y su transición.
/// </summary>
/// <param name="clipStart">Inicio del clip en la línea de tiempo; con <paramref name="sourceIn"/> da el tiempo de sus keyframes.</param>
/// <param name="lanes">Los carriles de su grupo, de abajo arriba; en <see cref="Start"/>–<see cref="End"/> solo tienen lo suyo.</param>
internal sealed class GroupClipPlan(string clipId, Time clipStart, Time sourceIn, Time start, Time end, EffectPlan? effects,
    IReadOnlyList<VideoLayerPlan> lanes)
{
    public string ClipId { get; } = clipId;

    public Time Start { get; } = start;

    public Time End { get; } = end;

    public EffectPlan? Effects { get; } = effects;

    public IReadOnlyList<VideoLayerPlan> Lanes { get; } = lanes;

    /// <summary>La transición del corte donde termina, si la hay.</summary>
    public TransitionPlan? Outgoing { get; init; }

    /// <summary>Tiempo de la secuencia anidada, que es el de los keyframes del compuesto (no lleva velocidad).</summary>
    public Time EffectTimeAt(Time timeline) => sourceIn + (timeline - clipStart);

    internal static GroupClipPlan? At(IReadOnlyList<GroupClipPlan> groups, Time time)
    {
        int lo = 0;
        int hi = groups.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (groups[mid].End <= time)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo < groups.Count && groups[lo].Start <= time ? groups[lo] : null;
    }
}

/// <summary>Un carril de video: una pista con sus clips activos ordenados por posición, o el carril compañero de sus transiciones.</summary>
internal sealed class VideoLayerPlan(int trackIndex, string trackId, IReadOnlyList<VideoClipPlan> clips)
{
    /// <summary>Sufijo del carril compañero: tiene los clips que entran en cada transición, adelantados media duración.</summary>
    public const string CompanionSuffix = "#t";

    /// <summary>Índice en <see cref="Sequence.VideoTracks"/>; 0 es la capa de más abajo.</summary>
    public int TrackIndex { get; } = trackIndex;

    public string TrackId { get; } = trackId;

    public IReadOnlyList<VideoClipPlan> Clips { get; } = clips;

    /// <summary>Carril con los clips que entran en las transiciones de esta pista; nulo si no tiene transiciones.</summary>
    public VideoLayerPlan? Companion { get; init; }

    /// <summary>Carril generado: los títulos de la pista ordenados por posición, sin decodificador ni transiciones.</summary>
    public IReadOnlyList<GeneratedClipPlan> Generated { get; init; } = [];

    /// <summary>Los clips compuestos que se componen aparte (con efectos o transiciones propios), ordenados por posición.</summary>
    public IReadOnlyList<GroupClipPlan> Groups { get; init; } = [];

    /// <summary>Los compuestos que entran en una transición de esta pista, adelantados media duración.</summary>
    public IReadOnlyList<GroupClipPlan> IncomingGroups { get; init; } = [];

    /// <summary>El carril muestra lo de dentro de un compuesto: no es una capa de la secuencia, se compone en su grupo.</summary>
    public bool IsNested { get; init; }

    /// <summary>Posición en <see cref="TimelinePlan.Layers"/>.</summary>
    public int Ordinal { get; internal set; }

    public GroupClipPlan? GroupAt(Time time) => GroupClipPlan.At(Groups, time);

    public GroupClipPlan? IncomingGroupAt(Time time) => GroupClipPlan.At(IncomingGroups, time);

    /// <summary>El título que se ve en <paramref name="time"/>, si hay.</summary>
    public GeneratedClipPlan? GeneratedAt(Time time)
    {
        int i = FirstGeneratedEndingAfter(time);
        return i < Generated.Count && Generated[i].Start <= time ? Generated[i] : null;
    }

    /// <summary>Índice del primer título que termina después de <paramref name="time"/>; para recorrer los que siguen.</summary>
    public int FirstGeneratedEndingAfter(Time time)
    {
        IReadOnlyList<GeneratedClipPlan> generated = Generated;
        int lo = 0;
        int hi = generated.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (generated[mid].End <= time)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    public VideoClipPlan? ClipAt(Time time)
    {
        int i = FirstEndingAfter(time);
        return i < Clips.Count && Clips[i].Start <= time ? Clips[i] : null;
    }

    /// <summary>El primer clip que empieza después de <paramref name="time"/>.</summary>
    public VideoClipPlan? NextAfter(Time time)
    {
        int i = FirstEndingAfter(time);
        while (i < Clips.Count && Clips[i].Start <= time)
        {
            i++;
        }

        return i < Clips.Count ? Clips[i] : null;
    }

    private int FirstEndingAfter(Time time)
    {
        int lo = 0;
        int hi = Clips.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Clips[mid].End <= time)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
