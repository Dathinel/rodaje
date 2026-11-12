using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.Core.Editing;

public static partial class TimelineEdits
{
    /// <summary>
    /// Separa el audio de un clip de video a su propia pista: crea el clip de audio con el mismo tramo del medio, el mismo
    /// lugar y la misma velocidad, enlazado con el de video. Los efectos de audio que tuviera el clip de video (volumen y
    /// fundidos, que en una pista de video no suenan) pasan al clip nuevo.
    /// </summary>
    /// <param name="ctx">Contexto de la edición.</param>
    /// <param name="clipId">El clip de video.</param>
    /// <param name="audioTrackId">
    /// Pista de destino; sin ella, la primera de audio sin bloquear que esté libre en ese tramo, o una nueva al final.
    /// </param>
    /// <returns>Identificador del clip de audio nuevo.</returns>
    public static string DetachAudio(EditContext ctx, string clipId, string? audioTrackId = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        (Track videoTrack, Clip video) = group.Find(m => m.Clip.Id == clipId);
        if (videoTrack.Kind != TrackKind.Video)
        {
            throw new EditException($"El clip «{video.Id}» ya está en una pista de audio.");
        }

        if (video.Generator is not null)
        {
            throw new EditException($"El clip «{video.Id}» es un título y no tiene sonido.");
        }

        if (video.Speed is { Freeze: true })
        {
            throw new EditException($"El clip «{video.Id}» es un fotograma congelado y no tiene sonido.");
        }

        MediaRef media = ctx.MediaOf(video);
        if (!media.HasAudio)
        {
            throw new EditException($"El medio «{media.Name}» no tiene sonido.");
        }

        if (group.Find(m => m.Track.Kind == TrackKind.Audio).Track is { } existing)
        {
            throw new EditException($"El audio de «{video.Id}» ya está separado en la pista «{existing.Name}».");
        }

        EditContext.EnsureUnlocked(videoTrack);
        Track target = DetachAudioTarget(ctx, video, audioTrackId);

        Clip audio = video.Clone();
        audio.Id = ctx.Ids.NewId("clip");
        audio.Effects = video.Effects.FindAll(IsAudioOnly);
        video.Effects.RemoveAll(IsAudioOnly);
        string linkId = video.LinkId ?? ctx.Ids.NewId("link");
        video.LinkId = linkId;
        audio.LinkId = linkId;
        TrackOps.AddClipSorted(target, audio);
        ctx.EnsureValid();
        return audio.Id;
    }

    private static Track DetachAudioTarget(EditContext ctx, Clip video, string? audioTrackId)
    {
        if (audioTrackId is not null)
        {
            Track track = TrimmingSupport.ResolveTrack(ctx, audioTrackId);
            if (track.Kind != TrackKind.Audio)
            {
                throw new EditException($"La pista «{track.Name}» es de video: el audio va en una de audio.");
            }

            EditContext.EnsureUnlocked(track);
            TrackOps.EnsureFree(track, video.Start, video.End);
            return track;
        }

        List<Track> audioTracks = ctx.Sequence.AudioTracks;
        if (audioTracks.Find(t => !t.Locked && TrackOps.FindOverlap(t, video.Start, video.End) is null) is { } free)
        {
            return free;
        }

        string id = AddTrack(ctx, TrackKind.Audio, audioTracks.Count, $"Audio {audioTracks.Count + 1}");
        return ctx.Sequence.GetTrack(id);
    }

    // Solo los tipos registrados como de audio; uno desconocido se queda donde estaba.
    private static bool IsAudioOnly(EffectInstance effect) =>
        EffectRegistry.Find(effect.Type) is { IsTransition: false } definition && definition.Target == EffectTarget.Audio;
}
