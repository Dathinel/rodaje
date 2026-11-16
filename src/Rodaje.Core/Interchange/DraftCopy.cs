using Rodaje.Core.Model;

namespace Rodaje.Core.Interchange;

/// <summary>Una copia de un <see cref="TimelineDraft"/> con todos sus identificadores nuevos, los compuestos de dentro hacia fuera.</summary>
internal sealed record RenumberedDraft(List<MediaRef> Media, List<Sequence> Compounds, Sequence Main);

internal static class DraftCopy
{
    /// <summary>
    /// Las secuencias de las que depende <paramref name="main"/> (sus compuestos, a cualquier profundidad), de la más interna
    /// hacia fuera y sin ella. Lanza <see cref="InterchangeException"/> con un ciclo, un compuesto que no existe o más
    /// anidado de lo que admite <paramref name="limits"/>.
    /// </summary>
    public static List<Sequence> CompoundsOf(Project project, Sequence main, InterchangeLimits limits)
    {
        var order = new List<Sequence>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var path = new HashSet<string>(StringComparer.Ordinal) { main.Id };

        void Visit(Sequence sequence, int depth)
        {
            if (depth > limits.MaxDepth)
            {
                throw new InterchangeException($"Los clips compuestos están anidados más de {limits.MaxDepth} niveles.");
            }

            foreach (string id in sequence.AllTracks.SelectMany(t => t.Clips).Select(c => c.SequenceId).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (done.Contains(id))
                {
                    continue;
                }

                if (!path.Add(id))
                {
                    throw new InterchangeException("Un clip compuesto se contiene a sí mismo.");
                }

                Sequence inner = project.FindSequence(id) ?? throw new InterchangeException($"Falta la secuencia del clip compuesto «{id}».");
                Visit(inner, depth + 1);
                path.Remove(id);
                done.Add(id);
                order.Add(inner);
            }
        }

        Visit(main, 1);
        return order;
    }

    /// <summary>
    /// Copia lo que usa <paramref name="draft"/> con identificadores de <paramref name="ids"/>. Los medios cuyo identificador
    /// está en <paramref name="reuse"/> no se copian: sus clips pasan a usar el identificador del valor.
    /// </summary>
    public static RenumberedDraft Renumber(TimelineDraft draft, IIdGenerator ids, InterchangeLimits limits, IReadOnlyDictionary<string, string>? reuse = null)
    {
        List<Sequence> compounds = CompoundsOf(draft.Project, draft.Sequence, limits);
        var mediaIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var media = new List<MediaRef>();
        foreach (MediaRef original in draft.Project.Media)
        {
            if (mediaIds.ContainsKey(original.Id))
            {
                continue;
            }

            if (reuse is not null && reuse.TryGetValue(original.Id, out string? existing))
            {
                mediaIds[original.Id] = existing;
                continue;
            }

            MediaRef copy = original.Clone();
            copy.Id = ids.NewId("media");
            mediaIds[original.Id] = copy.Id;
            media.Add(copy);
        }

        var sequenceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        Sequence Copy(Sequence original)
        {
            Sequence copy = original.Clone();
            copy.Id = ids.NewId("sequence");
            sequenceIds[original.Id] = copy.Id;
            var buses = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (AudioBus bus in copy.AudioBuses)
            {
                buses[bus.Id] = bus.Id = ids.NewId("bus");
                Effects(bus.Effects);
            }

            Effects(copy.MasterEffects);
            foreach (Track track in copy.AllTracks)
            {
                track.Id = ids.NewId("track");
                track.Output = track.Output is null ? null : buses.GetValueOrDefault(track.Output, track.Output);
                foreach (AudioSend send in track.Sends)
                {
                    send.BusId = buses.GetValueOrDefault(send.BusId, send.BusId);
                }

                Effects(track.Effects);
                var clipIds = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (Clip clip in track.Clips)
                {
                    clipIds[clip.Id] = clip.Id = ids.NewId("clip");
                    if (clip.MediaId.Length > 0)
                    {
                        clip.MediaId = mediaIds.TryGetValue(clip.MediaId, out string? m) ? m : throw new InterchangeException($"Un clip usa el medio «{clip.MediaId}», que no está en el archivo.");
                    }

                    if (clip.SequenceId is { } inner)
                    {
                        clip.SequenceId = sequenceIds[inner];
                    }

                    clip.LinkId = clip.LinkId is null ? null : Get(links, clip.LinkId, "link");
                    clip.GroupId = clip.GroupId is null ? null : Get(groups, clip.GroupId, "group");
                    Effects(clip.Effects);
                }

                foreach (TransitionInstance transition in track.Transitions)
                {
                    transition.Id = ids.NewId("transition");
                    transition.From = clipIds.GetValueOrDefault(transition.From, transition.From);
                    transition.To = clipIds.GetValueOrDefault(transition.To, transition.To);
                }
            }

            foreach (Marker marker in copy.Markers)
            {
                marker.Id = ids.NewId("marker");
            }

            foreach (Region region in copy.Regions)
            {
                region.Id = ids.NewId("region");
            }

            return copy;
        }

        void Effects(List<EffectInstance> effects)
        {
            foreach (EffectInstance effect in effects)
            {
                effect.Id = ids.NewId("effect");
            }
        }

        string Get(Dictionary<string, string> map, string key, string prefix)
        {
            if (!map.TryGetValue(key, out string? value))
            {
                map[key] = value = ids.NewId(prefix);
            }

            return value;
        }

        List<Sequence> copies = [.. compounds.Select(Copy)];
        return new RenumberedDraft(media, copies, Copy(draft.Sequence));
    }
}
