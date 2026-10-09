using FotoToma.Core.Model;
using FotoToma.Core.Timing;
using static FotoToma.Core.Tests.Model.ModelSamples;

namespace FotoToma.Core.Tests.Model;

public class TrackTests
{
    private static Track SampleTrack() => new()
    {
        Id = "v1",
        Kind = TrackKind.Video,
        Name = "V1",
        Muted = true,
        Locked = true,
        Hidden = true,
        Clips = [MakeClip("a", "m", 0, 0, 2), MakeClip("b", "m", 5, 10, 13)],
    };

    [Fact]
    public void NewTrack_HasDefaults()
    {
        var t = new Track();
        Assert.Equal("", t.Id);
        Assert.Equal(TrackKind.Video, t.Kind);
        Assert.Equal("", t.Name);
        Assert.False(t.Muted);
        Assert.False(t.Locked);
        Assert.False(t.Hidden);
        Assert.Empty(t.Clips);
    }

    [Fact]
    public void End_IsEndOfLastClipOrZero()
    {
        Assert.Equal(Time.Zero, new Track().End);
        Assert.Equal(Sec(8), SampleTrack().End);
    }

    [Fact]
    public void ClipAt_StartIsInclusiveAndEndExclusive()
    {
        Track t = SampleTrack();
        Assert.Equal("a", t.ClipAt(Time.Zero)?.Id);
        Assert.Equal("a", t.ClipAt(Sec(2) - Time.FromTicks(1))?.Id);
        Assert.Null(t.ClipAt(Sec(2)));          // final de «a», hueco
        Assert.Null(t.ClipAt(Sec(4.999)));      // hueco
        Assert.Equal("b", t.ClipAt(Sec(5))?.Id);
        Assert.Equal("b", t.ClipAt(Sec(7.5))?.Id);
        Assert.Null(t.ClipAt(Sec(8)));          // final de la pista
        Assert.Null(t.ClipAt(Time.FromTicks(-1)));
        Assert.Null(new Track().ClipAt(Time.Zero));
    }

    [Fact]
    public void ClipAt_AdjacentClips_ReturnsTheOneStartingThere()
    {
        var t = new Track { Clips = [MakeClip("a", "m", 0, 0, 2), MakeClip("b", "m", 2, 0, 2)] };
        Assert.Equal("b", t.ClipAt(Sec(2))?.Id);
    }

    [Fact]
    public void SortClips_OrdersByStart()
    {
        var t = new Track
        {
            Clips = [MakeClip("c", "m", 10, 0, 1), MakeClip("a", "m", 0, 0, 1), MakeClip("b", "m", 5, 0, 1)],
        };
        t.SortClips();
        Assert.Equal(["a", "b", "c"], t.Clips.Select(c => c.Id));
    }

    [Fact]
    public void Clone_CopiesEverythingDeeply()
    {
        Project p = ValidProject();
        Track original = p.Sequences[0].VideoTracks[0];
        original.Muted = true;
        Track copy = original.Clone();

        Assert.NotSame(original, copy);
        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(original.Kind, copy.Kind);
        Assert.Equal(original.Name, copy.Name);
        Assert.Equal(original.Muted, copy.Muted);
        Assert.NotSame(original.Clips, copy.Clips);
        Assert.Equal(original.Clips.Select(c => c.Id), copy.Clips.Select(c => c.Id));
        Assert.All(original.Clips.Zip(copy.Clips), pair => Assert.NotSame(pair.First, pair.Second));

        // Cambiar la copia a todos los niveles no toca el original
        copy.Name = "otra";
        copy.Clips[0].Start = Sec(99);
        copy.Clips[0].Effects[0].Type = "otro";
        copy.Clips[0].Effects[0].Parameters["opacity"].Value = 0.25;
        copy.Clips[0].Effects[0].Parameters["opacity"].Keyframes.Add(new Keyframe(Sec(5), 7));
        copy.Clips[0].Effects[0].Parameters["nuevo"] = new Parameter();
        copy.Clips[0].Effects.Add(new EffectInstance { Id = "fx2" });
        copy.Clips.Add(MakeClip("x", "m1", 50, 0, 1));

        Assert.Equal("V1", original.Name);
        Assert.Equal(Time.Zero, original.Clips[0].Start);
        Assert.Equal(2, original.Clips.Count);
        EffectInstance fx = Assert.Single(original.Clips[0].Effects);
        Assert.Equal("fototoma.transform", fx.Type);
        Assert.Equal(2, fx.Parameters.Count);
        Assert.Equal(1, fx.Parameters["opacity"].Value);
        Assert.Equal(2, fx.Parameters["opacity"].Keyframes.Count);
        Assert.Empty(ModelValidator.Validate(p));
    }

    [Fact]
    public void CopyStateFrom_KeepsIdentityAndCopiesDeeply()
    {
        var target = new Track { Id = "t", Kind = TrackKind.Audio, Name = "antes", Clips = [MakeClip("z", "m", 0, 0, 1)] };
        List<Clip> oldClips = target.Clips;
        Track source = SampleTrack();

        target.CopyStateFrom(source);

        Assert.Equal("v1", target.Id);
        Assert.Equal(TrackKind.Video, target.Kind);
        Assert.Equal("V1", target.Name);
        Assert.True(target.Muted);
        Assert.True(target.Locked);
        Assert.True(target.Hidden);
        Assert.Equal(["a", "b"], target.Clips.Select(c => c.Id));
        Assert.NotSame(source.Clips, target.Clips);
        Assert.NotSame(oldClips, target.Clips);
        Assert.NotSame(source.Clips[0], target.Clips[0]);

        target.Clips[0].Start = Sec(3);
        Assert.Equal(Time.Zero, source.Clips[0].Start);
    }

    [Fact]
    public void CopyStateFrom_CopiesFalseFlags()
    {
        Track target = SampleTrack();
        target.CopyStateFrom(new Track { Id = "n" });
        Assert.False(target.Muted);
        Assert.False(target.Locked);
        Assert.False(target.Hidden);
        Assert.Empty(target.Clips);
    }
}

public class ClipTests
{
    [Fact]
    public void NewClip_HasDefaults()
    {
        var c = new Clip();
        Assert.Equal("", c.Id);
        Assert.Equal("", c.MediaId);
        Assert.Null(c.LinkId);
        Assert.True(c.Enabled);
        Assert.Empty(c.Effects);
        Assert.Equal(Time.Zero, c.Duration);
        Assert.Equal(Time.Zero, c.End);
    }

    [Fact]
    public void DurationAndEnd()
    {
        Clip c = MakeClip("c", "m", 12, 20, 25);
        Assert.Equal(Sec(5), c.Duration);
        Assert.Equal(Sec(17), c.End);
    }

    [Fact]
    public void ToSourceTime_MapsTimelineToSource()
    {
        Clip c = MakeClip("c", "m", 12, 20, 25);
        Assert.Equal(Sec(20), c.ToSourceTime(Sec(12)));
        Assert.Equal(Sec(22.5), c.ToSourceTime(Sec(14.5)));
        Assert.Equal(Sec(25), c.ToSourceTime(Sec(17)));
        // Fuera del clip la conversión sigue siendo lineal
        Assert.Equal(Sec(18), c.ToSourceTime(Sec(10)));
    }

    [Fact]
    public void Clone_IsDeep()
    {
        Clip original = ValidProject().Sequences[0].VideoTracks[0].Clips[0];
        original.Enabled = false;
        Clip copy = original.Clone();

        Assert.NotSame(original, copy);
        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(original.MediaId, copy.MediaId);
        Assert.Equal(original.SourceIn, copy.SourceIn);
        Assert.Equal(original.SourceOut, copy.SourceOut);
        Assert.Equal(original.Start, copy.Start);
        Assert.Equal(original.LinkId, copy.LinkId);
        Assert.False(copy.Enabled);
        Assert.NotSame(original.Effects, copy.Effects);
        Assert.NotSame(original.Effects[0], copy.Effects[0]);
        Assert.NotSame(original.Effects[0].Parameters, copy.Effects[0].Parameters);
        Assert.NotSame(original.Effects[0].Parameters["opacity"], copy.Effects[0].Parameters["opacity"]);
        Assert.NotSame(original.Effects[0].Parameters["opacity"].Keyframes, copy.Effects[0].Parameters["opacity"].Keyframes);
        Assert.Equal(original.Effects[0].Parameters["opacity"].Keyframes, copy.Effects[0].Parameters["opacity"].Keyframes);

        copy.SourceOut = Sec(1);
        copy.LinkId = null;
        copy.Effects[0].Enabled = false;
        copy.Effects[0].Parameters["opacity"].Keyframes.Clear();
        Assert.Equal(Sec(10), original.SourceOut);
        Assert.Equal("L1", original.LinkId);
        Assert.True(original.Effects[0].Enabled);
        Assert.Equal(2, original.Effects[0].Parameters["opacity"].Keyframes.Count);
    }
}

public class MediaRefTests
{
    [Fact]
    public void NewMediaRef_HasDefaults()
    {
        var m = new MediaRef();
        Assert.Equal("", m.Id);
        Assert.Equal("", m.Path);
        Assert.Equal("", m.Name);
        Assert.Equal(Time.Zero, m.Duration);
        Assert.False(m.HasVideo);
        Assert.False(m.HasAudio);
        Assert.Null(m.FrameRate);
        Assert.Equal(0, m.Width);
        Assert.Equal(0, m.Height);
        Assert.Equal(0, m.SampleRate);
        Assert.Equal(0, m.AudioChannels);
    }

    [Fact]
    public void ResolvePath_RelativeWithSpacesAndAccents()
    {
        string projectDir = Path.Combine(Path.GetTempPath(), "Programas de uso libre", "Proyecto ñandú");
        var m = new MediaRef { Path = Path.Combine("Medios de canción", "toma 1 — día.mp4") };

        string resolved = m.ResolvePath(projectDir);

        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.Equal(
            Path.Combine(Path.GetFullPath(projectDir), "Medios de canción", "toma 1 — día.mp4"),
            resolved);
    }

    [Fact]
    public void ResolvePath_NormalizesParentSegments()
    {
        string projectDir = Path.Combine(Path.GetTempPath(), "Proyectos", "Vídeo uno");
        var m = new MediaRef { Path = Path.Combine("..", "Compartido", "a b.wav") };
        Assert.Equal(Path.Combine(Path.GetTempPath(), "Proyectos", "Compartido", "a b.wav"), m.ResolvePath(projectDir));
    }

    [Fact]
    public void ResolvePath_AbsolutePathIsReturnedAsIs()
    {
        string absolute = Path.Combine(Path.GetTempPath(), "Otra carpeta", "clip ó.mov");
        var m = new MediaRef { Path = absolute };
        Assert.Equal(absolute, m.ResolvePath(Path.Combine(Path.GetTempPath(), "Proyecto")));
    }

    [Fact]
    public void ResolvePath_RootedWithoutDrive_UsesProjectDrive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // «\Videos\a.mp4» no es absoluta en Windows: depende de la unidad. Se toma la del proyecto.
        var m = new MediaRef { Path = @"\Videos\a b.mp4" };
        Assert.Equal(@"Q:\Videos\a b.mp4", m.ResolvePath(@"Q:\Proyectos\Uno"));

        // «Q:foo» tampoco es absoluta: relativa al directorio actual de la unidad Q
        var driveRelative = new MediaRef { Path = @"Q:clip.mp4" };
        Assert.Equal(@"Q:\Proyectos\Uno\clip.mp4", driveRelative.ResolvePath(@"Q:\Proyectos\Uno"));
    }

    [Fact]
    public void Clone_CopiesAllFields()
    {
        var m = new MediaRef
        {
            Id = "m", Path = "a.mp4", Name = "a", Duration = Sec(3), HasVideo = true, HasAudio = true,
            FrameRate = Rational.Ntsc30, Width = 640, Height = 480, SampleRate = 48000, AudioChannels = 6,
        };
        MediaRef copy = m.Clone();

        Assert.NotSame(m, copy);
        Assert.Equal(
            (m.Id, m.Path, m.Name, m.Duration, m.HasVideo, m.HasAudio, m.FrameRate, m.Width, m.Height, m.SampleRate, m.AudioChannels),
            (copy.Id, copy.Path, copy.Name, copy.Duration, copy.HasVideo, copy.HasAudio, copy.FrameRate, copy.Width, copy.Height, copy.SampleRate, copy.AudioChannels));

        copy.Path = "b.mp4";
        copy.Duration = Sec(1);
        Assert.Equal("a.mp4", m.Path);
        Assert.Equal(Sec(3), m.Duration);
    }
}
