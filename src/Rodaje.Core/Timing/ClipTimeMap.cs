using Rodaje.Core.Model;

namespace Rodaje.Core.Timing;

/// <summary>
/// La única puerta entre la línea de tiempo y el origen de un clip. El origen de cada instante se calcula desde el ancla
/// (<see cref="SourceIn"/> hacia adelante, <see cref="SourceOut"/> en reversa) y <see cref="Offset"/>, nunca sumando
/// pasos, así que no deriva y dividir o recortar no cambia ningún cuadro. Inmutable; a 1× es la cuenta de siempre.
/// </summary>
/// <remarks>
/// Con <c>p(τ) = ⌊I(Offset + τ)⌋ − ⌊I(Offset)⌋</c> el clip muestra <c>SourceIn + p(τ)</c> hacia adelante y
/// <c>SourceOut − 1 − p(τ)</c> en reversa, para <c>τ</c> en <c>[0, Duration)</c>; el tramo del origen mide
/// <c>⌈I(Offset + Duration)⌉ − ⌊I(Offset)⌋</c>. Un congelado muestra siempre <see cref="SourceIn"/>.
/// </remarks>
public sealed class ClipTimeMap : IEquatable<ClipTimeMap>
{
    private readonly RetimeMath _speed;
    private readonly long _base;

    private ClipTimeMap(Time start, Time duration, Time offset, Time anchor, RetimeMath speed, bool reverse, bool freeze, bool hasSpeed)
    {
        if (duration <= Time.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Un clip necesita una duración positiva.");
        }

        if (hasSpeed && Int128.Abs(offset.Ticks) + duration.Ticks > RetimeMath.MaxExtent)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "El clip con velocidad pasa del largo máximo del mapeo.");
        }

        _speed = speed;
        Start = start;
        Duration = duration;
        Offset = offset;
        Reverse = reverse;
        Freeze = freeze;
        HasSpeed = hasSpeed;
        _base = speed.IsIdentity ? 0 : speed.Floor(offset.Ticks);
        Time span = freeze ? Time.FromTicks(1) : speed.IsIdentity ? duration : Time.FromTicks(speed.Ceiling(offset.Ticks + duration.Ticks) - _base);
        SourceIn = reverse ? anchor - span : anchor;
        SourceOut = reverse ? anchor : anchor + span;
    }

    public Time Start { get; }

    public Time Duration { get; }

    public Time End => Start + Duration;

    public Time Offset { get; }

    public Time SourceIn { get; }

    public Time SourceOut { get; }

    public bool Reverse { get; }

    public bool Freeze { get; }

    /// <summary>Viene de un clip con <see cref="Clip.Speed"/>; sin ella no hay cotas y <see cref="Offset"/> es 0.</summary>
    public bool HasSpeed { get; }

    public RetimeMath Speed => _speed;

    /// <summary>Muestra el origen tal cual: sin velocidad o con una que equivale a 1×.</summary>
    public bool IsIdentity => _speed.IsIdentity && !Reverse && !Freeze;

    /// <summary>El mapeo del clip. Lanza si su velocidad no es válida (<see cref="ModelValidator"/> lo informa antes).</summary>
    public static ClipTimeMap Of(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Speed is not { } speed)
        {
            return new ClipTimeMap(clip.Start, clip.SourceOut - clip.SourceIn, Time.Zero, clip.SourceIn, RetimeMath.Identity, false, false, false);
        }

        bool reverse = speed.Reverse && !speed.Freeze;
        return new ClipTimeMap(clip.Start, clip.Duration, speed.Offset, reverse ? clip.SourceOut : clip.SourceIn,
            RetimeMath.Of(speed), reverse, speed.Freeze, true);
    }

    /// <summary>Mapeo con el ancla dada: <c>SourceIn</c> hacia adelante y en un congelado, <c>SourceOut</c> en reversa.</summary>
    public static ClipTimeMap Create(Time start, Time duration, Time anchor, ClipSpeed? speed)
    {
        if (speed is null)
        {
            return new ClipTimeMap(start, duration, Time.Zero, anchor, RetimeMath.Identity, false, false, false);
        }

        return new ClipTimeMap(start, duration, speed.Offset, anchor, RetimeMath.Of(speed), speed.Reverse && !speed.Freeze, speed.Freeze, true);
    }

    /// <summary>
    /// El mapeo más largo con esa velocidad que no sale de <c>[sourceIn, sourceOut)</c>: el que se usa al cambiar la
    /// velocidad de un clip ya recortado. Hacia adelante se ancla en <paramref name="sourceIn"/> y en reversa en
    /// <paramref name="sourceOut"/>; a 1× la duración es la del tramo.
    /// </summary>
    /// <exception cref="ArgumentException">El tramo no alcanza ni un tick, o la velocidad es un congelado.</exception>
    public static ClipTimeMap Fit(Time start, Time sourceIn, Time sourceOut, ClipSpeed? speed)
    {
        if (speed is { Freeze: true })
        {
            throw new ArgumentException("Un congelado no tiene tramo de origen que ajustar: su duración se elige.", nameof(speed));
        }

        long span = (sourceOut - sourceIn).Ticks;
        if (span <= 0)
        {
            throw new ArgumentException("El tramo del origen está vacío.", nameof(sourceOut));
        }

        Time anchor = speed is { Reverse: true } ? sourceOut : sourceIn;
        RetimeMath math = RetimeMath.Of(speed);
        if (speed is null)
        {
            return Create(start, Time.FromTicks(span), anchor, speed);
        }

        long offset = speed.Offset.Ticks;
        long limit = (long)Int128.Max(0, RetimeMath.MaxExtent - Int128.Abs(offset));
        if (math.IsIdentity)
        {
            return Create(start, Time.FromTicks(Math.Min(span, limit)), anchor, speed);
        }

        long m = math.Floor(offset) + Math.Min(span, RetimeMath.MaxTarget / 2);
        long firstAbove = math.Inverse(m);
        if (math.Ceiling(firstAbove) == m)
        {
            firstAbove++;
        }

        long duration = Math.Min(firstAbove - 1 - offset, limit);
        if (duration <= 0)
        {
            throw new ArgumentException("El tramo del origen es más corto que un tick a esa velocidad.", nameof(sourceOut));
        }

        return Create(start, Time.FromTicks(duration), anchor, speed);
    }

    /// <summary>Instante del origen que se ve en <paramref name="timeline"/>, acotado al clip.</summary>
    public Time Source(Time timeline) => SourceAt(Clamp(timeline));

    /// <summary>
    /// Como <see cref="Source"/> dentro del clip; fuera, sigue a la velocidad del borde (el material que usa una transición).
    /// Sin velocidad es <c>SourceIn + (t − Start)</c>, sin acotar.
    /// </summary>
    public Time ExtendedSource(Time timeline)
    {
        if (!HasSpeed)
        {
            return SourceIn + (timeline - Start);
        }

        Int128 tau = (Int128)timeline.Ticks - Start.Ticks;
        if (tau >= 0 && tau < Duration.Ticks)
        {
            return SourceAt((long)tau);
        }

        if (Freeze)
        {
            return SourceIn;
        }

        long progress;
        if (_speed.IsIdentity)
        {
            progress = (long)Int128.Clamp(tau, -RetimeMath.MaxArgument, RetimeMath.MaxArgument);
        }
        else if (tau < 0)
        {
            progress = _speed.FloorExtended(Offset.Ticks, (long)Int128.Max(tau, -RetimeMath.MaxArgument)) - _base;
        }
        else
        {
            long beyond = (long)Int128.Min(tau - Duration.Ticks, RetimeMath.MaxArgument);
            progress = _speed.FloorExtended(Offset.Ticks + Duration.Ticks, beyond) - _base;
        }

        return Reverse ? SourceOut - Time.FromTicks(1) - Time.FromTicks(progress) : SourceIn + Time.FromTicks(progress);
    }

    /// <summary>
    /// Primer instante del clip cuyo origen alcanza <paramref name="source"/> (≥ hacia adelante, ≤ en reversa);
    /// <see cref="End"/> si no lo alcanza. Sirve para ubicar keyframes, cuadros y miniaturas.
    /// </summary>
    public Time TimelineOf(Time source)
    {
        if (Freeze)
        {
            return source <= SourceIn ? Start : End;
        }

        Int128 need = Reverse ? (Int128)SourceOut.Ticks - 1 - source.Ticks : (Int128)source.Ticks - SourceIn.Ticks;
        if (need <= 0)
        {
            return Start;
        }

        if (need > Progress(Duration.Ticks - 1))
        {
            return End;
        }

        long tau = _speed.IsIdentity ? (long)need : _speed.Inverse(_base + (long)need) - Offset.Ticks;
        return Start + Time.FromTicks(Math.Max(0, tau));
    }

    /// <summary>Instante en que se evalúan los keyframes del clip: el del origen, o uno virtual que avanza en un congelado.</summary>
    public Time EffectTime(Time timeline)
    {
        long tau = Clamp(timeline);
        return Freeze ? SourceIn + Offset + Time.FromTicks(tau) : SourceAt(tau);
    }

    /// <summary>Velocidad en <paramref name="timeline"/> (acotado al clip): negativa en reversa y 0 en un congelado.</summary>
    public double RateAt(Time timeline)
    {
        if (Freeze)
        {
            return 0;
        }

        double rate = _speed.IsIdentity ? 1 : _speed.RateAt(Offset.Ticks + Clamp(timeline));
        return Reverse ? -rate : rate;
    }

    /// <summary>
    /// Menor y mayor velocidad, en valor absoluto, en <c>[from, to)</c>; fuera del clip cuenta la del borde, como en
    /// <see cref="ExtendedSource"/>. Un congelado da 0.
    /// </summary>
    public (double Min, double Max) RateRange(Time from, Time to)
    {
        if (Freeze)
        {
            return (0, 0);
        }

        if (_speed.IsIdentity)
        {
            return (1, 1);
        }

        long first = Clamp(from);
        long last = Clamp(to > from ? to - Time.FromTicks(1) : from);
        return _speed.RateRange(Offset.Ticks + first, Offset.Ticks + last);
    }

    /// <summary>
    /// Si <paramref name="other"/> muestra el mismo origen que este mapeo en cada instante que los dos cubren, siguiendo su
    /// velocidad: lo que pasa con las dos mitades de un clip dividido o con el mismo clip tras recortarlo.
    /// </summary>
    public bool SameCourse(ClipTimeMap other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Reverse != other.Reverse || Freeze != other.Freeze || !_speed.Equals(other._speed))
        {
            return false;
        }

        if (Freeze)
        {
            return SourceIn == other.SourceIn;
        }

        if (_speed.IsIdentity)
        {
            return Reverse ? SourceOut + Start == other.SourceOut + other.Start : SourceIn - Start == other.SourceIn - other.Start;
        }

        // Hacia adelante se ve SourceIn − ⌊I(Offset)⌋ + ⌊I(t − Start + Offset)⌋: basta con la constante y el corrimiento.
        return Offset - Start == other.Offset - other.Start
            && (Reverse ? SourceOut + Time.FromTicks(_base) == other.SourceOut + Time.FromTicks(other._base)
                : SourceIn - Time.FromTicks(_base) == other.SourceIn - Time.FromTicks(other._base));
    }

    /// <summary>
    /// El mapeo de los dos clips como uno solo cuando <paramref name="next"/> empieza donde termina este y sigue el mismo
    /// recorrido (como las dos mitades de un clip dividido); nulo si no.
    /// </summary>
    public ClipTimeMap? Join(ClipTimeMap next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.Start != End || HasSpeed != next.HasSpeed || !SameCourse(next) || (HasSpeed && next.Offset != Offset + Duration)
            || (HasSpeed && Int128.Abs(Offset.Ticks) + Duration.Ticks + next.Duration.Ticks > RetimeMath.MaxExtent))
        {
            return null;
        }

        var joined = new ClipTimeMap(Start, Duration + next.Duration, Offset, Reverse ? SourceOut : SourceIn, _speed, Reverse, Freeze, HasSpeed);
        (ClipTimeMap left, ClipTimeMap right) = joined.SplitAt(next.Start);
        return left.Equals(this) && right.Equals(next) ? joined : null;
    }

    /// <summary>Los dos clips que quedan al dividir en <paramref name="at"/>; cada instante sigue mostrando el mismo origen.</summary>
    public (ClipTimeMap Left, ClipTimeMap Right) SplitAt(Time at)
    {
        if (at <= Start || at >= End)
        {
            throw new ArgumentOutOfRangeException(nameof(at), at, "Solo se puede dividir dentro del clip.");
        }

        Time tau = at - Start;
        return (With(Start, tau, Time.Zero), With(at, Duration - tau, tau));
    }

    /// <summary>El clip con su inicio en <paramref name="start"/> y el mismo final; antes del inicio sigue el mismo mapeo.</summary>
    public ClipTimeMap TrimIn(Time start)
    {
        if (start >= End)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "El inicio tiene que quedar antes del final.");
        }

        Time shift = start - Start;
        return With(start, Duration - shift, shift);
    }

    /// <summary>El clip con su final en <paramref name="end"/> y el mismo inicio.</summary>
    public ClipTimeMap TrimOut(Time end)
    {
        if (end <= Start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, "El final tiene que quedar después del inicio.");
        }

        return With(Start, end - Start, Time.Zero);
    }

    /// <summary>Desliza el material bajo el clip: todo el origen se corre <paramref name="delta"/>.</summary>
    public ClipTimeMap Slip(Time delta) =>
        new(Start, Duration, Offset, (Reverse ? SourceOut : SourceIn) + delta, _speed, Reverse, Freeze, HasSpeed);

    /// <summary>El mismo mapeo empezando en otro lugar de la pista.</summary>
    public ClipTimeMap MoveTo(Time start) =>
        new(start, Duration, Offset, Reverse ? SourceOut : SourceIn, _speed, Reverse, Freeze, HasSpeed);

    /// <summary>Escribe los tiempos en el clip: inicio, tramo del origen y, si tiene velocidad, duración y desplazamiento.</summary>
    /// <exception cref="ArgumentException">El clip tiene velocidad y el mapeo no, o al revés.</exception>
    public void ApplyTo(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if ((clip.Speed is not null) != HasSpeed)
        {
            throw new ArgumentException("El mapeo y el clip no coinciden en si tienen velocidad.", nameof(clip));
        }

        clip.Start = Start;
        clip.SourceIn = SourceIn;
        clip.SourceOut = SourceOut;
        if (clip.Speed is { } speed)
        {
            speed.Offset = Offset;
            clip.Duration = Duration;
        }
    }

    public bool Equals(ClipTimeMap? other) =>
        other is not null && Start == other.Start && Duration == other.Duration && Offset == other.Offset
        && SourceIn == other.SourceIn && SourceOut == other.SourceOut && Reverse == other.Reverse && Freeze == other.Freeze
        && HasSpeed == other.HasSpeed && _speed.Equals(other._speed);

    public override bool Equals(object? obj) => Equals(obj as ClipTimeMap);

    public override int GetHashCode() => HashCode.Combine(Start, Duration, Offset, SourceIn, SourceOut, Reverse, Freeze, _speed);

    public override string ToString() =>
        $"[{Start}, {End}) → [{SourceIn}, {SourceOut}){(Reverse ? " ◀" : "")}{(Freeze ? " congelado" : "")}";

    private ClipTimeMap With(Time start, Time duration, Time shift)
    {
        Time anchor = Freeze ? SourceIn
            : Reverse ? SourceOut - Time.FromTicks(Progress(shift.Ticks))
            : SourceIn + Time.FromTicks(Progress(shift.Ticks));
        return new ClipTimeMap(start, duration, HasSpeed ? Offset + shift : Time.Zero, anchor, _speed, Reverse, Freeze, HasSpeed);
    }

    private long Clamp(Time timeline)
    {
        Int128 tau = (Int128)timeline.Ticks - Start.Ticks;
        return (long)Int128.Clamp(tau, 0, Duration.Ticks - 1);
    }

    private Time SourceAt(long tau)
    {
        if (Freeze)
        {
            return SourceIn;
        }

        long progress = Progress(tau);
        return Reverse ? SourceOut - Time.FromTicks(1 + progress) : SourceIn + Time.FromTicks(progress);
    }

    private long Progress(long tau) => _speed.IsIdentity ? tau : _speed.Floor(Offset.Ticks + tau) - _base;
}
