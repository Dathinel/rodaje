using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Model;

public class ProjectTests
{
    [Fact]
    public void NewProject_HasCurrentSchemaAndEmptyCollections()
    {
        var p = new Project();
        Assert.Equal(15, Project.CurrentSchemaVersion);
        Assert.Equal(Project.CurrentSchemaVersion, p.SchemaVersion);
        Assert.Equal("", p.Id);
        Assert.Equal("", p.Name);
        Assert.Empty(p.Media);
        Assert.Empty(p.Sequences);
    }

    [Fact]
    public void FindAndGetMedia()
    {
        Project p = ValidProject();
        Assert.Same(p.Media[1], p.FindMedia("m2"));
        Assert.Same(p.Media[0], p.GetMedia("m1"));
        Assert.Null(p.FindMedia("nada"));
        Assert.Null(p.FindMedia("M1")); // distingue mayúsculas
        var ex = Assert.Throws<KeyNotFoundException>(() => p.GetMedia("nada"));
        Assert.Equal("No existe el medio «nada» en el proyecto.", ex.Message);
    }

    [Fact]
    public void FindAndGetSequence()
    {
        Project p = ValidProject();
        Assert.Same(p.Sequences[0], p.FindSequence("s1"));
        Assert.Same(p.Sequences[0], p.GetSequence("s1"));
        Assert.Null(p.FindSequence("s2"));
        var ex = Assert.Throws<KeyNotFoundException>(() => p.GetSequence("s2"));
        Assert.Equal("No existe la secuencia «s2» en el proyecto.", ex.Message);
    }
}

public class SequenceTests
{
    [Fact]
    public void NewSequence_HasDefaults()
    {
        var s = new Sequence();
        Assert.Equal(Rational.Fps30, s.FrameRate);
        Assert.Equal(1920, s.Width);
        Assert.Equal(1080, s.Height);
        Assert.Equal(48000, s.SampleRate);
        Assert.Empty(s.VideoTracks);
        Assert.Empty(s.AudioTracks);
        Assert.Empty(s.AllTracks);
        Assert.Equal(Time.Zero, s.Duration);
    }

    [Fact]
    public void Duration_IsEndOfLatestClipInAnyTrack()
    {
        Sequence s = ValidProject().Sequences[0];
        Assert.Equal(Sec(40), s.Duration); // c5: 10 + 30

        s.AudioTracks[0].Clips.RemoveAt(1);
        Assert.Equal(Sec(17), s.Duration); // c2 en V1: 12 + 5

        foreach (Track t in s.AllTracks)
        {
            t.Clips.Clear();
        }

        Assert.Equal(Time.Zero, s.Duration);
    }

    [Fact]
    public void Duration_WithTracksButNoClips_IsZero()
    {
        var s = new Sequence { VideoTracks = [new Track()], AudioTracks = [new Track { Kind = TrackKind.Audio }] };
        Assert.Equal(Time.Zero, s.Duration);
    }

    [Fact]
    public void AllTracks_ListsVideoThenAudioInOrder()
    {
        Sequence s = ValidProject().Sequences[0];
        Assert.Equal(["v1", "v2", "a1"], s.AllTracks.Select(t => t.Id));
    }

    [Fact]
    public void FindAndGetTrack()
    {
        Sequence s = ValidProject().Sequences[0];
        Assert.Same(s.VideoTracks[1], s.FindTrack("v2"));
        Assert.Same(s.AudioTracks[0], s.GetTrack("a1"));
        Assert.Null(s.FindTrack("x"));
        var ex = Assert.Throws<KeyNotFoundException>(() => s.GetTrack("x"));
        Assert.Equal("No existe la pista «x» en la secuencia.", ex.Message);
    }

    [Fact]
    public void FindAndGetClip_ReturnTrackAndClip()
    {
        Sequence s = ValidProject().Sequences[0];

        (Track Track, Clip Clip)? found = s.FindClip("c5");
        Assert.NotNull(found);
        Assert.Same(s.AudioTracks[0], found.Value.Track);
        Assert.Same(s.AudioTracks[0].Clips[1], found.Value.Clip);

        (Track track, Clip clip) = s.GetClip("c3");
        Assert.Equal("v2", track.Id);
        Assert.Equal("c3", clip.Id);

        Assert.Null(s.FindClip("nada"));
        var ex = Assert.Throws<KeyNotFoundException>(() => s.GetClip("nada"));
        Assert.Equal("No existe el clip «nada» en la secuencia.", ex.Message);
    }

    [Fact]
    public void GetLinkGroup_PutsRequestedClipFirst()
    {
        Sequence s = ValidProject().Sequences[0];

        List<(Track Track, Clip Clip)> fromVideo = s.GetLinkGroup("c1");
        Assert.Equal(["c1", "c4"], fromVideo.Select(g => g.Clip.Id));
        Assert.Equal(["v1", "a1"], fromVideo.Select(g => g.Track.Id));

        List<(Track Track, Clip Clip)> fromAudio = s.GetLinkGroup("c4");
        Assert.Equal(["c4", "c1"], fromAudio.Select(g => g.Clip.Id));
        Assert.Equal(["a1", "v1"], fromAudio.Select(g => g.Track.Id));
    }

    [Fact]
    public void GetLinkGroup_WithoutLink_ReturnsOnlyTheClip()
    {
        Sequence s = ValidProject().Sequences[0];
        List<(Track Track, Clip Clip)> group = s.GetLinkGroup("c2");
        (Track track, Clip clip) = Assert.Single(group);
        Assert.Equal("c2", clip.Id);
        Assert.Equal("v1", track.Id);
    }

    [Fact]
    public void GetLinkGroup_WithLinkNotSharedByOthers_ReturnsOnlyTheClip()
    {
        Sequence s = ValidProject().Sequences[0];
        s.GetClip("c3").Clip.LinkId = "solo";
        Assert.Single(s.GetLinkGroup("c3"));
    }

    [Fact]
    public void GetLinkGroup_IncludesEveryTrackWithTheLink()
    {
        Sequence s = ValidProject().Sequences[0];
        s.GetClip("c3").Clip.LinkId = "L1";
        Assert.Equal(["c3", "c1", "c4"], s.GetLinkGroup("c3").Select(g => g.Clip.Id));
    }

    [Fact]
    public void GetLinkGroup_MissingClip_Throws()
    {
        Sequence s = ValidProject().Sequences[0];
        Assert.Throws<KeyNotFoundException>(() => s.GetLinkGroup("nada"));
    }
}
