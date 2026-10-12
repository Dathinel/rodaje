using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>
/// Ediciones que aplican el resultado de un análisis (silencios, ritmo, ducking) como operaciones normales del núcleo:
/// el análisis da datos y aquí se convierten en cortes, borrados, marcadores y keyframes.
/// </summary>
public static class AnalysisEdits
{
    /// <summary>
    /// Quita de un clip (y de sus enlazados) los tramos dados en tiempo de la secuencia: corta en sus bordes, ajustados al
    /// cuadro hacia dentro, y borra cada pieza con ripple (según <paramref name="scope"/>) o dejando el hueco.
    /// </summary>
    /// <returns>Tramos quitados de verdad, en tiempo de la secuencia de antes.</returns>
    public static IReadOnlyList<(Time Start, Time End)> RemoveSpans(
        EditContext ctx,
        string clipId,
        IEnumerable<(Time Start, Time End)> spans,
        bool ripple,
        RippleScope scope)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(spans);
        Clip clip = FindClip(ctx, clipId);
        Rational rate = ctx.Sequence.FrameRate;
        Time clipStart = clip.Start, clipEnd = clip.End;

        var inside = new List<(Time Start, Time End)>();
        foreach ((Time start, Time end) in spans.OrderBy(s => s.Start))
        {
            Time s = start <= clipStart ? clipStart : FrameCeiling(start, rate);
            Time e = end >= clipEnd ? clipEnd : Time.FromFrames(end.ToFrames(rate), rate);

            if (e <= s)
            {
                continue;
            }

            if (inside.Count > 0 && s <= inside[^1].End)
            {
                inside[^1] = (inside[^1].Start, Time.Max(inside[^1].End, e));
            }
            else
            {
                inside.Add((s, e));
            }
        }

        if (inside.Count == 1 && inside[0].Start == clipStart && inside[0].End == clipEnd)
        {
            throw new EditException("El clip entero es silencio: no queda nada que conservar.");
        }

        for (int i = inside.Count - 1; i >= 0; i--)
        {
            (Time s, Time e) = inside[i];
            Clip current = FindClip(ctx, clipId);
            if (e < current.End)
            {
                SplitWithFreshLink(ctx, clipId, e);
            }

            string middle = clipId;
            if (s > current.Start)
            {
                middle = SplitWithFreshLink(ctx, clipId, s)[0];
            }

            if (ripple)
            {
                TimelineEdits.RippleDelete(ctx, [middle], scope);
            }
            else
            {
                TimelineEdits.Lift(ctx, [middle]);
            }
        }

        return inside;
    }

    /// <summary>Agrega un marcador en cada instante (ajustado al cuadro) que no tenga ya uno.</summary>
    /// <returns>Identificadores de los marcadores nuevos.</returns>
    public static IReadOnlyList<string> AddMarkers(EditContext ctx, IEnumerable<Time> times, string name)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(name);
        Rational rate = ctx.Sequence.FrameRate;
        var existing = new HashSet<Time>(ctx.Sequence.Markers.Select(m => m.Time));
        var ids = new List<string>();
        foreach (Time t in times.Where(t => t >= Time.Zero).Select(t => t.SnapToFrame(rate)).Distinct().Order())
        {
            if (existing.Add(t))
            {
                var marker = new Marker { Id = ctx.Ids.NewId("marker"), Time = t, Name = name };
                ctx.Sequence.Markers.Add(marker);
                ids.Add(marker.Id);
            }
        }

        EditContext.SortMarkers(ctx.Sequence);
        ctx.EnsureValid();
        return ids;
    }

    /// <summary>Corta el clip (con sus enlazados) en cada instante, ajustado al cuadro, que caiga dentro.</summary>
    /// <returns>Identificadores de las piezas nuevas, de izquierda a derecha.</returns>
    public static IReadOnlyList<string> SplitAt(EditContext ctx, string clipId, IEnumerable<Time> times)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(times);
        Clip clip = FindClip(ctx, clipId);
        Rational rate = ctx.Sequence.FrameRate;
        Time start = clip.Start, end = clip.End;
        var created = new List<string>();
        foreach (Time t in times.Select(t => t.SnapToFrame(rate)).Where(t => t > start && t < end).Distinct().OrderDescending())
        {
            created.InsertRange(0, SplitWithFreshLink(ctx, clipId, t));
        }

        return created;
    }

    /// <summary>
    /// Suma una envolvente de ganancia (dB, en tiempo de la secuencia, lineal entre puntos y 0 fuera) al volumen del clip
    /// como keyframes de <c>gainDb</c> del efecto de audio. Lo que ya estaba animado se conserva y se le suma lo mismo.
    /// </summary>
    public static void ApplyGainOffsets(EditContext ctx, string clipId, IReadOnlyList<(Time At, double OffsetDb)> envelope)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(envelope);
        Clip clip = FindClip(ctx, clipId);
        if (clip.Generator is not null)
        {
            throw new EditException("Un título no tiene sonido: el ducking va en el clip de música.");
        }

        Track track = ctx.Sequence.AllTracks.First(t => t.Clips.Contains(clip));
        EditContext.EnsureUnlocked(track);
        List<(Time At, double OffsetDb)> points = [.. envelope.OrderBy(p => p.At)];
        if (points.Count == 0)
        {
            return;
        }

        foreach ((_, double offset) in points)
        {
            if (!double.IsFinite(offset))
            {
                throw new EditException("La envolvente de ducking tiene un valor que no es un número.");
            }
        }

        EffectInstance? effect = clip.Effects.Find(e => e.Type == BuiltInEffects.Audio);
        if (effect is null)
        {
            effect = new EffectInstance { Id = ctx.Ids.NewId("effect"), Type = BuiltInEffects.Audio };
            clip.Effects.Add(effect);
        }

        string name = BuiltInEffects.AudioParameters.GainDb;
        if (!effect.Parameters.TryGetValue(name, out Parameter? gain))
        {
            gain = new Parameter { Value = 0 };
            effect.Parameters[name] = gain;
        }

        Parameter before = gain.Clone();
        double min = EffectRegistry.SilenceDb, max = 24;
        var result = new SortedDictionary<Time, Keyframe>();

        // Lo que ya estaba animado sigue animado, con el desplazamiento sumado en su instante.
        foreach (Keyframe k in before.Keyframes)
        {
            Time at = clip.TimeMap.TimelineOf(k.Time);
            result[k.Time] = k with { Value = Math.Clamp(k.Value + OffsetAt(points, at), min, max) };
        }

        var times = new List<Time>();
        foreach ((Time at, _) in points)
        {
            times.Add(Clamp(at, clip.Start, clip.End));
        }

        // Si la envolvente ya está baja en un borde del clip, el borde lleva su propio keyframe.
        times.Add(clip.Start);
        times.Add(clip.End);
        foreach (Time at in times.Distinct())
        {
            double offset = OffsetAt(points, at);
            bool edge = at == clip.Start || at == clip.End;
            if (edge && offset == 0 && !points.Exists(p => p.At == at))
            {
                continue;
            }

            Time effectTime = clip.ToEffectTime(at == clip.End ? clip.End - Time.FromTicks(1) : at);
            if (!result.ContainsKey(effectTime))
            {
                result[effectTime] = new Keyframe(effectTime, Math.Clamp(before.Evaluate(effectTime) + offset, min, max));
            }
        }

        gain.Keyframes = [.. result.Values];
        ctx.EnsureValid();
    }

    /// <summary>Valor de la envolvente en <paramref name="at"/>: lineal entre puntos, 0 antes del primero y tras el último.</summary>
    public static double OffsetAt(IReadOnlyList<(Time At, double OffsetDb)> points, Time at)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0 || at < points[0].At || at > points[^1].At)
        {
            return 0;
        }

        if (at == points[0].At)
        {
            return points[0].OffsetDb;
        }

        for (int i = 1; i < points.Count; i++)
        {
            if (at <= points[i].At)
            {
                (Time a, double va) = points[i - 1];
                (Time b, double vb) = points[i];
                long span = (b - a).Ticks;
                return span == 0 ? vb : va + ((vb - va) * (at - a).Ticks / span);
            }
        }

        return 0;
    }

    /// <summary>
    /// Corta y da a las piezas derechas un enlace propio: el contexto reutiliza el enlace derivado dentro de una misma
    /// operación, y cortar dos veces el mismo grupo dejaría dos piezas con el mismo enlace en una pista.
    /// </summary>
    private static IReadOnlyList<string> SplitWithFreshLink(EditContext ctx, string clipId, Time at)
    {
        IReadOnlyList<string> created = TimelineEdits.Split(ctx, clipId, at);
        string? fresh = null;
        foreach (string id in created)
        {
            Clip piece = FindClip(ctx, id);
            if (piece.LinkId is not null)
            {
                fresh ??= ctx.Ids.NewId("link");
                piece.LinkId = fresh;
            }
        }

        return created;
    }

    private static Clip FindClip(EditContext ctx, string clipId)
    {
        ArgumentNullException.ThrowIfNull(clipId);
        foreach (Track track in ctx.Sequence.AllTracks)
        {
            Clip? clip = track.Clips.Find(c => c.Id == clipId);
            if (clip is not null)
            {
                return clip;
            }
        }

        throw new EditException($"El clip «{clipId}» ya no está en la secuencia.");
    }

    private static Time FrameCeiling(Time t, Rational rate)
    {
        Time floor = Time.FromFrames(t.ToFrames(rate), rate);
        return floor >= t ? floor : Time.FromFrames(t.ToFrames(rate) + 1, rate);
    }

    private static Time Clamp(Time t, Time lo, Time hi) => t < lo ? lo : t > hi ? hi : t;
}
