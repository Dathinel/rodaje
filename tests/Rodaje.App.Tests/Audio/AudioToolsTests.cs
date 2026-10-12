using Rodaje.App.Services;
using Rodaje.App.Services.Audio;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.App.Tests.Audio;

public sealed class AudioToolsTests
{
    [Fact]
    public void A_preset_is_one_step_and_is_recognized_until_touched()
    {
        (Project project, Sequence sequence, Clip clip) = Build();
        var stack = new UndoStack(project);

        stack.Execute(AudioPresetCommands.Apply(sequence.Id, clip.Id, AudioPresets.Podcast));
        Assert.Same(AudioPresets.Podcast, AudioPresetCommands.Match(clip.Effects, out bool has));
        Assert.True(has);

        stack.Execute(AudioPresetCommands.Apply(sequence.Id, sequence.AudioTracks[0].Id, AudioPresets.MusicGlue));
        Assert.Same(AudioPresets.MusicGlue, AudioPresetCommands.Match(sequence.AudioTracks[0].Effects, out _));

        // Un preajuste sin ecualizador deja el que hay; la mezcla (la secuencia) también admite preajustes.
        stack.Execute(AudioPresetCommands.Apply(sequence.Id, clip.Id, AudioPresets.MixLimiter));
        Assert.Contains(clip.Effects, e => e.Type == BuiltInEffects.Equalizer);
        Assert.Same(AudioPresets.MixLimiter, AudioPresetCommands.Match(clip.Effects, out _));
        stack.Execute(AudioPresetCommands.Apply(sequence.Id, sequence.Id, AudioPresets.MixLimiter));
        Assert.Same(AudioPresets.MixLimiter, AudioPresetCommands.Match(sequence.MasterEffects, out _));
        Assert.True(stack.Undo());
        Assert.Empty(sequence.MasterEffects);
        Assert.True(stack.Undo());
        clip = sequence.GetClip(clip.Id).Clip;

        clip.Effects.Find(e => e.Type == BuiltInEffects.Compressor)!.Parameters[BuiltInEffects.CompressorParameters.Ratio].Value = 7;
        Assert.Null(AudioPresetCommands.Match(clip.Effects, out has));
        Assert.True(has);

        stack.Execute(AudioPresetCommands.Apply(sequence.Id, clip.Id, null));
        Assert.Null(AudioPresetCommands.Match(clip.Effects, out has));
        Assert.False(has);
        Assert.True(stack.Undo());
        Assert.True(stack.Undo());
        Assert.True(stack.Undo());
        Assert.Empty(clip.Effects);
        Assert.Empty(sequence.AudioTracks[0].Effects);
    }

    [Theory]
    [InlineData("-18", -18.0)]
    [InlineData("−23,5", -23.5)]
    [InlineData(" -14 LUFS ", -14.0)]
    [InlineData("5", null)]
    [InlineData("abc", null)]
    public void Custom_targets_are_parsed_in_spanish(string text, double? expected) =>
        Assert.Equal(expected, LoudnessNormalizer.ParseTarget(text));

    [Fact]
    public void A_voice_over_take_goes_to_its_track_if_free_or_to_a_new_one_in_one_step() => UiThread.Run(async () =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        string trackId = session.Sequence.AudioTracks[0].Id;
        int tracks = session.Sequence.AudioTracks.Count;
        int steps = session.History.Count;

        Assert.True(session.InsertVoiceOver(@"C:\medios\voz-en-off-1.wav", 96_000, 48_000, Time.FromSeconds(3), trackId));
        Clip first = session.Sequence.GetClip(session.SelectedClipIds[0]).Clip;
        Assert.Equal(tracks + 1, session.Sequence.AudioTracks.Count);
        Assert.Equal(EditorSession.VoiceOverTrackName, session.Sequence.AudioTracks[^1].Name);
        Assert.Equal((Time.FromSeconds(3), Time.FromSeconds(5)), (first.Start, first.End));

        Assert.True(session.InsertVoiceOver(@"C:\medios\voz-en-off-2.wav", 48_000, 48_000, Time.FromSeconds(12), trackId));
        Assert.Equal(trackId, session.Sequence.GetClip(session.SelectedClipIds[0]).Track.Id);
        Assert.Equal(steps + 2, session.History.Count);
        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.Equal(tracks, session.Sequence.AudioTracks.Count);
    });

    private static (Project, Sequence, Clip) Build()
    {
        var media = new MediaRef { Id = "m", Path = @"C:\voz.wav", Duration = Time.FromSeconds(10), HasAudio = true, SampleRate = 48000, AudioChannels = 1 };
        var clip = new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromSeconds(5) };
        var sequence = new Sequence { Id = "s", AudioTracks = [new Track { Id = "a", Kind = TrackKind.Audio, Clips = [clip] }] };
        var project = new Project { Media = [media], Sequences = [sequence] };
        return (project, sequence, clip);
    }
}
