using Rodaje.App.Services.Packages;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>Sonidos de la biblioteca: la copia del WAV entra a la biblioteca (si no estaba) y a la línea de tiempo en un paso.</summary>
public sealed partial class EditorSession
{
    public const string SoundsTrackName = "Efectos";

    /// <summary>
    /// Pone el sonido (ya copiado a <paramref name="wavPath"/>) en <paramref name="start"/>, en la pista
    /// <paramref name="trackId"/> si está libre en ese tramo y sin bloquear, o en una pista «Efectos» nueva. Si el archivo ya
    /// estaba en la biblioteca, usa ese medio. Deja seleccionado el clip.
    /// </summary>
    public bool InsertSound(string wavPath, PackageSound sound, Time start, string? trackId)
    {
        VerifyAccess();
        ArgumentException.ThrowIfNullOrEmpty(wavPath);
        ArgumentNullException.ThrowIfNull(sound);
        string full = Path.GetFullPath(wavPath);
        MediaRef? existing = Project.Media.Find(m => string.Equals(m.Path, full, StringComparison.OrdinalIgnoreCase));
        MediaRef media = existing ?? new MediaRef
        {
            Id = _ids.NewId("media"),
            Path = full,
            Name = sound.Name,
            Duration = sound.Duration,
            HasAudio = true,
            SampleRate = sound.Info.SampleRate,
            AudioChannels = sound.Info.Channels,
        };
        start = Time.Max(Time.Zero, start).SnapToFrame(Sequence.FrameRate);
        Time end = start + media.Duration;
        string trackName = UniqueTrackName(SoundsTrackName);
        var place = new SequenceEditCommand("Añadir sonido", SequenceId, ctx =>
        {
            Track? target = trackId is null ? null : ctx.Sequence.AudioTracks.Find(t => t.Id == trackId);
            string id = target is { Locked: false } && !target.Clips.Exists(c => c.Start < end && c.End > start)
                ? target.Id
                : TimelineEdits.AddTrack(ctx, TrackKind.Audio, ctx.Sequence.AudioTracks.Count, trackName);
            return TimelineEdits.InsertMedia(ctx, media.Id, Time.Zero, media.Duration, id, start, PlacementMode.Strict);
        }, _ids);
        ICommand command = existing is null ? new CompositeCommand("Añadir sonido", [new AddMediaCommand(media), place]) : place;
        if (!Execute(command))
        {
            return false;
        }

        Select([place.CreatedIds[0]]);
        return true;
    }
}
