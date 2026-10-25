using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Commands;

public class SequenceSnapshotTests
{
    [Fact]
    public void Restore_DeshaceCambiosDePropiedadesPistasYClips()
    {
        Project project = Create();
        Sequence seq = Seq(project);
        string before = ProjectDump.Of(project);
        Track v1 = seq.VideoTracks[0];
        Track v2 = seq.VideoTracks[1];
        Track a1 = seq.AudioTracks[0];
        var snapshot = SequenceSnapshot.Capture(seq);

        seq.Name = "Otra";
        seq.FrameRate = Rational.Fps25;
        seq.Width = 1280;
        seq.Height = 720;
        seq.SampleRate = 44100;
        v1.Clips[0].Start = S(1);
        v1.Clips[1].Effects[0].Parameters["opacity"].Keyframes.Add(new Keyframe(S(14), 0.5));
        v1.Name = "renombrada";
        v1.Muted = true;
        seq.VideoTracks.Reverse();
        seq.AudioTracks.Clear();
        seq.VideoTracks.Add(new Track { Id = "nueva", Kind = TrackKind.Video });

        Assert.False(snapshot.Matches(seq));
        snapshot.Restore(seq);

        Assert.Equal(before, ProjectDump.Of(project));
        Assert.True(snapshot.Matches(seq));
        Assert.Same(v1, seq.VideoTracks[0]);
        Assert.Same(v2, seq.VideoTracks[1]);
        Assert.Same(a1, seq.AudioTracks[0]);
        Assert.Equal(SequenceId, snapshot.SequenceId);
    }

    /// <summary>Tras una edición, la pista que no cambió comparte su copia con la instantánea anterior; las dos restauran bien.</summary>
    [Fact]
    public void Capture_ConLaAnterior_CompartePistasIgualesYRestauraCadaUna()
    {
        Project project = Create();
        Sequence seq = Seq(project);
        string start = ProjectDump.Of(project);
        var first = SequenceSnapshot.Capture(seq);
        seq.VideoTracks[0].Clips[0].Start = S(6);
        string moved = ProjectDump.Of(project);

        var second = SequenceSnapshot.Capture(seq, first);

        Assert.NotSame(first.VideoTracks[0].State, second.VideoTracks[0].State);
        Assert.Same(first.VideoTracks[1].State, second.VideoTracks[1].State);
        Assert.Same(first.AudioTracks[0].State, second.AudioTracks[0].State);
        first.Restore(seq);
        Assert.Equal(start, ProjectDump.Of(project));
        second.Restore(seq);
        Assert.Equal(moved, ProjectDump.Of(project));
        Assert.True(second.Matches(seq));

        // Lo restaurado son copias nuevas: cambiarlo no toca la copia compartida.
        seq.AudioTracks[0].Clips[0].Start = S(9);
        first.Restore(seq);
        Assert.Equal(start, ProjectDump.Of(project));
    }

    [Fact]
    public void Restore_ConservaLasListasYSePuedeRepetir()
    {
        Project project = Create();
        Sequence seq = Seq(project);
        List<Track> videoList = seq.VideoTracks;
        var snapshot = SequenceSnapshot.Capture(seq);

        snapshot.Restore(seq);
        seq.VideoTracks[0].Clips[0].Start = S(2);
        snapshot.Restore(seq);

        Assert.Same(videoList, seq.VideoTracks);
        Assert.Equal(S(0), seq.VideoTracks[0].Clips[0].Start);
    }

    [Fact]
    public void Capture_NoSeVeAfectadaPorCambiosPosteriores()
    {
        Project project = Create();
        Sequence seq = Seq(project);
        var snapshot = SequenceSnapshot.Capture(seq);
        Clip original = seq.VideoTracks[0].Clips[0];

        original.Start = S(4);
        snapshot.Restore(seq);

        Assert.NotSame(original, seq.VideoTracks[0].Clips[0]);
        Assert.Equal(S(0), seq.VideoTracks[0].Clips[0].Start);
    }

    [Fact]
    public void Restore_DevuelvePistaQuitadaComoElMismoObjeto()
    {
        Project project = Create();
        Sequence seq = Seq(project);
        Track a1 = seq.AudioTracks[0];
        var snapshot = SequenceSnapshot.Capture(seq);

        seq.AudioTracks.Remove(a1);
        a1.Clips.Clear();
        snapshot.Restore(seq);

        Assert.Same(a1, Assert.Single(seq.AudioTracks));
        Assert.Single(a1.Clips);
    }

    [Fact]
    public void Matches_DetectaCadaDiferencia()
    {
        var mutations = new List<Action<Sequence>>
        {
            s => s.Id = "x",
            s => s.Name = "x",
            s => s.FrameRate = Rational.Fps60,
            s => s.Width = 1,
            s => s.Height = 1,
            s => s.SampleRate = 1,
            s => s.VideoTracks.RemoveAt(1),
            s => s.VideoTracks[0] = s.VideoTracks[0].Clone(),
            s => s.AudioTracks[0].Hidden = true,
            s => s.AudioTracks[0].Clips[0].Enabled = false,
        };

        foreach (Action<Sequence> mutate in mutations)
        {
            Sequence seq = Seq(Create());
            var snapshot = SequenceSnapshot.Capture(seq);
            Assert.True(snapshot.Matches(seq));
            mutate(seq);
            Assert.False(snapshot.Matches(seq));
        }
    }

    [Fact]
    public void TrackStateEquals_ComparaMarcasYContenido()
    {
        Track a = Seq(Create()).VideoTracks[0];
        Assert.True(SequenceSnapshot.TrackStateEquals(a, a.Clone()));

        var flagMutations = new List<Action<Track>> { t => t.Locked = !t.Locked, t => t.Muted = true, t => t.Hidden = true };
        foreach (Action<Track> mutate in flagMutations)
        {
            Track b = a.Clone();
            mutate(b);
            Assert.False(SequenceSnapshot.TrackStateEquals(a, b));
            Assert.True(SequenceSnapshot.TrackContentEquals(a, b));
        }
    }

    [Fact]
    public void TrackContentEquals_DetectaCualquierCambioDeContenido()
    {
        Track a = Seq(Create()).VideoTracks[0];
        var mutations = new List<Action<Track>>
        {
            t => t.Id = "x",
            t => t.Kind = TrackKind.Audio,
            t => t.Name = "x",
            t => t.Clips.RemoveAt(0),
            t => t.Clips.Reverse(),
            t => t.Clips[0].Id = "x",
            t => t.Clips[0].MediaId = "x",
            t => t.Clips[0].SourceIn = S(1),
            t => t.Clips[0].SourceOut = S(4),
            t => t.Clips[0].Start = S(1),
            t => t.Clips[0].LinkId = null,
            t => t.Clips[0].Enabled = false,
            t => t.Clips[0].Effects.Add(new EffectInstance { Id = "fx-9" }),
            t => t.Clips[1].Effects[0].Id = "x",
            t => t.Clips[1].Effects[0].Type = "x",
            t => t.Clips[1].Effects[0].Enabled = false,
            t => t.Clips[1].Effects[0].Parameters.Remove("x"),
            t => t.Clips[1].Effects[0].Parameters["y"] = t.Clips[1].Effects[0].Parameters["x"],
            t => t.Clips[1].Effects[0].Parameters["x"].Value = 0.25,
            t => t.Clips[1].Effects[0].Parameters["opacity"].Keyframes.RemoveAt(1),
            t => t.Clips[1].Effects[0].Parameters["opacity"].Keyframes[1] = new Keyframe(S(12), 1, Interpolation.Hold),
        };

        foreach (Action<Track> mutate in mutations)
        {
            Track b = a.Clone();
            mutate(b);
            Assert.False(SequenceSnapshot.TrackContentEquals(a, b));
        }

        // Mismo número de parámetros pero con una clave distinta.
        Track c = a.Clone();
        Dictionary<string, Parameter> parameters = c.Clips[1].Effects[0].Parameters;
        parameters["z"] = parameters["x"];
        parameters.Remove("x");
        Assert.False(SequenceSnapshot.TrackContentEquals(a, c));
    }

    [Fact]
    public void Comparadores_RechazanNulos()
    {
        Track t = new();
        Clip c = new();
        EffectInstance e = new();
        Parameter p = new();
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.Capture(null!));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.Capture(new Sequence()).Restore(null!));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.Capture(new Sequence()).Matches(null!));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.TrackStateEquals(null!, t));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.TrackStateEquals(t, null!));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.TrackContentEquals(null!, t));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.TrackContentEquals(t, null!));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.ClipStateEquals(null!, c));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.ClipStateEquals(c, null!));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.EffectStateEquals(null!, e));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.EffectStateEquals(e, null!));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.ParameterStateEquals(null!, p));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.ParameterStateEquals(p, null!));
    }

    [Fact]
    public void Matches_ComparesMarkersAndRegions()
    {
        Project project = Create();
        Sequence seq = Seq(project);
        seq.Markers.Add(new Marker { Id = "m-1", Time = S(1), Name = "uno" });
        seq.Regions.Add(new Region { Id = "r-1", Start = S(0), End = S(2), Name = "intro" });
        var snapshot = SequenceSnapshot.Capture(seq);
        Assert.True(snapshot.Matches(seq));

        seq.Markers[0].Name = "otro";
        Assert.False(snapshot.Matches(seq));
        snapshot.Restore(seq);
        seq.Regions[0].End = S(3);
        Assert.False(snapshot.Matches(seq));

        Marker marker = seq.Markers[0];
        Region region = seq.Regions[0];
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.MarkerEquals(null!, marker));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.MarkerEquals(marker, null!));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.RegionEquals(null!, region));
        Assert.Throws<ArgumentNullException>(() => SequenceSnapshot.RegionEquals(region, null!));
    }

    [Fact]
    public void SmallPieces_BehaveAsDocumented()
    {
        Project project = Create();
        Sequence seq = Seq(project);
        seq.Markers.Add(new Marker { Id = "m-1", Time = S(1) });
        Rodaje.Core.Editing.EditContext.ShiftMarkers(seq, Time.Zero, Time.Zero);
        Assert.Equal(S(1), seq.Markers[0].Time);

        Assert.Null(new Rodaje.Core.Editing.EditException().InnerException);
        var inner = new InvalidOperationException("x");
        Assert.Same(inner, new Rodaje.Core.Editing.EditException("y", inner).InnerException);

        var stack = new UndoStack(project);
        Assert.Throws<Rodaje.Core.Editing.EditException>(() => stack.Execute(Edits.SetTrackState(SequenceId, "track-v2", name: "Otro nombre")));
    }
}
