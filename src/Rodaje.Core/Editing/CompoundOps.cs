using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>
/// Clips compuestos: un clip que muestra un tramo de otra secuencia del proyecto (formato 6). <see cref="Flatten(Project, Sequence)"/> da la
/// secuencia con cada compuesto reemplazado por los clips que muestra, que es lo que reproducen la vista previa y la
/// exportación.
/// </summary>
public static class CompoundOps
{
    /// <summary>Separador de los ids que <see cref="Flatten(Project, Sequence)"/> da a lo que saca de un compuesto: <c>compuesto/interno</c>.</summary>
    public const char IdSeparator = '/';

    /// <summary>Hasta dónde llega el origen de un compuesto: el final de su secuencia.</summary>
    public static Time SourceLimit(Project project, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clip);
        return clip.SequenceId is { } id && project.FindSequence(id) is { } nested ? nested.Duration : Time.Zero;
    }

    public static bool HasCompounds(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return sequence.AllTracks.Any(t => t.Clips.Exists(c => c.IsCompound));
    }

    /// <summary>
    /// La secuencia con cada compuesto desplegado: la pista <c>i</c> de lo anidado va en la capa <c>i</c> sobre la pista
    /// del compuesto (la 0 comparte la pista; las demás son pistas nuevas justo encima, con id <c>pista/i</c>), recortado
    /// a su tramo. Es recursivo; un ciclo o más de <see cref="ModelValidator.MaxCompoundDepth"/> niveles no se muestran.
    /// Las pistas anidadas ocultas (video) o mudas (audio) no entran. Sin compuestos devuelve la misma secuencia.
    /// </summary>
    public static Sequence Flatten(Project project, Sequence sequence) => Flatten(project, sequence, null, null);

    /// <summary>
    /// Como <see cref="Flatten(Project, Sequence)"/>, pero deja tal cual los compuestos para los que <paramref name="keep"/>
    /// (con su pista) dice que sí: quien reproduce los compone aparte, con sus efectos y transiciones. <paramref name="path"/>
    /// son las secuencias que ya contienen a esta, para cortar ciclos y la profundidad; por omisión, solo ella.
    /// </summary>
    public static Sequence Flatten(Project project, Sequence sequence, Func<Track, Clip, bool>? keep, IReadOnlyList<string>? path = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        return HasCompounds(sequence) ? FlattenPath(project, sequence, path is null ? [sequence.Id] : [.. path], keep) : sequence;
    }

    /// <summary>Un compuesto que se puede mostrar desde <paramref name="path"/>: sin ciclo ni más niveles de los permitidos.</summary>
    public static Sequence? Nested(Project project, Clip compound, IReadOnlyList<string> path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(compound);
        ArgumentNullException.ThrowIfNull(path);
        return compound.SequenceId is { } id && !path.Contains(id) && path.Count <= ModelValidator.MaxCompoundDepth
            ? project.FindSequence(id)
            : null;
    }

    private static Sequence FlattenPath(Project project, Sequence sequence, List<string> path, Func<Track, Clip, bool>? keep) => new()
    {
        Id = sequence.Id,
        Name = sequence.Name,
        FrameRate = sequence.FrameRate,
        Width = sequence.Width,
        Height = sequence.Height,
        SampleRate = sequence.SampleRate,
        MasterGainDb = sequence.MasterGainDb,
        MasterEffects = sequence.MasterEffects,
        AudioBuses = sequence.AudioBuses,
        Markers = sequence.Markers,
        Regions = sequence.Regions,
        VideoTracks = Expand(project, sequence.VideoTracks, TrackKind.Video, path, keep),
        AudioTracks = Expand(project, sequence.AudioTracks, TrackKind.Audio, path, keep),
    };

    private static List<Track> Expand(Project project, List<Track> tracks, TrackKind kind, List<string> path, Func<Track, Clip, bool>? keep)
    {
        var result = new List<Track>();
        foreach (Track track in tracks)
        {
            bool Expands(Clip c) => c.IsCompound && (keep is null || !keep(track, c));
            if (!track.Clips.Exists(Expands))
            {
                result.Add(track);
                continue;
            }

            var compoundIds = track.Clips.Where(Expands).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            Track lane0 = track.Clone();
            lane0.Clips.RemoveAll(c => compoundIds.Contains(c.Id));
            lane0.Transitions.RemoveAll(t => compoundIds.Contains(t.From) || compoundIds.Contains(t.To));
            var lanes = new List<Track> { lane0 };
            foreach (Clip compound in track.Clips.Where(c => compoundIds.Contains(c.Id) && c.Enabled))
            {
                if (Nested(project, compound, path) is not { } nested)
                {
                    continue;
                }

                Sequence flat = FlattenPath(project, nested, [.. path, nested.Id], keep);
                List<Track> inner = kind == TrackKind.Video
                    ? flat.VideoTracks.FindAll(t => !t.Hidden)
                    : flat.AudioTracks.FindAll(t => !t.Muted);
                for (int k = 0; k < inner.Count; k++)
                {
                    if (k == lanes.Count)
                    {
                        lanes.Add(new Track
                        {
                            Id = $"{track.Id}{IdSeparator}{k}",
                            Kind = track.Kind,
                            Name = track.Name,
                            Muted = track.Muted,
                            Hidden = track.Hidden,
                            Role = track.Role,
                            Solo = track.Solo,
                            Output = track.Output,
                            Sends = track.Sends.ConvertAll(s => s.Clone()),
                            Effects = track.Effects.ConvertAll(e => e.Clone()),
                        });
                    }

                    string prefix = compound.Id + IdSeparator;
                    Window(inner[k], compound, lanes[k], id => prefix + id);
                }
            }

            foreach (Track lane in lanes)
            {
                lane.SortClips();
            }

            result.AddRange(lanes);
        }

        return result;
    }

    /// <summary>
    /// Copia en <paramref name="target"/> lo de <paramref name="source"/> que muestra <paramref name="compound"/>: recortado
    /// a su tramo y llevado a su lugar, con las transiciones entre clips copiados. Un título recortado guarda cuánto quedó
    /// fuera (como al dividirlo), así se ve igual que dentro.
    /// </summary>
    /// <returns>Los clips copiados.</returns>
    public static List<Clip> Window(Track source, Clip compound, Track target, Func<string, string> rename)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(compound);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rename);
        Time from = compound.SourceIn;
        Time to = compound.SourceOut;
        Time offset = compound.Start - compound.SourceIn;
        var copied = new List<Clip>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Clip original in source.Clips)
        {
            if (original.End <= from || original.Start >= to)
            {
                continue;
            }

            Clip clip = original.Clone();
            if (clip.Start < from)
            {
                Time cut = from - clip.Start;
                clip.TimeMap.TrimIn(from).ApplyTo(clip);
                if (clip.Generator is { } g)
                {
                    g.Edges.Before += cut;
                }
            }

            if (clip.End > to)
            {
                Time cut = clip.End - to;
                clip.TimeMap.TrimOut(to).ApplyTo(clip);
                if (clip.Generator is { } g)
                {
                    g.Edges.After += cut;
                }
            }

            clip.Start += offset;
            clip.Id = rename(original.Id);
            clip.LinkId = clip.LinkId is null ? null : rename(clip.LinkId);
            foreach (EffectInstance effect in clip.Effects)
            {
                effect.Id = rename(effect.Id);
            }

            ids[original.Id] = clip.Id;
            target.Clips.Add(clip);
            copied.Add(clip);
        }

        foreach (TransitionInstance transition in source.Transitions)
        {
            if (ids.TryGetValue(transition.From, out string? fromId) && ids.TryGetValue(transition.To, out string? toId))
            {
                TransitionInstance copy = transition.Clone();
                copy.Id = rename(transition.Id);
                copy.From = fromId;
                copy.To = toId;
                target.Transitions.Add(copy);
            }
        }

        return copied;
    }
}
