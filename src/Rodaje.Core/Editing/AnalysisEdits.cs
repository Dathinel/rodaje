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
    /// «Montaje al ritmo»: recorta cada clip, de izquierda a derecha y pista por pista, para que termine en el pulso
    /// que toca (<paramref name="step"/> pulsos después del primero que cae tras su inicio) y corre lo que sigue en su
    /// pista (ripple), así los cortes van cayendo en la música. Un clip no crece más allá de su material (los que tienen
    /// reversa, curva o congelado solo se acortan) y uno sin pulso después de su inicio se deja como está. Va con los
    /// enlazados.
    /// </summary>
    /// <param name="ctx">Contexto de edición.</param>
    /// <param name="clipIds">Los clips del montaje (la música no va entre ellos).</param>
    /// <param name="beats">Pulsos en tiempo de la secuencia, en cualquier orden.</param>
    /// <param name="step">Cada cuántos pulsos cae un corte (1, 2, 4, 8…).</param>
    /// <param name="scope">Qué pistas se corren al cambiar cada duración.</param>
    /// <returns>Los clips que cambiaron de duración, con su final nuevo.</returns>
    public static IReadOnlyList<(string ClipId, Time End)> FitToBeats(EditContext ctx, IEnumerable<string> clipIds, IReadOnlyList<Time> beats, int step,
        RippleScope scope = RippleScope.Track)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(clipIds);
        ArgumentNullException.ThrowIfNull(beats);
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);
        Rational rate = ctx.Sequence.FrameRate;
        Time[] grid = [.. beats.Select(b => b.SnapToFrame(rate)).Distinct().Order()];
        var changed = new List<(string, Time)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Track track in ctx.Sequence.AllTracks)
        {
            // Los ids que caen en esta pista, en orden de inicio (los enlazados de otra pista van con su clip).
            List<string> ordered = [.. track.Clips.Where(c => clipIds.Contains(c.Id) && seen.Add(c.Id)).OrderBy(c => c.Start).Select(c => c.Id)];
            foreach (string clipId in ordered)
            {
                if (ctx.Sequence.FindClip(clipId) is not { } found)
                {
                    continue;
                }

                Clip clip = found.Clip;
                int first = Array.FindIndex(grid, b => b > clip.Start);
                if (first < 0 || first + step - 1 >= grid.Length)
                {
                    continue;
                }

                Time end = grid[first + step - 1];
                if (MaxEnd(ctx, clip) is { } limit && end > limit)
                {
                    // Al cuadro entero por debajo del tope: nunca más allá del material.
                    end = Time.FromFrames((long)Math.Floor(limit.ToSeconds() * rate.ToDouble()), rate);
                }

                if (end <= clip.Start || end == clip.End)
                {
                    continue;
                }

                TimelineEdits.RippleTrim(ctx, clipId, TrimEdge.Out, end - clip.End, scope);
                changed.Add((clipId, end));
            }
        }

        ctx.EnsureValid();
        return changed;
    }

    /// <summary>Hasta dónde puede llegar el final del clip con el material que tiene; <c>null</c> si no tiene tope (título, foto).</summary>
    private static Time? MaxEnd(EditContext ctx, Clip clip)
    {
        if (clip.Generator is not null)
        {
            return null;
        }

        if (clip.Speed is { IsIdentity: false } speed && (speed.Reverse || speed.Freeze || speed.Curve is not null))
        {
            return clip.End;
        }

        Time? source = clip.IsCompound
            ? CompoundOps.SourceLimit(ctx.Project, clip)
            : ctx.Project.FindMedia(clip.MediaId) is { } media ? media.IsStillImage ? null : media.Duration : null;
        if (source is not { } available || available <= clip.SourceIn)
        {
            return source is null ? null : clip.End;
        }

        Time remaining = available - clip.SourceIn;
        if (clip.Speed is { } s && s.Rate != new Rational(1, 1))
        {
            // A 2× el material dura la mitad en la secuencia.
            remaining = Time.FromTicks((long)Math.Round(remaining.Ticks * s.Rate.Den / (double)s.Rate.Num));
        }

        return clip.Start + remaining;
    }

    /// <summary>
    /// «Sincronizar por el sonido»: mueve cada clip para que su sonido coincida con el del clip de referencia, que no se
    /// mueve. <paramref name="offsets"/> dice, para cada clip, dónde cae el 0 de su medio en el reloj del medio de
    /// referencia (lo que da la correlación de las formas de onda): el instante de referencia <c>t</c> suena en el otro
    /// medio en <c>t − offset</c>. El inicio nuevo es el instante de la secuencia en que la referencia reproduce
    /// <c>SourceIn + offset</c> del clip, ajustado al cuadro; va con sus enlazados y falla si el sitio está ocupado o la
    /// referencia no va a 1× (con velocidad, reversa o congelado no hay un reloj común).
    /// </summary>
    /// <returns>Cuánto se corrió cada clip (0 si ya estaba en su sitio).</returns>
    public static IReadOnlyList<(string ClipId, Time Shift)> AlignByAudio(EditContext ctx, string referenceClipId,
        IReadOnlyList<(string ClipId, Time Offset)> offsets)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(offsets);
        Clip reference = FindClip(ctx, referenceClipId);
        if (reference.Speed is { IsIdentity: false })
        {
            throw new EditException("El clip de referencia tiene velocidad, reversa o un fotograma congelado: ponlo a 1× para sincronizar por el sonido.");
        }

        Rational rate = ctx.Sequence.FrameRate;
        var shifts = new List<(string, Time)>(offsets.Count);
        foreach ((string clipId, Time offset) in offsets)
        {
            if (clipId == referenceClipId)
            {
                continue;
            }

            Clip clip = FindClip(ctx, clipId);
            Time start = AlignedStart(reference, clip, offset).SnapToFrame(rate);
            if (start < Time.Zero)
            {
                throw new EditException($"El clip «{clip.Id}» quedaría antes del inicio de la línea de tiempo: empieza antes que la referencia en el sonido.");
            }

            Time shift = start - clip.Start;
            if (shift != Time.Zero)
            {
                TimelineEdits.Move(ctx, clipId, start, null, PlacementMode.Strict);
            }

            shifts.Add((clipId, shift));
        }

        ctx.EnsureValid();
        return shifts;
    }

    /// <summary>Dónde tiene que empezar <paramref name="clip"/> para sonar a la vez que <paramref name="reference"/> (sin ajustar al cuadro).</summary>
    public static Time AlignedStart(Clip reference, Clip clip, Time offset)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(clip);
        // El reloj del medio de referencia pasa por la secuencia con pendiente 1: t_ref = Start + (m − SourceIn).
        return reference.Start + (clip.SourceIn + offset - reference.SourceIn);
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
