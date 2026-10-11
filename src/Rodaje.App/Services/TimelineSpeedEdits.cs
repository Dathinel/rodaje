using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>
/// Los comandos de velocidad que usan la línea de tiempo y los atajos: velocidad constante, estirar un borde, congelar
/// cuadro e invertir. Cada uno cambia el clip y sus enlazados (que comparten velocidad) en un paso del historial.
/// </summary>
/// <remarks>
/// Doble provisional de <c>Edits.SetSpeed</c>, <c>Edits.RateStretch</c> y <c>Edits.FreezeFrame</c> del núcleo (fase 5c,
/// agente A): mismas firmas de uso, solo velocidad constante, sin reflejar keyframes al invertir. Al unir el núcleo, cada
/// método pasa a llamar al suyo de <c>Edits</c> y esta clase queda como fachada o se borra.
/// </remarks>
public static class TimelineSpeedEdits
{
    /// <summary>Denominador con que se guarda una velocidad que sale de estirar: diezmilésimas, dentro del tope de términos.</summary>
    private const long RateDenominator = 10_000;

    /// <summary>
    /// Pone la velocidad constante <paramref name="rate"/> al clip y a sus enlazados; la duración cambia y el tramo del
    /// origen se conserva. Con <paramref name="ripple"/> se corre lo que sigue; sin él, solo si el clip alargado choca.
    /// </summary>
    public static SequenceEditCommand SetSpeed(string sequenceId, string clipId, Rational rate, RippleScope? ripple, IIdGenerator? ids = null) =>
        new("Cambiar velocidad", sequenceId, ctx => Retime(ctx, clipId, TrimEdge.Out, ripple, collisionsRipple: true, (clip, speed, start, _) =>
        {
            speed.Rate = rate;
            speed.Offset = Time.Zero;
            return ClipTimeMap.Fit(start, clip.SourceIn, clip.SourceOut, speed);
        }), ids);

    /// <summary>
    /// Estira o encoge el clip hasta <paramref name="duration"/> cambiando la velocidad, sin cambiar el tramo del origen
    /// (Alt + arrastrar un borde). Con el borde de entrada y sin ripple el final queda quieto. Se funde con el siguiente
    /// del mismo clip y borde: un arrastre es un paso.
    /// </summary>
    public static SequenceEditCommand RateStretch(string sequenceId, string clipId, TrimEdge edge, Time duration, RippleScope? ripple, IIdGenerator? ids = null) =>
        new("Cambiar velocidad", sequenceId, ctx => Retime(ctx, clipId, edge, ripple, collisionsRipple: false, (clip, speed, start, media) =>
        {
            speed.Offset = Time.Zero;
            Time anchor = speed.Reverse ? clip.SourceOut : clip.SourceIn;
            long terms = RateTerms(RateFor(clip.SourceDuration, duration));
            while (true)
            {
                speed.Rate = Reduce(terms);
                ClipTimeMap map = ClipTimeMap.Create(start, duration, anchor, speed);
                if ((map.SourceIn >= Time.Zero && map.SourceOut <= media) || terms <= MinTerms)
                {
                    return map;
                }

                // Redondear al más cercano puede pedir unos ticks más de los que tiene el medio: se baja un paso.
                terms--;
            }
        }), ids, $"ratestretch:{clipId}:{(edge == TrimEdge.In ? "in" : "out")}");

    /// <summary>
    /// Congela el cuadro de <paramref name="at"/> durante <paramref name="duration"/>: corta el clip ahí, mete el cuadro
    /// congelado (sin sonido) y corre lo que sigue en su pista y en las de sus enlazados.
    /// </summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el clip congelado.</remarks>
    public static SequenceEditCommand FreezeFrame(string sequenceId, string clipId, Time at, Time duration, RippleScope scope, IIdGenerator? ids = null) =>
        new("Congelar cuadro", sequenceId, ctx =>
        {
            (Track track, Clip clip) = ctx.Sequence.GetClip(clipId);
            EditContext.EnsureUnlocked(track);
            if (track.Kind != TrackKind.Video)
            {
                throw new EditException("Solo se congela un cuadro de video.");
            }

            if (clip.Speed is { Freeze: true })
            {
                throw new EditException("El clip ya es un cuadro congelado.");
            }

            if (at < clip.Start || at >= clip.End || duration <= Time.Zero)
            {
                throw new EditException("El cursor tiene que estar sobre el clip para congelar su cuadro.");
            }

            Time source = clip.TimeMap.Source(at);
            List<Track> tracks = [.. ctx.Sequence.GetLinkGroup(clipId).Select(m => m.Track).Distinct()];
            if (at > clip.Start)
            {
                TimelineEdits.Split(ctx, clipId, at);
            }

            ctx.RippleShift(tracks, at, duration, scope);
            var frozen = new Clip
            {
                Id = ctx.Ids.NewId("clip"),
                MediaId = clip.MediaId,
                SourceIn = source,
                SourceOut = source + Time.FromTicks(1),
                Start = at,
                Enabled = clip.Enabled,
                Speed = new ClipSpeed { Freeze = true },
                Duration = duration,
                Effects = clip.Effects.ConvertAll(effect =>
                {
                    EffectInstance copy = effect.Clone();
                    copy.Id = ctx.Ids.NewId("fx");
                    return copy;
                }),
            };

            track.Clips.Add(frozen);
            track.SortClips();
            ctx.EnsureValid();
            return (IReadOnlyList<string>)[frozen.Id];
        }, ids);

    /// <summary>Invierte el clip y sus enlazados (o los vuelve a poner hacia adelante) sin cambiar su lugar ni su duración.</summary>
    public static SequenceEditCommand SetReverse(string sequenceId, string clipId, bool reverse, IIdGenerator? ids = null) =>
        new(reverse ? "Invertir" : "Quitar invertir", sequenceId, ctx => Retime(ctx, clipId, TrimEdge.Out, null, collisionsRipple: false, (clip, speed, start, _) =>
        {
            if (speed.Freeze)
            {
                throw new EditException("Un cuadro congelado no se invierte.");
            }

            speed.Reverse = reverse;
            speed.Offset = Time.Zero;
            return ClipTimeMap.Create(start, clip.Duration, reverse ? clip.SourceOut : clip.SourceIn, speed);
        }), ids);

    private const long MinTerms = RateDenominator / 10;
    private const long MaxTerms = RateDenominator * 100;

    /// <summary>
    /// La velocidad constante que lleva <paramref name="source"/> de origen a <paramref name="duration"/> de línea de
    /// tiempo, redondeada a la diezmilésima más cercana (así un arrastre que vuelve a 2× queda en 2× exacto) y dentro de
    /// 0,1× a 100×.
    /// </summary>
    public static Rational RateFor(Time source, Time duration)
    {
        if (source <= Time.Zero || duration <= Time.Zero)
        {
            throw new EditException("La duración tiene que ser positiva.");
        }

        Int128 num = (((Int128)source.Ticks * RateDenominator * 2) + duration.Ticks) / (2 * (Int128)duration.Ticks);
        return Reduce((long)Int128.Clamp(num, MinTerms, MaxTerms));
    }

    private static long RateTerms(Rational rate) => rate.Num * RateDenominator / rate.Den;

    private static Rational Reduce(long terms)
    {
        long gcd = (long)System.Numerics.BigInteger.GreatestCommonDivisor(terms, RateDenominator);
        return new Rational(terms / gcd, RateDenominator / gcd);
    }

    /// <summary>
    /// Aplica a cada clip del grupo enlazado el mapeo que da <paramref name="build"/> (sobre una copia de su velocidad) y
    /// corre lo que sigue: con <paramref name="ripple"/> siempre; sin él, solo si <paramref name="collisionsRipple"/> y el
    /// clip alargado se monta sobre el siguiente.
    /// </summary>
    private static void Retime(EditContext ctx, string clipId, TrimEdge edge, RippleScope? ripple, bool collisionsRipple, Func<Clip, ClipSpeed, Time, Time, ClipTimeMap> build)
    {
        List<(Track Track, Clip Clip)> group = ctx.Sequence.GetLinkGroup(clipId);
        Clip primary = group[0].Clip;
        Time oldEnd = primary.End;
        bool keepEnd = edge == TrimEdge.In && ripple is null;
        foreach ((Track track, Clip clip) in group)
        {
            EditContext.EnsureUnlocked(track);
            ClipSpeed speed = clip.Speed?.Clone() ?? new ClipSpeed();
            if (speed.Curve is not null)
            {
                throw new EditException("Las curvas de velocidad todavía no se estiran desde la línea de tiempo.");
            }

            if (speed.Freeze)
            {
                throw new EditException("Un cuadro congelado no tiene velocidad: cambia su duración recortándolo.");
            }

            ClipTimeMap map = build(clip, speed, clip.Start, ctx.MediaOf(clip).Duration);
            if (keepEnd)
            {
                map = map.MoveTo(clip.End - map.Duration);
            }

            if (speed.IsIdentity)
            {
                clip.Speed = null;
                clip.Start = map.Start;
                clip.SourceIn = map.SourceIn;
                clip.SourceOut = map.SourceOut;
            }
            else
            {
                clip.Speed = speed;
                map.ApplyTo(clip);
            }
        }

        Time delta = keepEnd ? Time.Zero : primary.End - oldEnd;
        var moved = new HashSet<Clip>(group.Select(m => m.Clip));
        List<Track> tracks = [.. group.Select(m => m.Track).Distinct()];
        if (delta != Time.Zero && ripple is { } scope)
        {
            ctx.RippleShift(tracks, oldEnd, delta, scope, moved);
        }
        else if (delta > Time.Zero && collisionsRipple && group.Any(m => Overlaps(m.Track, m.Clip)))
        {
            ctx.RippleShift(tracks, oldEnd, delta, RippleScope.Track, moved);
        }

        ctx.EnsureValid();
    }

    private static bool Overlaps(Track track, Clip clip) =>
        track.Clips.Exists(other => !ReferenceEquals(other, clip) && other.Start < clip.End && clip.Start < other.End);
}
