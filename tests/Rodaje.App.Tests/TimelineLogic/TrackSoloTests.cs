using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>El solo de la cabecera de pista: qué pistas callan y cómo se velan sus clips, con la misma regla que el motor.</summary>
public sealed class TrackSoloTests
{
    private static Sequence TwoAudioTracks(out Track voice, out Track music)
    {
        voice = new Track { Id = "voz", Kind = TrackKind.Audio, Name = "Voz" };
        music = new Track { Id = "musica", Kind = TrackKind.Audio, Name = "Música" };
        voice.Clips.Add(new Clip { Id = "v1", MediaId = "m", SourceOut = Time.FromSeconds(2) });
        music.Clips.Add(new Clip { Id = "m1", MediaId = "m", SourceOut = Time.FromSeconds(2) });
        var video = new Track { Id = "video", Kind = TrackKind.Video, Name = "Video 1" };
        var sequence = new Sequence { Id = "s", Width = 1920, Height = 1080 };
        sequence.VideoTracks.Add(video);
        sequence.AudioTracks.Add(voice);
        sequence.AudioTracks.Add(music);
        return sequence;
    }

    [Fact]
    public void SinSolos_NadieCallaYNoHayVelo()
    {
        Sequence sequence = TwoAudioTracks(out Track voice, out Track music);

        Assert.False(ClipDecorations.IsSoloing(sequence));
        Assert.False(ClipDecorations.SilencedBySolo(sequence, voice));
        Assert.False(ClipDecorations.SilencedBySolo(sequence, music));
        Assert.Equal(0, ClipDecorations.Veil(music.Clips[0], music, ClipDecorations.SilencedBySolo(sequence, music)));
    }

    [Fact]
    public void ConUnSolo_CallanLasDemasPistasDeAudioYSusClipsSeVelanComoSilenciados()
    {
        Sequence sequence = TwoAudioTracks(out Track voice, out Track music);
        voice.Solo = true;

        Assert.True(ClipDecorations.IsSoloing(sequence));
        Assert.False(ClipDecorations.SilencedBySolo(sequence, voice));
        Assert.True(ClipDecorations.SilencedBySolo(sequence, music));
        Assert.False(ClipDecorations.SilencedBySolo(sequence, sequence.VideoTracks[0]));
        Assert.Equal(ClipDecorations.MutedVeil, ClipDecorations.Veil(music.Clips[0], music, silencedBySolo: true));
        Assert.Equal(0, ClipDecorations.Veil(voice.Clips[0], voice, silencedBySolo: false));
    }

    [Fact]
    public void UnaPistaQueSaleAUnBusConSolo_SigueSonando()
    {
        Sequence sequence = TwoAudioTracks(out Track voice, out Track music);
        sequence.AudioBuses.Add(new AudioBus { Id = "bus", Name = "Música", Solo = true });
        music.Output = "bus";

        Assert.True(ClipDecorations.IsSoloing(sequence));
        Assert.False(ClipDecorations.SilencedBySolo(sequence, music));
        Assert.True(ClipDecorations.SilencedBySolo(sequence, voice));
    }

    [Fact]
    public void ElSoloDeLaCabecera_SePoneYSeQuitaConUnComando_YElExclusivoDejaUnaSola()
    {
        Sequence sequence = TwoAudioTracks(out Track voice, out Track music);
        var project = new Project { Id = "p", Name = "Solo", Sequences = [sequence] };
        var stack = new UndoStack(project);

        stack.Execute(Edits.SetTrackMix(sequence.Id, voice.Id, solo: !voice.Solo));
        Assert.True(voice.Solo);
        stack.Execute(Edits.SoloExclusive(sequence.Id, music.Id));
        Assert.False(voice.Solo);
        Assert.True(music.Solo);

        // Ctrl+clic sobre la única con solo: se quitan todos.
        stack.Execute(Edits.SoloExclusive(sequence.Id, music.Id));
        Assert.False(ClipDecorations.IsSoloing(sequence));

        stack.Undo();
        Assert.True(music.Solo);
    }
}
