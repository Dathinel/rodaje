using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Commands;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Editing;

/// <summary>Los keyframes de los efectos de pista siguen al contenido que el ripple o el reordenar corren.</summary>
public class TrackKeyframesTests
{
    private const string Gain = BuiltInEffects.AudioParameters.GainDb;

    private static Track WithVolume(params (double Seconds, double Value)[] keys) => new()
    {
        Id = "t",
        Kind = TrackKind.Audio,
        Effects =
        [
            new EffectInstance
            {
                Id = "vol",
                Type = BuiltInEffects.Audio,
                Parameters = { [Gain] = new Parameter { Keyframes = [.. keys.Select(k => new Keyframe(S(k.Seconds), k.Value))] } },
            },
        ],
    };

    private static Parameter Curve(Track track) => track.Effects[0].Parameters[Gain];

    private static (double Seconds, double Value)[] Keys(Track track) =>
        [.. Curve(track).Keyframes.Select(k => (k.Time.ToSeconds(), k.Value))];

    [Fact]
    public void ClosingASpan_DropsItsKeyframes_AndKeepsTheCurveOnBothSides()
    {
        // Rampa de 0 a −10 dB entre 0 s y 10 s; se borra [4, 6).
        Track track = WithVolume((0, 0), (5, -5), (10, -10));
        Parameter before = Curve(track).Clone();

        TrackKeyframes.Shift(track, S(6), S(-2));

        Parameter after = Curve(track);
        Assert.DoesNotContain(after.Keyframes, k => k.Value == -5);
        foreach (double t in new[] { 0, 1, 3.5, 3.99 })
        {
            Assert.Equal(before.Evaluate(S(t)), after.Evaluate(S(t)), 9);
        }

        foreach (double t in new[] { 6, 7, 9, 12 })
        {
            Assert.Equal(before.Evaluate(S(t)), after.Evaluate(S(t - 2)), 9);
        }
    }

    [Fact]
    public void OpeningSpace_MovesWhatComesAfter()
    {
        Track track = WithVolume((0, 0), (10, -10));
        Parameter before = Curve(track).Clone();

        TrackKeyframes.Shift(track, S(4), S(3));

        Parameter after = Curve(track);
        Assert.Equal(before.Evaluate(S(2)), after.Evaluate(S(2)), 9);
        Assert.Equal(before.Evaluate(S(4)), after.Evaluate(S(7)), 9);
        Assert.Equal(before.Evaluate(S(8)), after.Evaluate(S(11)), 9);
        Assert.Equal(S(13), after.Keyframes[^1].Time);
    }

    [Fact]
    public void FlatStretches_GetNoExtraKeyframes()
    {
        Track track = WithVolume((0, -6), (2, -6), (20, -3));

        TrackKeyframes.Shift(track, S(1), S(-0.5));

        Assert.Equal([(0, -6), (1.5, -6), (19.5, -3)], Keys(track));
    }

    [Fact]
    public void NoKeyframes_OrNoShift_ChangeNothing()
    {
        var track = new Track { Id = "t", Kind = TrackKind.Audio, Effects = [new EffectInstance { Id = "v", Type = BuiltInEffects.Audio }] };
        TrackKeyframes.Shift(track, S(1), S(1));
        TrackKeyframes.Move(track, [new TrackKeyframes.Piece(S(0), S(1), S(1))]);
        Assert.Empty(track.Effects[0].Parameters);

        Track animated = WithVolume((0, 0), (1, -1));
        TrackKeyframes.Shift(animated, S(0.5), Time.Zero);
        Assert.Equal([(0, 0), (1, -1)], Keys(animated));

        Assert.Throws<ArgumentNullException>(() => TrackKeyframes.Shift(null!, S(0), S(1)));
        Assert.Throws<ArgumentNullException>(() => TrackKeyframes.Move(null!, []));
        Assert.Throws<ArgumentNullException>(() => TrackKeyframes.Move(track, null!));
    }

    [Fact]
    public void DeletingEverythingAnimated_LeavesTheValueThatFollows()
    {
        Track track = WithVolume((2, -1), (3, -2));

        TrackKeyframes.Shift(track, S(4), S(-4));

        Assert.Equal([(0, -2)], Keys(track));
    }

    [Fact]
    public void RippleDelete_ShiftsTheTrackVolume_AndUndoRestoresIt()
    {
        Project project = Create();
        Track a1 = Seq(project).AudioTracks[0];
        a1.Clips.Add(new Clip { Id = "clip-9", MediaId = "media-1", SourceIn = S(20), SourceOut = S(24), Start = S(8) });
        a1.Effects.Add(WithVolume((8, 0), (12, -12)).Effects[0]);
        var stack = new UndoStack(project);
        string before = ProjectSerializer.Serialize(project);
        Parameter old = Curve(a1).Clone();

        stack.Execute(Edits.CloseGap(SequenceId, a1.Id, S(6), RippleScope.Track));

        Assert.Equal(S(5), a1.Clips[^1].Start);
        Assert.Equal(old.Evaluate(S(10)), Curve(a1).Evaluate(S(7)), 9);
        Assert.Equal([(5, 0), (9, -12)], Keys(a1));
        stack.Undo();
        Assert.Equal(before, ProjectSerializer.Serialize(project));
    }

    [Fact]
    public void Reordering_CarriesEachClipsStretchOfTheCurve()
    {
        Project project = Create();
        Sequence seq = Seq(project);
        Track a1 = seq.AudioTracks[0];
        a1.Clips[0].LinkId = null;
        seq.VideoTracks[0].Clips[0].LinkId = null;
        a1.Clips.Add(new Clip { Id = "clip-9", MediaId = "media-1", SourceIn = S(20), SourceOut = S(22), Start = S(5) });
        a1.Effects.Add(WithVolume((0, 0), (5, -5), (7, -20)).Effects[0]);
        Parameter old = Curve(a1).Clone();
        var stack = new UndoStack(project);

        stack.Execute(Edits.ReorderOnMainTrack(SequenceId, "clip-9", 0));

        // «clip-9» estaba en [5, 7) y ahora en [0, 2); «clip-4» pasó de [0, 5) a [2, 7).
        Assert.Equal(old.Evaluate(S(5)), Curve(a1).Evaluate(S(0)), 9);
        Assert.Equal(old.Evaluate(S(6)), Curve(a1).Evaluate(S(1)), 9);
        Assert.Equal(old.Evaluate(S(0)), Curve(a1).Evaluate(S(2)), 9);
        Assert.Equal(old.Evaluate(S(2.5)), Curve(a1).Evaluate(S(4.5)), 9);
        Assert.Empty(ModelValidator.Validate(project));
    }

    [Fact]
    public void Reordering_MovesTheLinkedTracksCurveToo()
    {
        Project project = Create();
        Sequence seq = Seq(project);
        Track v1 = seq.VideoTracks[0];
        v1.Clips.Insert(1, NewClip("clip-5", 5, 2));
        Track a1 = seq.AudioTracks[0];
        a1.Effects.Add(WithVolume((0, 0), (5, -10)).Effects[0]);
        Parameter old = Curve(a1).Clone();

        new UndoStack(project).Execute(Edits.ReorderOnMainTrack(SequenceId, "clip-1", 1));

        // «clip-1» va de [0, 5) a [2, 7) y su audio enlazado «clip-4» con él.
        Assert.Equal(S(2), a1.Clips[0].Start);
        Assert.Equal(old.Evaluate(S(1)), Curve(a1).Evaluate(S(3)), 9);
        Assert.Equal(old.Evaluate(S(4)), Curve(a1).Evaluate(S(6)), 9);
    }
}
