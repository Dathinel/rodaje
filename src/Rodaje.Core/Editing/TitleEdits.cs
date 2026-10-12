using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Text;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Ediciones de los clips generadores (títulos) y de las pistas de subtítulos (formato 5).</summary>
public static class TitleEdits
{
    /// <summary>Duración de un título nuevo.</summary>
    public static readonly Time DefaultDuration = Time.FromSeconds(5);

    /// <summary>
    /// Pone un clip generador en <paramref name="at"/> sobre la pista de video libre más alta (sin contar la principal,
    /// las bloqueadas ni las de subtítulos); si ninguna tiene sitio, crea una pista encima de todas con
    /// <paramref name="newTrackName"/>.
    /// </summary>
    /// <returns>El id del clip y, si se creó, el de la pista nueva.</returns>
    public static IReadOnlyList<string> InsertTitle(EditContext ctx, Time at, Time duration, ClipGenerator generator, string newTrackName)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(generator);
        EnsurePlacement(at, duration);
        List<Track> video = ctx.Sequence.VideoTracks;
        Track? target = null;
        for (int i = video.Count - 1; i >= 1; i--)
        {
            Track track = video[i];
            if (!track.Locked && track.Role is null && TrackOps.FindOverlap(track, at, at + duration) is null)
            {
                target = track;
                break;
            }
        }

        string? createdTrack = null;
        if (target is null)
        {
            createdTrack = TimelineEdits.AddTrack(ctx, TrackKind.Video, video.Count, newTrackName);
            target = ctx.Sequence.GetTrack(createdTrack);
        }

        string clipId = Place(ctx, target, at, duration, generator);
        return createdTrack is null ? [clipId] : [clipId, createdTrack];
    }

    /// <summary>Pone un clip generador en una pista de video concreta, en un tramo libre.</summary>
    public static string InsertGenerator(EditContext ctx, string trackId, Time at, Time duration, ClipGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(generator);
        EnsurePlacement(at, duration);
        Track track = TrimmingSupport.ResolveTrack(ctx, trackId);
        if (track.Kind != TrackKind.Video)
        {
            throw new EditException("Un título solo puede ir en una pista de video.");
        }

        return Place(ctx, track, at, duration, generator);
    }

    public static void SetText(EditContext ctx, string clipId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ClipGenerator generator = Generator(ctx, clipId);
        generator.Spans = TextSpans.Remap(generator.Text, text, generator.Spans);
        generator.Text = text;
        ctx.EnsureValid();
    }

    /// <summary>
    /// Cambia el formato de un trozo del texto (negrita, color, tamaño de una palabra) con <paramref name="change"/>, que
    /// recibe el formato que tenía cada parte; los tramos quedan ordenados y unidos.
    /// </summary>
    public static void FormatText(EditContext ctx, string clipId, int start, int length, Func<TextSpan, TextSpan> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ClipGenerator generator = Generator(ctx, clipId);
        if (length <= 0 || start < 0 || start + length > generator.Text.Length)
        {
            throw new EditException("Elige un trozo del texto para darle formato.");
        }

        generator.Spans = TextSpans.Apply(generator.Spans, generator.Text.Length, start, length, change);
        ctx.EnsureValid();
    }

    /// <summary>Sustituye la copia del estilo del clip (D-029): el paquete del que salió no cambia.</summary>
    public static void SetStyle(EditContext ctx, string clipId, TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        Generator(ctx, clipId).Style = style.Clone();
        ctx.EnsureValid();
    }

    public static void SetEdges(EditContext ctx, string clipId, bool? animateIn, bool? animateOut)
    {
        GeneratorEdges edges = Generator(ctx, clipId).Edges;
        edges.In = animateIn ?? edges.In;
        edges.Out = animateOut ?? edges.Out;
        ctx.EnsureValid();
    }

    public static void SetBox(EditContext ctx, string clipId, TextFrame? box)
    {
        Generator(ctx, clipId).Box = box?.Clone();
        ctx.EnsureValid();
    }

    /// <summary>
    /// Pasa la entrada y la salida activas del estilo a keyframes de <c>rodaje.transform</c> y las quita del estilo: desde
    /// ahí quedan fijas en el clip y se editan a mano. Compone igual que el motor (<see cref="TitleMotion.Compose"/>): el
    /// desplazamiento gira y se escala con el clip, la escala y la opacidad se multiplican y el giro se suma, con las
    /// posiciones escaladas de la referencia del estilo a la secuencia y el desfase de un trozo dividido.
    /// </summary>
    public static void ConvertAnimationToKeyframes(EditContext ctx, string clipId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        (Track track, Clip clip) = ctx.Sequence.FindClip(clipId)
            ?? throw new EditException($"El clip «{clipId}» ya no está en la secuencia.");
        EditContext.EnsureUnlocked(track);
        if (clip.Generator?.Style is not { } style)
        {
            throw new EditException("Solo un título con estilo tiene animaciones que convertir.");
        }

        GeneratorEdges edges = clip.Generator.Edges;
        // Por línea, palabra o letra no hay una transformación del bloque que fijar: esas quedan en el estilo.
        StyleAnimation? entry = edges.In && style.In is { PerUnit: false } && TitleMotion.Ticks(style.In) > 0 ? style.In : null;
        StyleAnimation? exit = edges.Out && style.Out is { PerUnit: false } && TitleMotion.Ticks(style.Out) > 0 ? style.Out : null;
        if (entry is null && exit is null)
        {
            throw new EditException("El título no tiene animación de entrada ni de salida del bloque activa (las de línea, palabra o letra no se pasan a keyframes).");
        }

        // Si no caben las dos en el título entero, se encogen por igual, como al evaluarlas.
        (long entryTicks, long exitTicks, double stretch) = TitleMotion.Fit(
            TitleMotion.Ticks(entry), TitleMotion.Ticks(exit), edges.Before.Ticks + clip.Duration.Ticks + edges.After.Ticks);
        double units = Math.Min(
            (double)ctx.Sequence.Width / style.Reference.Width,
            (double)ctx.Sequence.Height / style.Reference.Height);

        EffectInstance transform = clip.Effects.Find(e => e.Type == BuiltInEffects.Transform)
            ?? AddTransform(ctx, clip);
        var original = transform.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        Time first = clip.ToEffectTime(clip.Start);
        Time last = first + clip.Duration;
        if (entry is not null)
        {
            Bake(transform, original, entry, first - edges.Before, Time.FromTicks(entryTicks), stretch, units, first, last);
        }

        if (exit is not null)
        {
            Time end = last + edges.After;
            Bake(transform, original, exit, end - Time.FromTicks(exitTicks), Time.FromTicks(exitTicks), stretch, units, first, last);
        }

        TextStyle changed = style.Clone();
        changed.In = entry is null ? changed.In : null;
        changed.Out = exit is null ? changed.Out : null;
        clip.Generator.Style = changed;
        ctx.EnsureValid();
    }

    /// <summary>Agrega una pista de video con el rol de subtítulos encima de todas.</summary>
    /// <returns>El id de la pista nueva.</returns>
    public static string AddSubtitleTrack(EditContext ctx, string name)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        string id = TimelineEdits.AddTrack(ctx, TrackKind.Video, ctx.Sequence.VideoTracks.Count, name);
        ctx.Sequence.GetTrack(id).Role = TrackRoles.Subtitles;
        ctx.EnsureValid();
        return id;
    }

    /// <summary>Cambia el rol de una pista de video; <c>null</c> la vuelve corriente.</summary>
    public static void SetTrackRole(EditContext ctx, string trackId, string? role)
    {
        Track track = TrimmingSupport.ResolveTrack(ctx, trackId);
        EditContext.EnsureUnlocked(track);
        track.Role = role;
        ctx.EnsureValid();
    }

    private static void EnsurePlacement(Time at, Time duration)
    {
        if (at < Time.Zero)
        {
            throw new EditException("El título no puede empezar antes del inicio de la línea de tiempo.");
        }

        if (duration <= Time.Zero)
        {
            throw new EditException("El título necesita una duración.");
        }
    }

    private static string Place(EditContext ctx, Track track, Time at, Time duration, ClipGenerator generator)
    {
        EditContext.EnsureUnlocked(track);
        TrackOps.EnsureFree(track, at, at + duration);
        var clip = new Clip
        {
            Id = ctx.Ids.NewId("clip"),
            Generator = generator.Clone(),
            SourceIn = Time.Zero,
            SourceOut = duration,
            Start = at,
        };
        TrackOps.AddClipSorted(track, clip);
        ctx.EnsureValid();
        return clip.Id;
    }

    private static ClipGenerator Generator(EditContext ctx, string clipId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        (Track track, Clip clip) = ctx.Sequence.FindClip(clipId)
            ?? throw new EditException($"El clip «{clipId}» ya no está en la secuencia.");
        EditContext.EnsureUnlocked(track);
        return clip.Generator ?? throw new EditException($"El clip «{clipId}» no es un título.");
    }

    private static EffectInstance AddTransform(EditContext ctx, Clip clip)
    {
        var effect = new EffectInstance { Id = ctx.Ids.NewId("effect"), Type = BuiltInEffects.Transform };
        clip.Effects.Insert(0, effect);
        return effect;
    }

    private static readonly string[] Motion =
    [
        BuiltInEffects.TransformParameters.X,
        BuiltInEffects.TransformParameters.Y,
        BuiltInEffects.TransformParameters.Scale,
        BuiltInEffects.TransformParameters.Rotation,
        BuiltInEffects.TransformParameters.Opacity,
    ];

    /// <summary>
    /// Escribe una animación como keyframes en los instantes de sus puntos (y de los keyframes que el clip ya tenía en ese
    /// tramo, y en los bordes del clip si la animación se sale): en cada uno, la transformación compuesta del motor.
    /// </summary>
    private static void Bake(
        EffectInstance transform,
        Dictionary<string, Parameter> original,
        StyleAnimation animation,
        Time origin,
        Time span,
        double stretch,
        double units,
        Time first,
        Time last)
    {
        ParameterDefinition?[] specs = [.. Motion.Select(n => EffectRegistry.Find(BuiltInEffects.Transform)?.FindParameter(n))];
        var curves = new Parameter?[Motion.Length];
        Time duration = Time.FromSeconds(animation.DurationSeconds);
        for (int i = 0; i < Motion.Length; i++)
        {
            if (animation.Curves.TryGetValue(Motion[i], out List<StyleKey>? keys) && keys.Count > 0)
            {
                ParameterDefinition? spec = specs[i];
                curves[i] = new Parameter
                {
                    Keyframes = [.. keys
                        .Where(k => double.IsFinite(k.T) && double.IsFinite(k.Value))
                        .OrderBy(k => k.T)
                        .Select(k => new Keyframe(Time.Max(Time.Zero, Time.Min(duration, Time.FromSeconds(k.T))), spec?.Clamp(k.Value) ?? k.Value, k.Interp))],
                };
            }
        }

        Time end = origin + span;
        var samples = new SortedSet<Time>();
        foreach (Parameter? curve in curves)
        {
            foreach (Keyframe key in curve?.Keyframes ?? [])
            {
                samples.Add(origin + Time.FromTicks((long)Math.Round(key.Time.Ticks * stretch)));
            }
        }

        foreach (Parameter parameter in original.Values)
        {
            foreach (Keyframe key in parameter.Keyframes)
            {
                if (key.Time > origin && key.Time < end)
                {
                    samples.Add(key.Time);
                }
            }
        }

        foreach (Time edge in (Time[])[first, last])
        {
            if (edge > origin && edge < end)
            {
                samples.Add(edge);
            }
        }

        samples.RemoveWhere(t => t < first || t > last || t < origin || t > end);
        var rows = new List<(Time At, double[] Values, Interpolation[] Interps)>();
        bool[] changed = new bool[Motion.Length];
        foreach (Time at in samples)
        {
            // Las curvas están en el tiempo del estilo; si se encogieron, se recorren más rápido.
            long position = (at - origin).Ticks;
            var local = Time.FromTicks(stretch < 1 ? (long)Math.Round(position / stretch) : position);
            double[] baseline = new double[Motion.Length];
            double[] delta = new double[Motion.Length];
            for (int i = 0; i < Motion.Length; i++)
            {
                double neutral = BuiltInEffects.TransformParameters.DefaultValue(Motion[i]);
                baseline[i] = original.GetValueOrDefault(Motion[i])?.Evaluate(at) ?? neutral;
                delta[i] = curves[i]?.Evaluate(local) ?? neutral;
            }

            (double x, double y, double scale, double rotation, double opacity) = TitleMotion.Compose(
                baseline[0], baseline[1], baseline[2], baseline[3], baseline[4],
                delta[0] * units, delta[1] * units, delta[2], delta[3], delta[4]);
            double[] values = [x, y, scale, rotation, opacity];
            var interps = new Interpolation[Motion.Length];
            for (int i = 0; i < Motion.Length; i++)
            {
                values[i] = specs[i]?.Clamp(values[i]) ?? values[i];
                changed[i] |= Math.Abs(values[i] - baseline[i]) > 1e-9;
                // La posición compuesta sale de las curvas x e y: manda la que hay (x si están las dos).
                Parameter? source = i < 2 ? curves[0] ?? curves[1] : curves[i];
                interps[i] = source?.Keyframes.FindLast(k => k.Time <= local)?.Interpolation ?? Interpolation.Linear;
            }

            rows.Add((at, values, interps));
        }

        for (int i = 0; i < Motion.Length; i++)
        {
            if (!changed[i])
            {
                continue;
            }

            if (!transform.Parameters.TryGetValue(Motion[i], out Parameter? parameter))
            {
                parameter = new Parameter { Value = BuiltInEffects.TransformParameters.DefaultValue(Motion[i]) };
                transform.Parameters[Motion[i]] = parameter;
            }

            foreach ((Time at, double[] values, Interpolation[] interps) in rows)
            {
                Upsert(parameter, at, values[i], interps[i]);
            }
        }
    }

    private static void Upsert(Parameter parameter, Time at, double value, Interpolation interpolation)
    {
        List<Keyframe> keys = parameter.Keyframes;
        int index = keys.FindIndex(k => k.Time >= at);
        var key = new Keyframe(at, value, interpolation);
        if (index >= 0 && keys[index].Time == at)
        {
            keys[index] = key;
        }
        else
        {
            keys.Insert(index < 0 ? keys.Count : index, key);
        }
    }
}
