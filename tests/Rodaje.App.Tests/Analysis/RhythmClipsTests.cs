using Rodaje.App.Views.Analysis;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Analysis;

/// <summary>R1 del repaso del 19-nov: con todo seleccionado, «Ajustar al ritmo» dejó la música en un trozo de 0,3 s.</summary>
public sealed class RhythmClipsTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static Sequence Sample() => new()
    {
        Id = "s", Name = "Secuencia",
        VideoTracks = [new Track { Id = "v", Name = "V1", Kind = TrackKind.Video, Clips =
        [
            new Clip { Id = "toma1", MediaId = "m1", SourceOut = S(5), LinkId = "l1" },
            new Clip { Id = "toma2", MediaId = "m2", SourceOut = S(5), Start = S(5) },
        ] }],
        AudioTracks =
        [
            new Track { Id = "a1", Name = "A1", Kind = TrackKind.Audio, Clips = [new Clip { Id = "sonido1", MediaId = "m1", SourceOut = S(5), LinkId = "l1" }] },
            new Track { Id = "a2", Name = "A2", Kind = TrackKind.Audio, Clips =
            [
                new Clip { Id = "ritmo", MediaId = "mr", SourceOut = S(30) },
                new Clip { Id = "lecho", MediaId = "mm", SourceOut = S(30), Start = S(30) },
            ] },
        ],
    };

    [Fact]
    public void LooseAudioClipsAreNotMontageClipsButTakesAndTheirLinkedSoundAre()
    {
        Sequence sequence = Sample();
        Clip music = sequence.GetClip("ritmo").Clip;

        IReadOnlyList<string> clips = RhythmClips.Montage(sequence, sequence.AllTracks.SelectMany(t => t.Clips).Select(c => c.Id), music);

        Assert.Equal(["toma1", "toma2", "sonido1"], clips.Order());
    }

    [Fact]
    public void TheMusicAndItsLinkedGroupAreNeverIncluded()
    {
        Sequence sequence = Sample();
        sequence.GetClip("toma1").Clip.LinkId = "l1";
        Clip music = sequence.GetClip("sonido1").Clip;

        Assert.Equal(["toma2"], RhythmClips.Montage(sequence, ["toma1", "toma2", "sonido1", "no-existe"], music));
    }
}
