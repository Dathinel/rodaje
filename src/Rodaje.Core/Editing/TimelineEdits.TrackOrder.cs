using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>
/// Borrar y reordenar pistas desde la línea de tiempo. La principal es la primera de video: no se borra ni se mueve y
/// ninguna otra pasa a su lugar, así el magnetismo nunca salta a una pista que tenía huecos.
/// </summary>
public static partial class TimelineEdits
{
    /// <summary>Si <paramref name="track"/> es la pista principal de la secuencia.</summary>
    public static bool IsMainTrack(Sequence sequence, Track track)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return sequence.VideoTracks.Count > 0 && ReferenceEquals(sequence.VideoTracks[0], track);
    }

    /// <summary>
    /// Como <see cref="RemoveTrack"/>, pero sin tocar la principal; los compresores que se disparaban con esta pista
    /// vuelven a usar su propio audio, salvo los de pistas bloqueadas (una llave que no existe se ignora al mezclar).
    /// </summary>
    public static void DeleteTrack(EditContext ctx, string trackId)
    {
        Track track = TrimmingSupport.ResolveTrack(ctx, trackId);
        if (IsMainTrack(ctx.Sequence, track))
        {
            throw new EditException($"La pista principal «{track.Name}» no se puede eliminar.");
        }

        RemoveTrack(ctx, trackId);
        foreach (EffectInstance effect in AllEffects(ctx.Sequence))
        {
            if (effect.Options.TryGetValue(BuiltInEffects.CompressorOptions.Sidechain, out string? key) && key == track.Id)
            {
                effect.Options.Remove(BuiltInEffects.CompressorOptions.Sidechain);
            }
        }
    }

    /// <summary>Como <see cref="MoveTrack"/>, pero la principal se queda primera.</summary>
    public static void ReorderTrack(EditContext ctx, string trackId, int newIndex)
    {
        Track track = TrimmingSupport.ResolveTrack(ctx, trackId);
        if (IsMainTrack(ctx.Sequence, track))
        {
            throw new EditException($"La pista principal «{track.Name}» no se mueve.");
        }

        if (track.Kind == TrackKind.Video && newIndex == 0)
        {
            throw new EditException("La pista no puede ir debajo de la principal.");
        }

        MoveTrack(ctx, trackId, newIndex);
    }

    /// <summary>
    /// Sube o baja la pista un lugar en la línea de tiempo: en video, subir la pone por encima en la imagen; en audio,
    /// la acerca al video.
    /// </summary>
    public static void StepTrack(EditContext ctx, string trackId, bool up)
    {
        Track track = TrimmingSupport.ResolveTrack(ctx, trackId);
        int target = SteppedIndex(ctx.Sequence, track, up)
            ?? throw new EditException(up ? $"La pista «{track.Name}» ya está lo más arriba que puede." : $"La pista «{track.Name}» ya está lo más abajo que puede.");
        ReorderTrack(ctx, trackId, target);
    }

    /// <summary>Si <see cref="StepTrack"/> puede mover la pista en esa dirección (no mira bloqueos).</summary>
    public static bool CanStepTrack(Sequence sequence, string trackId, bool up)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return sequence.FindTrack(trackId) is { } track && SteppedIndex(sequence, track, up) is not null;
    }

    /// <summary>
    /// La primera pista de audio sin bloquear que está libre en [<paramref name="from"/>, <paramref name="to"/>), sin
    /// contar los clips de <paramref name="ignore"/> ni las pistas de <paramref name="exclude"/>.
    /// </summary>
    public static Track? FreeAudioTrack(Sequence sequence, Time from, Time to, IReadOnlySet<Clip>? ignore = null, IReadOnlyCollection<Track>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return sequence.AudioTracks.Find(t => !t.Locked && exclude?.Contains(t) != true && TrackOps.FindOverlap(t, from, to, ignore) is null);
    }

    /// <summary>
    /// Dónde va el sonido enlazado de un clip de video en ese tramo: <see cref="FreeAudioTrack"/> y, si no hay, una pista
    /// de audio nueva al final del bloque de audio, sin reordenar las demás.
    /// </summary>
    public static Track LinkedAudioTrack(EditContext ctx, Time from, Time to, IReadOnlySet<Clip>? ignore = null, IReadOnlyCollection<Track>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (FreeAudioTrack(ctx.Sequence, from, to, ignore, exclude) is { } free)
        {
            return free;
        }

        string id = AddTrack(ctx, TrackKind.Audio, ctx.Sequence.AudioTracks.Count, NewAudioTrackName(ctx.Sequence));
        return ctx.Sequence.GetTrack(id);
    }

    /// <summary>Coloca un medio en una pista de video con su sonido en <see cref="LinkedAudioTrack"/>.</summary>
    /// <returns>Los identificadores de los clips creados; el de video va primero.</returns>
    public static IReadOnlyList<string> InsertMediaWithLinkedAudio(
        EditContext ctx,
        string mediaId,
        Time sourceIn,
        Time sourceOut,
        string trackId,
        Time at,
        PlacementMode mode)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Track audio = LinkedAudioTrack(ctx, at, at + (sourceOut - sourceIn));
        return InsertMedia(ctx, mediaId, sourceIn, sourceOut, trackId, at, mode, audio.Id);
    }

    private static int? SteppedIndex(Sequence sequence, Track track, bool up)
    {
        List<Track> list = TracksSupport.ListOf(sequence, track.Kind);
        int index = list.IndexOf(track);
        int target = track.Kind == TrackKind.Video ? (up ? index + 1 : index - 1) : (up ? index - 1 : index + 1);
        int lowest = track.Kind == TrackKind.Video ? 1 : 0;
        return index < lowest || target < lowest || target >= list.Count ? null : target;
    }

    private static string NewAudioTrackName(Sequence sequence)
    {
        var used = new HashSet<string>(sequence.AllTracks.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        for (int n = sequence.AudioTracks.Count + 1; ; n++)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"Audio {n}");
            if (!used.Contains(name))
            {
                return name;
            }
        }
    }

    private static IEnumerable<EffectInstance> AllEffects(Sequence sequence) =>
        sequence.MasterEffects
            .Concat(sequence.AudioBuses.SelectMany(b => b.Effects))
            .Concat(sequence.AllTracks.Where(t => !t.Locked).SelectMany(t => t.Effects.Concat(t.Clips.SelectMany(c => c.Effects))));
}
