using Rodaje.Core.Effects;
using Rodaje.Core.Model;
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
        Generator(ctx, clipId).Text = text;
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
    }

    public static void SetBox(EditContext ctx, string clipId, TextFrame? box)
    {
        Generator(ctx, clipId).Box = box?.Clone();
        ctx.EnsureValid();
    }

    /// <summary>
    /// Pasa la entrada y la salida activas del estilo a keyframes de <c>rodaje.transform</c> y las quita del estilo: desde
    /// ahí quedan fijas en el clip y se editan a mano. Combina igual que el motor: suma en posición y rotación, multiplica
    /// en escala y opacidad, con las posiciones escaladas de la referencia del estilo a la secuencia.
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

        StyleAnimation? entry = clip.Generator.Edges.In ? style.In : null;
        StyleAnimation? exit = clip.Generator.Edges.Out ? style.Out : null;
        double entrySeconds = entry?.DurationSeconds ?? 0;
        double exitSeconds = exit?.DurationSeconds ?? 0;
        if (entrySeconds + exitSeconds <= 0)
        {
            throw new EditException("El título no tiene animación de entrada ni de salida activa.");
        }

        // Si no caben las dos, se encogen por igual, como al evaluarlas.
        double clipSeconds = clip.Duration.ToSeconds();
        double fit = Math.Min(1, clipSeconds / (entrySeconds + exitSeconds));
        double units = Math.Min(
            (double)ctx.Sequence.Width / style.Reference.Width,
            (double)ctx.Sequence.Height / style.Reference.Height);

        EffectInstance transform = clip.Effects.Find(e => e.Type == BuiltInEffects.Transform)
            ?? AddTransform(ctx, clip);
        var original = transform.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        Time from = clip.ToEffectTime(clip.Start);
        Time to = from + clip.Duration;
        if (entry is not null)
        {
            Bake(transform, original, entry, from, fit, units, from, to);
        }

        if (exit is not null)
        {
            Bake(transform, original, exit, to - Time.FromSeconds(exitSeconds * fit), fit, units, from, to);
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

    private static void Bake(
        EffectInstance transform,
        Dictionary<string, Parameter> original,
        StyleAnimation animation,
        Time origin,
        double fit,
        double units,
        Time first,
        Time last)
    {
        foreach ((string name, List<StyleKey> keys) in animation.Curves)
        {
            if (keys.Count == 0)
            {
                continue;
            }

            double neutral = BuiltInEffects.TransformParameters.DefaultValue(name);
            Parameter? before = original.GetValueOrDefault(name);
            if (!transform.Parameters.TryGetValue(name, out Parameter? parameter))
            {
                parameter = new Parameter { Value = neutral };
                transform.Parameters[name] = parameter;
            }

            ParameterDefinition? spec = EffectRegistry.Find(BuiltInEffects.Transform)?.FindParameter(name);
            foreach (StyleKey key in keys)
            {
                Time at = Time.Max(first, Time.Min(last, origin + Time.FromSeconds(key.T * fit)));
                double baseline = before?.Evaluate(at) ?? neutral;
                double value = name switch
                {
                    BuiltInEffects.TransformParameters.X or BuiltInEffects.TransformParameters.Y => baseline + (key.Value * units),
                    BuiltInEffects.TransformParameters.Scale or BuiltInEffects.TransformParameters.Opacity => baseline * key.Value,
                    _ => baseline + key.Value,
                };
                Upsert(parameter, at, spec?.Clamp(value) ?? value, key.Interp);
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
