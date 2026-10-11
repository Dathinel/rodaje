using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <remarks>
/// Velocidad (fase 5c). El grupo enlazado cambia junto y cada miembro conserva su material, así que su duración cambia.
/// Decisión de Daniel, en cualquier pista: si el clip se acorta, el siguiente se une por magnetismo y la transición del
/// corte se queda; si crece, respeta los huecos y solo empuja lo que pisa, lo justo. Con
/// <see cref="RippleScope.Track"/> cada pista del grupo se corre con su propio Δ, y las de los enlazados de lo que se
/// corre con el de la pista que los arrastra; con <see cref="RippleScope.AllTracks"/> o
/// <see cref="RippleScope.AllTracksAndMarkers"/> se corren todas las pistas (y los marcadores) con un mismo Δ.
/// </remarks>
public static partial class TimelineEdits
{
    /// <summary>Duración por defecto de un congelado.</summary>
    public static Time DefaultFreezeDuration { get; } = Time.FromSeconds(3);

    /// <summary>Denominador más grande de la velocidad que sale de estirar un borde.</summary>
    public const long StretchMaxDenominator = 10_000;

    /// <summary>
    /// Velocidad constante para el clip y sus enlazados. Cada uno conserva su tramo del origen, y su final se alinea al
    /// cuadro de la secuencia hacia abajo (sin pasar de su material). Conserva reversa, tono y muestreo; quita una curva.
    /// </summary>
    public static void SetSpeed(EditContext ctx, string clipId, Rational rate, RippleScope scope = RippleScope.Track)
    {
        if (!RetimeMath.IsValidRate(rate))
        {
            throw new EditException($"La velocidad {rate} está fuera de 0,1× a 100×.");
        }

        Retime(ctx, clipId, scope, (_, speed) =>
        {
            speed.Curve = null;
            speed.Rate = rate;
            return speed;
        });
    }

    /// <summary>
    /// Velocidad que cambia a lo largo del clip; <c>null</c> vuelve a 1×. Se conservan
    /// la forma y el material: la duración es la que recorre el tramo del origen con esa forma, y la curva la cubre.
    /// </summary>
    public static void SetCurve(EditContext ctx, string clipId, SpeedCurve? curve, RippleScope scope = RippleScope.Track)
    {
        if (curve is null)
        {
            SetSpeed(ctx, clipId, new Rational(1, 1), scope);
            return;
        }

        Clip primary = TrimmingSupport.ResolveGroup(ctx, clipId)[0].Clip;
        SpeedCurve fitted = CoveringCurve(curve, primary.SourceDuration);
        Retime(ctx, clipId, scope, (_, speed) =>
        {
            speed.Rate = new Rational(1, 1);
            speed.Curve = fitted.Clone();
            return speed;
        });
    }

    /// <summary>
    /// Invierte o endereza el clip y sus enlazados sin cambiar duración ni material.
    /// Los keyframes de sus efectos se reflejan en el tramo del origen, así la animación sigue igual en la
    /// línea de tiempo; un segundo cambio los deja como estaban.
    /// </summary>
    public static void SetReverse(EditContext ctx, string clipId, bool reverse)
    {
        List<(Track Track, Clip Clip)> group = EditableSpeedGroup(ctx, clipId);
        if (group.Exists(m => m.Clip.Speed is { Freeze: true }))
        {
            throw new EditException("Un cuadro congelado no se puede invertir.");
        }

        foreach ((_, Clip clip) in group)
        {
            bool current = clip.Speed is { Reverse: true };
            if (current == reverse)
            {
                continue;
            }

            ClipTimeMap map = clip.TimeMap;
            ClipSpeed speed = clip.Speed?.Clone() ?? new ClipSpeed();
            speed.Reverse = reverse;
            ClipSpeed? next = Normalize(speed);
            ClipTimeMap reversed = ClipTimeMap.Create(map.Start, map.Duration, reverse ? map.SourceOut : map.SourceIn, next);
            clip.Speed = next;
            reversed.ApplyTo(clip);
            ReflectKeyframes(clip);
        }

        ctx.EnsureValid();
    }

    /// <summary>Si el audio del clip y sus enlazados mantiene el tono al cambiar de velocidad.</summary>
    public static void SetPreservePitch(EditContext ctx, string clipId, bool preservePitch)
    {
        foreach ((_, Clip clip) in EditableSpeedGroup(ctx, clipId))
        {
            if (clip.Speed is { } speed)
            {
                speed.PreservePitch = preservePitch;
            }
        }

        ctx.EnsureValid();
    }

    /// <summary>
    /// Alt + arrastrar un borde: el clip pasa a durar <c>D ∓ delta</c> con el mismo material, así que cambia su velocidad
    /// a <c>material / duración</c>, aproximada con un denominador de hasta <see cref="StretchMaxDenominator"/> y acotada
    /// a 0,1×–100×. El inicio queda, tire del borde que tire; lo que sigue se une o se empuja como en <see cref="SetSpeed"/>.
    /// En un congelado es un recorte. En una curva se estira la forma: su <see cref="SpeedCurve.Span"/> y el
    /// desplazamiento se escalan por <c>D′/D</c> y cada punto se divide por lo mismo (velocidad media nueva), redondeado
    /// a centésimas y acotado para que ningún punto salga de 0,1×–10×.
    /// </summary>
    public static void RateStretch(EditContext ctx, string clipId, TrimEdge edge, Time delta, RippleScope scope = RippleScope.Track)
    {
        List<(Track Track, Clip Clip)> group = EditableSpeedGroup(ctx, clipId);
        Clip primary = group[0].Clip;
        if (primary.Speed is { Freeze: true })
        {
            StretchFrozen(ctx, group, edge, delta, scope);
            return;
        }

        Time duration = edge == TrimEdge.In ? primary.Duration - delta : primary.Duration + delta;
        if (duration <= Time.Zero)
        {
            throw new EditException($"El clip «{primary.Id}» se quedaría sin duración.");
        }

        if (primary.Speed?.Curve is { } curve)
        {
            Time length = StretchableLength(curve, primary.Duration, duration);
            long from = primary.Duration.Ticks;
            Retime(ctx, clipId, scope, (_, speed) =>
            {
                if (speed.Curve is { } own)
                {
                    speed.Curve = ScaleCurve(own, from, length.Ticks);
                    speed.Offset = Time.FromTicks(ScaleTicks(speed.Offset.Ticks, length.Ticks, from));
                }

                return speed;
            }, (clip, fit) => ExactLength(ctx, primary, clip, fit, length), keepOffset: true);
            return;
        }

        Rational rate = ApproximateRate(primary.SourceDuration.Ticks, duration.Ticks);
        Retime(ctx, clipId, scope, (clip, speed) =>
        {
            speed.Rate = rate;
            return speed;
        }, (clip, fit) => ExactLength(ctx, primary, clip, fit, duration));
    }

    /// <summary>El clip arrastrado llega justo a <paramref name="duration"/> si su archivo tiene material; los enlazados quedan con su largo.</summary>
    private static ClipTimeMap ExactLength(EditContext ctx, Clip primary, Clip clip, ClipTimeMap fit, Time duration)
    {
        bool exact = ReferenceEquals(clip, primary);
        Time start = clip.Start;
        ClipTimeMap moved = fit.MoveTo(start);
        if (!exact || moved.Duration == duration)
        {
            return moved;
        }

        ClipTimeMap stretched = moved.TrimOut(start + duration);
        return stretched.SourceIn >= Time.Zero && stretched.SourceOut <= ctx.MediaOf(clip).Duration ? stretched : moved;
    }

    /// <summary>
    /// <paramref name="target"/> acotada para que, al dividir cada punto de la curva por <c>target / current</c>, ninguno
    /// salga de 0,1×–10× ni la curva pase del largo máximo del mapeo.
    /// </summary>
    private static Time StretchableLength(SpeedCurve curve, Time current, Time target)
    {
        int low = curve.Points.Min(p => p.Hundredths);
        int high = curve.Points.Max(p => p.Hundredths);
        Int128 d = current.Ticks;
        Int128 longest = d * low / RetimeMath.MinHundredths;
        Int128 shortest = RetimeMath.CeilDiv(d * high, RetimeMath.MaxHundredths);
        longest = Int128.Min(longest, (Int128)RetimeMath.MaxExtent * current.Ticks / Math.Max(1, curve.Span.Ticks));
        if (longest < shortest)
        {
            throw new EditException("La curva de velocidad ya no se puede estirar más.");
        }

        return Time.FromTicks((long)Int128.Clamp(target.Ticks, shortest, longest));
    }

    /// <summary>La misma forma con la duración por <c>to / from</c> y cada velocidad por <c>from / to</c>, a la centésima más cercana.</summary>
    private static SpeedCurve ScaleCurve(SpeedCurve curve, long from, long to) => new()
    {
        Span = Time.FromTicks(Math.Clamp(ScaleTicks(curve.Span.Ticks, to, from), 1, RetimeMath.MaxExtent)),
        Points = curve.Points.ConvertAll(p => p with
        {
            Hundredths = (int)Int128.Clamp(RoundDiv((Int128)p.Hundredths * from, to), RetimeMath.MinHundredths, RetimeMath.MaxHundredths),
        }),
    };

    /// <summary><c>ticks · num / den</c> redondeado al más cercano.</summary>
    private static long ScaleTicks(long ticks, long num, long den) => (long)RoundDiv((Int128)ticks * num, den);

    private static Int128 RoundDiv(Int128 a, Int128 b) => RetimeMath.FloorDiv((2 * a) + b, 2 * b);

    /// <summary>
    /// Congela el cuadro que se ve en <paramref name="at"/>: corta el clip de video (y sus enlazados) ahí, pone un clip
    /// congelado de <paramref name="duration"/> (3 s por omisión) con copias de sus efectos y corre lo que sigue en las
    /// pistas del grupo y las de sus enlazados, así el audio queda con un hueco.
    /// </summary>
    /// <returns>El identificador del clip congelado.</returns>
    public static string FreezeFrame(EditContext ctx, string clipId, Time at, Time? duration = null, RippleScope scope = RippleScope.Track)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Time length = duration ?? DefaultFreezeDuration;
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        (Track track, Clip primary) = group[0];
        if (track.Kind != TrackKind.Video)
        {
            throw new EditException("Solo se puede congelar un cuadro de un clip de video.");
        }

        if (at < primary.Start || at >= primary.End)
        {
            throw new EditException($"El instante {at} no cae dentro del clip «{primary.Id}».");
        }

        if (length <= Time.Zero || length.Ticks > RetimeMath.MaxExtent)
        {
            throw new EditException("La duración del congelado está fuera de rango.");
        }

        foreach ((Track t, _) in group)
        {
            EditContext.EnsureUnlocked(t);
        }

        Time source = primary.TimeMap.Source(at);
        foreach ((Track t, Clip member) in group.FindAll(m => TrimmingSupport.StrictlyContains(m.Clip, at)))
        {
            TrackOps.AddClipSorted(t, TrackOps.SplitOff(ctx, member, at));
        }

        ctx.RippleShift(group.Select(m => m.Track), at, length, scope);

        var frozen = new Clip
        {
            Id = ctx.Ids.NewId("clip"),
            MediaId = primary.MediaId,
            Enabled = primary.Enabled,
            Speed = new ClipSpeed { Freeze = true, FrameSampling = primary.Speed?.FrameSampling ?? FrameSampling.Nearest },
            Effects = primary.Effects.ConvertAll(effect =>
            {
                EffectInstance copy = effect.Clone();
                copy.Id = ctx.Ids.NewId("fx");
                return copy;
            }),
        };
        ClipTimeMap.Create(at, length, source, frozen.Speed).ApplyTo(frozen);
        TrackOps.AddClipSorted(track, frozen);
        ctx.EnsureValid();
        return frozen.Id;
    }

    /// <summary>
    /// La mejor fracción de denominador hasta <see cref="StretchMaxDenominator"/> para <paramref name="material"/> /
    /// <paramref name="duration"/> (fracciones continuas con semiconvergentes), acotada a 0,1×–100×.
    /// </summary>
    public static Rational ApproximateRate(long material, long duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(material);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(duration);
        if ((Int128)material * 10 <= duration)
        {
            return RetimeMath.MinRate;
        }

        if (material >= (Int128)duration * 100)
        {
            return RetimeMath.MaxRate;
        }

        Int128 p0 = 0, q0 = 1, p1 = 1, q1 = 0;
        Int128 n = material, d = duration;
        while (d != 0)
        {
            Int128 a = n / d;
            Int128 q2 = q0 + (a * q1);
            if (q2 > StretchMaxDenominator)
            {
                Int128 k = (StretchMaxDenominator - q0) / q1;
                Int128 ps = p0 + (k * p1), qs = q0 + (k * q1);
                bool semi = Int128.Abs((ps * duration) - (material * qs)) * q1 < Int128.Abs((p1 * duration) - (material * q1)) * qs;
                return semi ? new Rational((long)ps, (long)qs) : new Rational((long)p1, (long)q1);
            }

            (p0, q0, p1, q1) = (p1, q1, p0 + (a * p1), q2);
            (n, d) = (d, n - (a * d));
        }

        return new Rational((long)p1, (long)q1);
    }

    /// <summary>
    /// Cambia la velocidad del grupo con <paramref name="change"/> (sobre una copia, con desplazamiento 0 salvo
    /// <paramref name="keepOffset"/>) y ajusta cada miembro a su
    /// material; <paramref name="place"/> puede rehacer el mapeo ajustado. Comprueba, aplica y corre lo que haga falta.
    /// </summary>
    private static void Retime(
        EditContext ctx,
        string clipId,
        RippleScope scope,
        Func<Clip, ClipSpeed, ClipSpeed> change,
        Func<Clip, ClipTimeMap, ClipTimeMap>? place = null,
        bool keepOffset = false)
    {
        List<(Track Track, Clip Clip)> group = EditableSpeedGroup(ctx, clipId);
        if (group.Exists(m => m.Clip.Speed is { Freeze: true }))
        {
            throw new EditException("Un cuadro congelado no tiene velocidad; cambia su duración recortándolo.");
        }

        var plans = new List<(TrimPlan Plan, ClipSpeed? Speed)>(group.Count);
        foreach ((Track track, Clip clip) in group)
        {
            ClipSpeed changed = change(clip, clip.Speed?.Clone() ?? new ClipSpeed());
            changed.Offset = keepOffset ? changed.Offset : Time.Zero;
            ClipSpeed? speed = Normalize(changed);
            ClipTimeMap map;
            try
            {
                map = ClipTimeMap.Fit(clip.Start, clip.SourceIn, clip.SourceOut, speed);
            }
            catch (ArgumentException)
            {
                throw new EditException($"El clip «{clip.Id}» es demasiado corto para esa velocidad.");
            }

            map = place is null ? AlignEnd(ctx, clip, map, speed) : place(clip, map);
            plans.Add((new TrimPlan(track, clip, map), speed));
        }

        TrimmingSupport.CheckBounds(ctx, plans.ConvertAll(p => p.Plan));
        var seeds = new List<(Track Track, Time From, Time Delta)>();
        var edited = new HashSet<Clip>(group.Select(m => m.Clip));
        foreach ((TrimPlan plan, ClipSpeed? speed) in plans)
        {
            Time oldEnd = plan.Clip.End;
            seeds.Add((plan.Track, oldEnd, plan.End - oldEnd));
            plan.Clip.Speed = speed;
            plan.Map!.ApplyTo(plan.Clip);
        }

        Magnetize(ctx, seeds, scope, edited);
    }

    /// <summary>Estirar un congelado es cambiar su duración con el inicio quieto, con la misma regla de huecos.</summary>
    private static void StretchFrozen(EditContext ctx, List<(Track Track, Clip Clip)> group, TrimEdge edge, Time delta, RippleScope scope)
    {
        List<TrimPlan> plans = group.ConvertAll(m =>
        {
            TrimPlan plan = TrimPlan.Of(m.Track, m.Clip);
            return edge == TrimEdge.In ? plan.TrimIn(m.Clip.Start + delta).MoveTo(m.Clip.Start) : plan.TrimOut(m.Clip.End + delta);
        });
        TrimmingSupport.CheckBounds(ctx, plans);
        var seeds = new List<(Track Track, Time From, Time Delta)>();
        foreach (TrimPlan plan in plans)
        {
            seeds.Add((plan.Track, plan.Clip.End, plan.End - plan.Clip.End));
            plan.Map!.ApplyTo(plan.Clip);
        }

        Magnetize(ctx, seeds, scope, new HashSet<Clip>(group.Select(m => m.Clip)));
    }

    /// <summary>
    /// Corre lo que sigue a los clips que cambiaron de duración. Lo que se acorta corre siempre (el siguiente se une); lo
    /// que crece empuja solo lo que pisa, así los huecos se respetan. Cada pista con su Δ, o todas con el de la primera
    /// que se corre si el ámbito es de todas las pistas. Comprueba que nada se pise y valida.
    /// </summary>
    private static void Magnetize(EditContext ctx, List<(Track Track, Time From, Time Delta)> seeds, RippleScope scope, HashSet<Clip> edited)
    {
        var moving = new List<(Track Track, Time From, Time Delta)>();
        foreach ((Track track, Time from, Time delta) in seeds)
        {
            if (delta < Time.Zero)
            {
                moving.Add((track, from, delta));
                continue;
            }

            // Al crecer se empuja solo lo que pisa: el hueco se consume primero, y arrastrar a pasos da lo mismo que de una vez.
            Clip? next = track.Clips.Find(c => !edited.Contains(c) && c.Start >= from);
            if (next is not null && next.Start < from + delta)
            {
                moving.Add((track, from, from + delta - next.Start));
            }
        }

        IReadOnlyList<Track> shifted = moving.Count == 0 ? []
            : scope.IncludesAllTracks() ? ctx.RippleShift(moving.Select(s => s.Track), moving[0].From, moving[0].Delta, scope, edited)
            : ctx.RippleEach(moving, edited);
        TrimmingSupport.EnsureNoOverlaps(scope.IncludesAllTracks() ? ctx.Sequence.AllTracks : seeds.Select(s => s.Track).Concat(shifted).Distinct());
        ctx.EnsureValid();
    }

    /// <summary>
    /// Con velocidad, el final va al cuadro de la secuencia más cercano (al anterior si el archivo no tiene material
    /// para el siguiente). Al más cercano y no siempre hacia abajo para que repetir el cambio, como al arrastrar el
    /// deslizador, no vaya comiendo material.
    /// </summary>
    private static ClipTimeMap AlignEnd(EditContext ctx, Clip clip, ClipTimeMap map, ClipSpeed? speed)
    {
        if (speed is null)
        {
            return map;
        }

        Rational fps = ctx.Sequence.FrameRate;
        Time floor = Time.FromFrames(map.End.ToFrames(fps), fps);
        Time nearest = map.End.SnapToFrame(fps);
        foreach (Time end in (Time[])[nearest, floor])
        {
            if (end == map.End)
            {
                return map;
            }

            if (end > map.Start && end.Ticks - map.Start.Ticks <= RetimeMath.MaxExtent && map.TrimOut(end) is { } aligned
                && aligned.SourceIn >= Time.Zero && aligned.SourceOut <= ctx.MediaOf(clip).Duration)
            {
                return aligned;
            }
        }

        return map;
    }

    /// <summary>Una velocidad que equivale a 1× sin desplazamiento vuelve a ser nula, como en el formato 3.</summary>
    private static ClipSpeed? Normalize(ClipSpeed speed) => speed.IsIdentity && speed.Offset == Time.Zero ? null : speed;

    private static List<(Track Track, Clip Clip)> EditableSpeedGroup(EditContext ctx, string clipId)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        foreach ((Track track, _) in group)
        {
            EditContext.EnsureUnlocked(track);
        }

        return group;
    }

    /// <summary>La curva con <see cref="SpeedCurve.Span"/> igual a la duración que recorre <paramref name="material"/> ticks del origen.</summary>
    private static SpeedCurve CoveringCurve(SpeedCurve curve, Time material)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var probe = new SpeedCurve { Span = Time.FromTicks(Math.Min(material.Ticks, RetimeMath.MaxExtent)), Points = curve.Points };
        if (RetimeMath.CurveProblem(probe) is { } problem)
        {
            throw new EditException(problem);
        }

        // La forma fija la velocidad media m; recorrer L ticks lleva L / m = L² / I(L).
        long walked = RetimeMath.FromCurve(probe).Floor(probe.Span.Ticks);
        Int128 span = (Int128)probe.Span.Ticks * probe.Span.Ticks / Math.Max(1, walked);
        return new SpeedCurve { Span = Time.FromTicks((long)Int128.Clamp(span, 1, RetimeMath.MaxExtent)), Points = [.. curve.Points] };
    }

    /// <summary>
    /// Refleja los keyframes de los efectos del clip en su tramo del origen (<c>t → SourceIn + SourceOut − t</c>), en orden
    /// inverso; la suavización de cada tramo pasa al keyframe que ahora lo empieza, con entrada y salida cambiadas.
    /// </summary>
    private static void ReflectKeyframes(Clip clip)
    {
        Time sum = clip.SourceIn + clip.SourceOut;
        foreach (Parameter parameter in clip.Effects.SelectMany(e => e.Parameters.Values))
        {
            List<Keyframe> old = parameter.Keyframes;
            int n = old.Count;
            if (n == 0)
            {
                continue;
            }

            parameter.Keyframes = [.. Enumerable.Range(0, n).Select(k =>
            {
                Keyframe source = old[n - 1 - k];
                Interpolation ease = old[k < n - 1 ? n - 2 - k : n - 1].Interpolation;
                return new Keyframe(sum - source.Time, source.Value, Mirror(ease));
            })];
        }
    }

    private static Interpolation Mirror(Interpolation interpolation) => interpolation switch
    {
        Interpolation.EaseIn => Interpolation.EaseOut,
        Interpolation.EaseOut => Interpolation.EaseIn,
        _ => interpolation,
    };
}
