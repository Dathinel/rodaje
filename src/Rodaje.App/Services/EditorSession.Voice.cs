using Rodaje.App.Services.Voice;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>Texto a voz: insertar el audio generado en el cursor y regenerar un clip de voz, cada cosa en un paso.</summary>
public sealed partial class EditorSession
{
    /// <summary>El texto del primer clip de texto o subtítulo seleccionado; <c>null</c> si no hay.</summary>
    public string? SelectedClipText()
    {
        foreach (string id in SelectedClipIds)
        {
            if (Sequence.FindClip(id) is { Clip.Generator: { Kind: GeneratorKinds.Text, Text: { Length: > 0 } text } })
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>El primer clip seleccionado que es voz generada (su WAV tiene el <c>.json</c> al lado), con sus ajustes.</summary>
    public (Clip Clip, VoiceSettings Settings)? SelectedGeneratedVoice()
    {
        foreach (string id in SelectedClipIds)
        {
            if (Sequence.FindClip(id) is { } found && found.Clip.Generator is null
                && Project.FindMedia(found.Clip.MediaId) is { HasAudio: true } media
                && VoiceFiles.ReadSettings(media.Path) is { } settings)
            {
                return (found.Clip, settings);
            }
        }

        return null;
    }

    /// <summary>
    /// Agrega el WAV a la biblioteca y lo pone en el cursor, en la primera pista de audio libre en ese tramo (o en una
    /// pista «Voz» nueva), en un solo paso del historial. Deja seleccionado el clip nuevo.
    /// </summary>
    public bool InsertGeneratedVoice(string wavPath, VoiceSettings settings, long frames, int sampleRate)
    {
        VerifyAccess();
        MediaRef media = VoiceMedia(wavPath, settings, frames, sampleRate);
        Time at = EditPosition;
        Time end = at + media.Duration;
        string trackName = UniqueTrackName("Voz");
        var place = new SequenceEditCommand("Insertar voz", SequenceId, ctx =>
        {
            string trackId = ctx.Sequence.AudioTracks.Find(t => !t.Locked && !t.Clips.Exists(c => c.Start < end && c.End > at))?.Id
                ?? TimelineEdits.AddTrack(ctx, TrackKind.Audio, ctx.Sequence.AudioTracks.Count, trackName);
            return TimelineEdits.InsertMedia(ctx, media.Id, Time.Zero, media.Duration, trackId, at, PlacementMode.Strict);
        }, _ids);
        if (!Execute(new CompositeCommand("Insertar voz", [new AddMediaCommand(media), place])))
        {
            return false;
        }

        Select([place.CreatedIds[0]]);
        return true;
    }

    /// <summary>
    /// Cambia el audio del clip por el WAV nuevo (desde su inicio, con su largo) en un paso; el medio anterior sale de la
    /// biblioteca si ningún otro clip lo usa. Si el audio nuevo es más largo y choca con el clip siguiente, no cambia nada.
    /// </summary>
    public bool ReplaceGeneratedVoice(string clipId, string wavPath, VoiceSettings settings, long frames, int sampleRate)
    {
        VerifyAccess();
        if (Sequence.FindClip(clipId) is not { } found)
        {
            return false;
        }

        string oldMedia = found.Clip.MediaId;
        MediaRef media = VoiceMedia(wavPath, settings, frames, sampleRate);
        var swap = new SequenceEditCommand("Regenerar voz", SequenceId, ctx =>
        {
            (Track track, Clip clip) = ctx.Sequence.GetClip(clipId);
            EditContext.EnsureUnlocked(track);
            clip.MediaId = media.Id;
            clip.SourceIn = Time.Zero;
            clip.SourceOut = media.Duration;
            if (track.Clips.Find(c => c != clip && c.Start < clip.End && c.End > clip.Start) is { } blocking)
            {
                throw new EditException($"La voz nueva es más larga y choca con «{ctx.Project.FindMedia(blocking.MediaId)?.Name ?? blocking.Id}». Deja espacio después del clip.");
            }

            ctx.EnsureValid();
        }, _ids);

        List<ICommand> commands = [new AddMediaCommand(media), swap];
        if (Project.Sequences.SelectMany(s => s.AllTracks).SelectMany(t => t.Clips).Count(c => c.MediaId == oldMedia) == 1)
        {
            commands.Add(new RemoveMediaCommand(oldMedia));
        }

        return Execute(new CompositeCommand("Regenerar voz", commands));
    }

    private MediaRef VoiceMedia(string wavPath, VoiceSettings settings, long frames, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrEmpty(wavPath);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        string text = string.Join(' ', settings.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return new MediaRef
        {
            Id = _ids.NewId("media"),
            Path = Path.GetFullPath(wavPath),
            Name = "Voz: " + (text.Length > 40 ? text[..40] + "…" : text),
            Duration = Time.FromSamples(frames, sampleRate),
            HasAudio = true,
            SampleRate = sampleRate,
            AudioChannels = 1,
        };
    }
}
