using Rodaje.App.Services.Settings;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// Ctrl+T: la transición por defecto en los cortes de los clips seleccionados (al final de cada uno o, si no tiene
/// vecino, al principio) o, sin selección, en el corte más cercano al cursor de la pista principal (o de cualquier pista).
/// Si el corte ya tiene transición, cambia su tipo y conserva la duración. Todo en un paso del historial.
/// </summary>
public static class DefaultTransition
{
    /// <summary>Los cortes donde iría, por el id del clip que sale; vacío si no hay ninguno.</summary>
    public static IReadOnlyList<string> Cuts(Sequence sequence, IReadOnlyCollection<string> selectedClipIds, Time playhead, Track? preferred)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(selectedClipIds);
        var cuts = new List<string>();
        foreach (string id in selectedClipIds)
        {
            if (sequence.FindClip(id) is not { } found || found.Track.Locked)
            {
                continue;
            }

            List<Clip> clips = found.Track.Clips;
            int index = clips.IndexOf(found.Clip);
            string? from = index + 1 < clips.Count && clips[index + 1].Start == found.Clip.End ? found.Clip.Id
                : index > 0 && clips[index - 1].End == found.Clip.Start ? clips[index - 1].Id
                : null;
            if (from is not null && !cuts.Contains(from))
            {
                cuts.Add(from);
            }
        }

        if (cuts.Count > 0 || selectedClipIds.Count > 0)
        {
            return cuts;
        }

        IEnumerable<Track> tracks = preferred is { Locked: false } ? [preferred] : [];
        foreach (Track track in tracks.Concat(sequence.AllTracks.Where(t => !t.Locked && t != preferred)))
        {
            (Clip From, Time Cut)? best = null;
            for (int i = 1; i < track.Clips.Count; i++)
            {
                Clip clip = track.Clips[i - 1];
                if (clip.End == track.Clips[i].Start && (best is null || Distance(clip.End, playhead) < Distance(best.Value.Cut, playhead)))
                {
                    best = (clip, clip.End);
                }
            }

            if (best is { } nearest)
            {
                return [nearest.From.Id];
            }
        }

        return cuts;
    }

    /// <summary>El comando que la pone en esos cortes; <see cref="SequenceEditCommand.CreatedIds"/> son las transiciones.</summary>
    public static SequenceEditCommand Build(string sequenceId, IReadOnlyList<string> fromClipIds, TransitionPreferences preferences, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(fromClipIds);
        ArgumentNullException.ThrowIfNull(preferences);
        return new SequenceEditCommand("Añadir transición por defecto", sequenceId, ctx =>
        {
            var created = new List<string>();
            foreach (string from in fromClipIds)
            {
                Track track = ctx.Sequence.FindClip(from)?.Track ?? throw new EditException($"El clip «{from}» ya no está en la secuencia.");
                string reference = track.Kind == TrackKind.Audio ? preferences.AudioReference : preferences.Reference;
                bool builtIn = EffectRegistry.Find(reference) is { IsTransition: true };
                string type = builtIn ? reference : BuiltInTransitions.Preset;
                string id;
                if (track.TransitionFrom(from) is { } existing)
                {
                    TransitionEdits.SetType(ctx, existing.Id, type);
                    id = existing.Id;
                }
                else
                {
                    Clip next = track.Clips[track.Clips.FindIndex(c => c.Id == from) + 1];
                    var probe = new TransitionInstance { From = from, To = next.Id, Alignment = preferences.Alignment };
                    Time max = TransitionOps.MaxDuration(track, probe);
                    Time frame = Time.FromFrames(1, ctx.Sequence.FrameRate);
                    Time wanted = Time.Max(frame, Time.FromSeconds(preferences.Seconds).SnapToFrame(ctx.Sequence.FrameRate));
                    id = TransitionEdits.AddTransition(ctx, from, type, Time.Max(Time.FromTicks(1), Time.Min(wanted, max)));
                }

                if (!builtIn && PackageReference.TryParse(reference, out _))
                {
                    TransitionEdits.SetOption(ctx, id, BuiltInTransitions.PresetOptions.Preset, reference);
                }

                TransitionEdits.SetAlignment(ctx, id, preferences.Alignment);
                TransitionEdits.SetCurve(ctx, id, preferences.Curve, preferences.Ease);
                created.Add(id);
            }

            return created;
        }, ids);
    }

    /// <summary>Guarda <paramref name="transition"/> como la de por defecto de su clase (video o audio).</summary>
    public static void Remember(TransitionPreferences preferences, TransitionInstance transition)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(transition);
        string reference = transition.Type == BuiltInTransitions.Preset
            ? transition.Options.GetValueOrDefault(BuiltInTransitions.PresetOptions.Preset) ?? BuiltInTransitions.Crossfade
            : transition.Type;
        if (BuiltInTransitions.IsAudio(reference))
        {
            preferences.AudioReference = reference;
        }
        else
        {
            preferences.Reference = reference;
        }

        preferences.Seconds = transition.Duration.ToSeconds();
        preferences.Alignment = transition.Alignment;
        preferences.Curve = transition.Curve;
        preferences.Ease = transition.Ease;
        preferences.Normalize();
    }

    private static long Distance(Time a, Time b) => Math.Abs((a - b).Ticks);
}
