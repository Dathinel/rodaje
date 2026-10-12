using Rodaje.App.Services.Audio;
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

        stack.Execute(AudioPresetCommands.Apply(sequence.Id, sequence.AudioTracks[0].Id, AudioPresets.SoftMusic));
        Assert.Same(AudioPresets.SoftMusic, AudioPresetCommands.Match(sequence.AudioTracks[0].Effects, out _));

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

    private static (Project, Sequence, Clip) Build()
    {
        var media = new MediaRef { Id = "m", Path = @"C:\voz.wav", Duration = Time.FromSeconds(10), HasAudio = true, SampleRate = 48000, AudioChannels = 1 };
        var clip = new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromSeconds(5) };
        var sequence = new Sequence { Id = "s", AudioTracks = [new Track { Id = "a", Kind = TrackKind.Audio, Clips = [clip] }] };
        var project = new Project { Media = [media], Sequences = [sequence] };
        return (project, sequence, clip);
    }
}
