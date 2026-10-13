using System.Globalization;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>Dónde y cómo caería un medio soltado sobre la línea de tiempo.</summary>
/// <param name="Build">El comando (uno nuevo en cada llamada).</param>
/// <param name="TrackId">La pista que recibe el clip principal.</param>
/// <param name="LinkedTrackId">La pista que recibe el sonido enlazado, si hay.</param>
public sealed record DropPlan(Func<SequenceEditCommand> Build, string TrackId, string? LinkedTrackId, Time At, Time Duration, PlacementMode Mode);

/// <summary>Las decisiones de la línea de tiempo que no dependen de la interfaz: soltar medios, regiones, nombres.</summary>
public static class TimelinePlanning
{
    /// <summary>Duración de la región que se crea sin selección ni clip bajo el cursor.</summary>
    public static readonly Time DefaultRegionLength = Time.FromSeconds(5);

    /// <summary>
    /// Un medio soltado en <paramref name="position"/> sobre <paramref name="row"/>. En la pista magnética se inserta en
    /// el corte más cercano, con su sonido enlazado en la primera pista de audio; en las demás pistas tapa lo que haya
    /// (o lo corre, con <paramref name="insert"/>). El sonido solo se enlaza si la pista de video lo trae según
    /// <see cref="DragOptions.LinkedAudio"/>; si no, el medio entra sin clip de audio. Un medio sin imagen sobre una
    /// pista de video va a la última de audio (música). <c>null</c> si no tiene dónde ir.
    /// </summary>
    public static DropPlan? PlanDrop(
        Project project,
        Sequence sequence,
        MediaRef media,
        TrackRow? row,
        Time position,
        bool insert,
        DragOptions options,
        Time? playhead,
        IIdGenerator? ids = null,
        (Time In, Time Out)? range = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(options);
        if (row is null || media.Duration <= Time.Zero)
        {
            return null;
        }

        Track? track = row.Track;
        if (track.Kind == TrackKind.Video && !media.HasVideo)
        {
            track = media.HasAudio ? sequence.AudioTracks.LastOrDefault() : null;
        }
        else if (track.Kind == TrackKind.Audio && !media.HasAudio)
        {
            track = null;
        }

        if (track is null || track.Locked)
        {
            return null;
        }

        // Un subclip trae su tramo; un medio entra entero.
        (Time sourceIn, Time sourceOut) = range is { } r && r.Out > r.In && r.In >= Time.Zero && r.Out <= media.Duration ? r : (Time.Zero, media.Duration);
        Time duration = sourceOut - sourceIn;
        Time at = position.SnapToFrame(sequence.FrameRate);
        if (options.Snapping)
        {
            SnapPoints snaps = SnapPoints.Collect(sequence, new HashSet<string>(), playhead);
            Time threshold = Time.FromSeconds(SnapPoints.ThresholdPixels / options.PixelsPerSecond);
            (Time correction, _) = snaps.SnapSpan(at, at + duration, threshold);
            at = Time.Max(Time.Zero, at + correction);
        }

        PlacementMode mode = insert ? PlacementMode.Insert : PlacementMode.Overwrite;
        string? linked = null;
        bool magnetic = options.MagneticTrackId == track.Id;
        bool linkAudio = media.HasAudio && options.LinkedAudio.Allows(sequence, track.Id);
        if (magnetic)
        {
            Time origin = track.Clips.Count > 0 ? track.Clips[0].Start : Time.Zero;
            (_, at) = ClipDrag.MagneticSlot(track.Clips, origin, at, duration);
            mode = PlacementMode.Insert;
            linked = linkAudio ? sequence.AudioTracks.FirstOrDefault(t => !t.Locked)?.Id : null;
        }
        else if (track.Kind == TrackKind.Video && linkAudio)
        {
            Time end = at + duration;
            linked = sequence.AudioTracks.FirstOrDefault(t => !t.Locked && !t.Clips.Exists(c => c.Start < end && c.End > at))?.Id;
        }

        string trackId = track.Id;
        string sequenceId = sequence.Id;
        string mediaId = media.Id;
        Time place = at;
        RippleScope scope = options.RippleScope;
        return new DropPlan(
            magnetic
                ? () => MagneticEdits.InsertMedia(sequenceId, mediaId, duration, trackId, place, linked, scope, ids, sourceIn)
                : () => Edits.InsertMedia(sequenceId, mediaId, sourceIn, sourceOut, trackId, place, mode, linked, ids),
            trackId,
            linked,
            at,
            duration,
            mode);
    }

    /// <summary>
    /// El tramo de una región nueva: el rango marcado si lo hay; si no, la entrada y la salida de la secuencia (I y O);
    /// si no, lo que abarcan los clips seleccionados; si no, el clip de la pista principal bajo el cursor; y si tampoco,
    /// unos segundos desde el cursor.
    /// </summary>
    public static (Time Start, Time End) RegionSpan(Sequence sequence, IReadOnlyList<string> selection, (Time Start, Time End)? range, Track? mainTrack, Time playhead,
        (Time? In, Time? Out) marks = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(selection);
        if (range is { } r && r.End > r.Start)
        {
            return r;
        }

        if (marks is ({ } markIn, { } markOut) && markOut > markIn)
        {
            return (markIn, markOut);
        }

        List<Clip> selected = [.. selection.Select(id => sequence.FindClip(id)?.Clip).OfType<Clip>()];
        if (selected.Count > 0)
        {
            return (selected.Min(c => c.Start), selected.Max(c => c.End));
        }

        if (mainTrack?.ClipAt(playhead) is { } clip)
        {
            return (clip.Start, clip.End);
        }

        return (playhead, playhead + DefaultRegionLength);
    }

    /// <summary>«Marcador 3», con el primer número libre.</summary>
    public static string NextName(string prefix, IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var used = new HashSet<string>(existing, StringComparer.Ordinal);
        for (int n = 1; ; n++)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"{prefix} {n}");
            if (!used.Contains(name))
            {
                return name;
            }
        }
    }
}
