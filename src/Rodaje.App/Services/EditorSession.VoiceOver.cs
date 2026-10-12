using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>Voz en off: la toma grabada entra a la biblioteca y a la línea de tiempo en un solo paso.</summary>
public sealed partial class EditorSession
{
    public const string VoiceOverTrackName = "Voz en off";

    /// <summary>
    /// Agrega el WAV grabado y lo pone en <paramref name="start"/>, en la pista <paramref name="trackId"/> si está libre en ese
    /// tramo y sin bloquear, o en una pista «Voz en off» nueva. Deja seleccionado el clip.
    /// </summary>
    public bool InsertVoiceOver(string wavPath, long frames, int sampleRate, Time start, string? trackId)
    {
        VerifyAccess();
        ArgumentException.ThrowIfNullOrEmpty(wavPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        var media = new MediaRef
        {
            Id = _ids.NewId("media"),
            Path = Path.GetFullPath(wavPath),
            Name = "Voz en off " + Path.GetFileNameWithoutExtension(wavPath).Replace("voz-en-off-", "", StringComparison.Ordinal),
            Duration = Time.FromSamples(frames, sampleRate),
            HasAudio = true,
            SampleRate = sampleRate,
            AudioChannels = 1,
        };
        Time end = start + media.Duration;
        string trackName = UniqueTrackName(VoiceOverTrackName);
        var place = new SequenceEditCommand("Grabar voz en off", SequenceId, ctx =>
        {
            Track? target = trackId is null ? null : ctx.Sequence.AudioTracks.Find(t => t.Id == trackId);
            string id = target is { Locked: false } && !target.Clips.Exists(c => c.Start < end && c.End > start)
                ? target.Id
                : TimelineEdits.AddTrack(ctx, TrackKind.Audio, ctx.Sequence.AudioTracks.Count, trackName);
            return TimelineEdits.InsertMedia(ctx, media.Id, Time.Zero, media.Duration, id, start, PlacementMode.Strict);
        }, _ids);
        if (!Execute(new CompositeCommand("Grabar voz en off", [new AddMediaCommand(media), place])))
        {
            return false;
        }

        Select([place.CreatedIds[0]]);
        return true;
    }
}
