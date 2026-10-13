using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Ediciones que meten algo en la pista principal magnética sin desincronizar: el espacio se abre en ella y en las
/// pistas de los enlazados de lo que se corre (o en todas, según el ripple), como hace el borrado ripple al cerrarlo.
/// </summary>
public static class MagneticEdits
{
    /// <summary>
    /// Corre a la derecha, desde <paramref name="at"/>, la pista principal y las de los enlazados de lo que se corre (con
    /// ripple de todas las pistas, todas las no bloqueadas), salvo <paramref name="skip"/>.
    /// </summary>
    public static void OpenSpace(EditContext ctx, Track main, Time at, Time duration, RippleScope scope, IReadOnlySet<Clip>? exclude = null, IReadOnlyCollection<Track>? skip = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(main);
        var tracks = new List<Track>();
        if (scope.IncludesAllTracks())
        {
            tracks.AddRange(ctx.Sequence.AllTracks.Where(t => !t.Locked));
        }
        else
        {
            tracks.Add(main);
            foreach (Clip clip in main.Clips)
            {
                if (clip.Start < at || clip.LinkId is null || (exclude?.Contains(clip) ?? false))
                {
                    continue;
                }

                foreach ((Track track, _) in ctx.Sequence.GetLinkGroup(clip.Id).Skip(1))
                {
                    if (!tracks.Contains(track))
                    {
                        tracks.Add(track);
                    }
                }
            }
        }

        foreach (Track track in tracks)
        {
            if (skip is not null && skip.Contains(track))
            {
                continue;
            }

            EditContext.EnsureUnlocked(track);
            EditContext.ShiftClips(track, at, duration, exclude);
        }

        if (scope == RippleScope.AllTracksAndMarkers)
        {
            EditContext.ShiftMarkers(ctx.Sequence, at, duration);
        }
    }

    /// <summary>Lleva el clip (con sus enlazados, salvo <paramref name="solo"/>) a la pista principal, en el corte <paramref name="at"/>.</summary>
    public static SequenceEditCommand EnterMainTrack(string sequenceId, string clipId, string mainTrackId, Time at, RippleScope scope, bool solo, IIdGenerator? ids) =>
        new("Mover clip", sequenceId, ctx =>
        {
            (Track track, Clip clip) = ctx.Sequence.GetClip(clipId);
            List<(Track Track, Clip Clip)> group = solo ? [(track, clip)] : ctx.Sequence.GetLinkGroup(clipId);
            OpenSpace(ctx, ctx.Sequence.GetTrack(mainTrackId), at, clip.Duration, scope, new HashSet<Clip>(group.Select(g => g.Clip)));
            string? link = clip.LinkId;
            if (solo)
            {
                clip.LinkId = null;
            }

            try
            {
                TimelineEdits.Move(ctx, clipId, at, mainTrackId, PlacementMode.Strict);
            }
            finally
            {
                clip.LinkId = link;
            }

            ctx.EnsureValid();
        }, ids);

    /// <summary>Coloca un medio entero en la pista principal en el corte <paramref name="at"/>, con su sonido en <paramref name="linkedTrackId"/>.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los clips creados, el de la pista principal primero.</remarks>
    public static SequenceEditCommand InsertMedia(string sequenceId, string mediaId, Time duration, string mainTrackId, Time at, string? linkedTrackId, RippleScope scope, IIdGenerator? ids, Time sourceIn = default) =>
        new("Insertar", sequenceId, ctx =>
        {
            Track main = ctx.Sequence.GetTrack(mainTrackId);
            List<Track> skip = [main];
            if (linkedTrackId is not null)
            {
                skip.Add(ctx.Sequence.GetTrack(linkedTrackId));
            }

            // Con el alcance de la pista, la inserción misma corre la principal, la del sonido y las de los enlazados de lo
            // que se corre. Con todas las pistas, aquí se corren las demás y la inserción solo las suyas.
            bool allTracks = scope.IncludesAllTracks();
            if (allTracks)
            {
                OpenSpace(ctx, main, at, duration, scope, skip: skip);
            }

            return TimelineEdits.InsertMedia(ctx, mediaId, sourceIn, sourceIn + duration, mainTrackId, at, PlacementMode.Insert, linkedTrackId, followLinks: !allTracks);
        }, ids);
}
