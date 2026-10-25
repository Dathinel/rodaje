using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Inspector;

public sealed class KeyframeGraphGeometryTests
{
    private static Time S(double value) => Time.FromSeconds(value);

    private static Clip Clip() => new() { Id = "clip", MediaId = "media", Start = S(20), SourceIn = S(5), SourceOut = S(15) };

    private static KeyframeGraphGeometry Geometry(Clip clip) => new(clip, 100, 2000, 1020, 220, -10, 10);

    [Theory]
    [InlineData(5, 10, -10, 210)]
    [InlineData(10, 510, 0, 110)]
    [InlineData(15, 1010, 10, 10)]
    public void PixelAndModelRoundTripOnTrimmedClip(double time, double x, double value, double y)
    {
        KeyframeGraphGeometry geometry = Geometry(Clip());
        Assert.Equal(new GraphPoint(x, y), geometry.Pixel(S(time), value));
        Assert.Equal(S(time), geometry.TimeAt(x));
        Assert.Equal(value, geometry.ValueAt(y), 8);
    }

    [Fact]
    public void FrameSnapUsesSequenceTimeAndClampsToClip()
    {
        KeyframeGraphGeometry geometry = Geometry(Clip());
        Time time = geometry.TimeAt(123.3, Rational.Ntsc30);
        Time timeline = EffectCommands.TimelineOfKeyframe(geometry.Clip, time);
        Assert.Equal(timeline.SnapToFrame(Rational.Ntsc30), timeline);
        Assert.Equal(S(5), geometry.TimeAt(-10000));
        Assert.Equal(S(15), geometry.TimeAt(10000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MappingUsesExistingRetimeInBothDirections(bool reverse)
    {
        Clip clip = Clip();
        clip.Speed = new ClipSpeed { Rate = new Rational(2, 1), Reverse = reverse };
        clip.Duration = S(5);
        KeyframeGraphGeometry geometry = Geometry(clip);
        Time source = clip.ToEffectTime(S(22));
        GraphPoint pixel = geometry.Pixel(source, 0);
        Assert.Equal(210, pixel.Seconds, 5);
        Assert.Equal(source, geometry.TimeAt(pixel.Seconds));
    }

    [Fact]
    public void SingleKeyAndFlatRangeRemainDrawable()
    {
        var parameter = new Parameter { Keyframes = [new Keyframe(S(6), 42)] };
        (double low, double high) = KeyframeGraphGeometry.Fit(parameter, []);
        Assert.True(double.IsFinite(low) && double.IsFinite(high));
        Assert.True(low < 42 && high > 42);
        KeyframeGraphGeometry geometry = Geometry(Clip()) with { Low = low, High = high };
        Assert.Equal(0, geometry.Hit(parameter.Keyframes, new HashSet<Time>(), geometry.Pixel(S(6), 42), 8)?.Key);
    }

    [Fact]
    public void HitTestsBezierHandlesAndKeysAndMissesOutsideRadius()
    {
        Keyframe[] keys = [new(S(5), -5, Interpolation.Bezier) { Ease = BezierEase.Smooth }, new(S(10), 5)];
        KeyframeGraphGeometry geometry = Geometry(Clip());
        (GraphPoint outgoing, GraphPoint incoming) = KeyframeGraph.Handles(keys[0], keys[1], keys[0].Time);
        HashSet<Time> first = [keys[0].Time];
        HashSet<Time> none = [];
        Assert.Equal((0, 0), geometry.Hit(keys, first, geometry.HandlePixel(outgoing, keys[0].Time), 8));
        Assert.Equal((0, 1), geometry.Hit(keys, first, geometry.HandlePixel(incoming, keys[0].Time), 8));
        Assert.Equal((1, -1), geometry.Hit(keys, none, geometry.Pixel(keys[1].Time, keys[1].Value), 8));
        Assert.Null(geometry.Hit(keys, none, geometry.HandlePixel(outgoing, keys[0].Time), 8));
        Assert.Null(geometry.Hit(keys, first, new GraphPoint(1000, 0), 8));
        keys[0] = keys[0] with { Interpolation = Interpolation.Linear };
        Assert.Null(geometry.Hit(keys, first, geometry.HandlePixel(outgoing, keys[0].Time), 8));
    }

    [Fact]
    public void MarqueeWorksInEveryDragDirectionWithInclusiveEdges()
    {
        Keyframe[] keys = [new(S(5), -5), new(S(7), 0), new(S(10), 5)];
        KeyframeGraphGeometry geometry = Geometry(Clip());
        GraphPoint a = geometry.Pixel(S(5), -5);
        GraphPoint b = geometry.Pixel(S(7), 0);
        Assert.Equal([S(5), S(7)], geometry.InRectangle(keys, a, b));
        Assert.Equal(geometry.InRectangle(keys, a, b), geometry.InRectangle(keys, b, a));
    }

    [Fact]
    public void FitIncludesOvershootAndBothHoldEndpoints()
    {
        var parameter = new Parameter { Keyframes = [new(S(5), 0, Interpolation.Hold), new(S(10), 100)] };
        (double low, double high) = KeyframeGraphGeometry.Fit(parameter, [new(1, -30), new(2, 160)]);
        Assert.True(low < -30 && high > 160);
        (low, high) = KeyframeGraphGeometry.Fit(parameter, KeyframeGraph.Sample(parameter, GraphMode.Value));
        Assert.True(low < 0 && high > 100);
    }

    [Fact]
    public void FitWithFloor_KeepsTheSpeedAxisAboveZero()
    {
        var ramp = new Parameter { Keyframes = [new(S(0), 0.1), new(S(2), 2)] };
        (double low, double high) = KeyframeGraphGeometry.Fit(ramp, []);
        Assert.True(low < 0);
        Assert.Equal((0.0, high), KeyframeGraphGeometry.Fit(ramp, [], floor: 0));
        // Un valor real por debajo del piso sigue a la vista.
        Assert.Equal(-1, KeyframeGraphGeometry.Fit(ramp, [new(1, -1)], floor: 0).Low);
    }

    [Fact]
    public void FlatCurveWithSpec_OpensTheAxisToATenthOfTheSliderRange()
    {
        var flat = new Parameter { Keyframes = [new(S(1), 1), new(S(4), 1)] };
        ParameterSpec spec = SpeedGraph.Spec;
        (double low, double high) = KeyframeGraphGeometry.Fit(flat, [], spec, floor: 0);
        Assert.Equal((spec.Maximum - spec.Minimum) / 10 * 1.2, high - low, 6);
        Assert.True(low > 0 && low < 1 && high > 1);
        // Media altura del gráfico mueve el valor lo bastante para que se note en el modelo (centésimas en la velocidad).
        KeyframeGraphGeometry geometry = Geometry(Clip()) with { Low = low, High = high };
        Assert.True(Math.Abs(geometry.ValueAt(geometry.Height / 2) - geometry.ValueAt(geometry.Height - KeyframeGraphGeometry.Padding)) > 0.1);

        var unbounded = spec with { Minimum = double.NegativeInfinity };
        (low, high) = KeyframeGraphGeometry.Fit(flat, [], unbounded);
        Assert.True(double.IsFinite(low) && double.IsFinite(high) && low < 1 && high > 1);
    }

    [Fact]
    public void CatalogIncludesAudioMasksAndRepeatedEffectsWithDistinctIdentity()
    {
        Clip clip = Clip();
        clip.Effects =
        [
            new() { Id = "a", Type = BuiltInEffects.Audio, Parameters = new() { ["gainDb"] = new() } },
            new() { Id = "m", Type = BuiltInEffects.Mask, Parameters = new() { ["x"] = new() } },
            new() { Id = "m2", Type = BuiltInEffects.Mask, Parameters = new() { ["x"] = new() } },
        ];
        IReadOnlyList<GraphParameter> choices = GraphParameters.For(clip, new(1920, 1080, 10));
        Assert.Equal(["a", "m", "m2"], choices.Select(c => c.EffectId));
        Assert.Equal(3, choices.Select(c => c.Label).Distinct().Count());
    }

    [Fact]
    public void TimeFitAndRulerUseTheSharedTimelineScale()
    {
        Clip clip = Clip();
        var fit = KeyframeGraphGeometry.FitTime(clip, 1020);
        Assert.Equal(100, fit.PixelsPerSecond);
        Assert.Equal(2000, fit.ScrollX);
        KeyframeGraphGeometry geometry = Geometry(clip);
        Assert.Contains(geometry.TimeTicks(Rational.Fps30), tick => tick.Time == S(20) && tick.X == 0 && tick.IsMajor);
    }

    [Fact]
    public void TimelineOriginRejectsInvalidValuesAndSharesScale()
    {
        var state = new TimelineState { ScrollX = -5 };
        Assert.Equal(0, state.ScrollX);
        state.ScrollX = double.NaN;
        Assert.Equal(0, state.ScrollX);
        state.ScrollX = 2000;
        state.PixelsPerSecond = 100;
        KeyframeGraphGeometry geometry = Geometry(Clip()) with { ScrollX = state.ScrollX, PixelsPerSecond = state.PixelsPerSecond };
        Assert.Equal(10, geometry.Pixel(S(5), 0).Seconds);
    }

    private static (Project Project, UndoStack History, ParameterSpec Spec) Editable()
    {
        Clip clip = Clip();
        clip.Effects = [new() { Id = "fx", Type = BuiltInEffects.Transform, Parameters = new()
        {
            ["opacity"] = new() { Value = 0.2, Keyframes = [new(S(6), 0.2, Interpolation.Bezier) { Ease = BezierEase.Smooth }, new(S(8), 0.7)] },
            ["x"] = new() { Keyframes = [new(S(6), 1), new(S(8), 3)] },
            ["y"] = new() { Keyframes = [new(S(6), 2), new(S(8), 4)] },
        } }];
        var sequence = new Sequence { Id = "seq", VideoTracks = [new() { Id = "v", Kind = TrackKind.Video, Clips = [clip] }] };
        var project = new Project { Id = "project", Sequences = [sequence], Media = [new() { Id = "media", Path = "video.mp4", Duration = S(30), HasVideo = true }] };
        return (project, new UndoStack(project), ParameterSpec.For(BuiltInEffects.Transform, "opacity", new(1920, 1080, 10)));
    }

    private static Parameter Parameter(Project project, string name = "opacity") =>
        project.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters[name];

    [Fact]
    public void GroupDragKeepsRelativeValuesAndEaseAndIsOneUndoStep()
    {
        (Project project, UndoStack history, ParameterSpec spec) = Editable();
        Keyframe[] original = [.. Parameter(project).Keyframes];
        string before = ProjectSerializer.Serialize(project);
        history.BeginGroup("Mover keyframes");
        history.Execute(KeyframeGraphEdits.Move("seq", "clip", "fx", "opacity", original, Time.Zero, S(1), 0.1, spec));
        history.Execute(KeyframeGraphEdits.Move("seq", "clip", "fx", "opacity", original, S(1), S(2), 0.2, spec));
        history.EndGroup();
        Assert.Equal(1, history.Count);
        Assert.Equal([S(8), S(10)], Parameter(project).Keyframes.Select(k => k.Time));
        Assert.Equal(0.4, Parameter(project).Keyframes[0].Value, 8);
        Assert.Equal(0.9, Parameter(project).Keyframes[1].Value, 8);
        Assert.Equal(BezierEase.Smooth, Parameter(project).Keyframes[0].Ease);
        string after = ProjectSerializer.Serialize(project);
        Assert.True(history.Undo());
        Assert.Equal(before, ProjectSerializer.Serialize(project));
        Assert.True(history.Redo());
        Assert.Equal(after, ProjectSerializer.Serialize(project));
    }

    [Fact]
    public void GroupClampsValueDeltaTogetherAndCancelRestoresEverything()
    {
        (Project project, UndoStack history, ParameterSpec spec) = Editable();
        Keyframe[] original = [.. Parameter(project).Keyframes];
        string before = ProjectSerializer.Serialize(project);
        history.BeginGroup("Mover keyframes");
        history.Execute(KeyframeGraphEdits.Move("seq", "clip", "fx", "opacity", original, Time.Zero, S(1), 100, spec));
        Assert.Equal(0.5, Parameter(project).Keyframes[0].Value, 8);
        Assert.Equal(1, Parameter(project).Keyframes[1].Value);
        history.CancelGroup();
        Assert.Equal(before, ProjectSerializer.Serialize(project));
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void CollisionRollsBackTheWholeMoveIncludingValues()
    {
        (Project project, UndoStack history, ParameterSpec spec) = Editable();
        Keyframe[] original = [Parameter(project).Keyframes[0]];
        string before = ProjectSerializer.Serialize(project);
        Assert.Throws<EditException>(() => history.Execute(KeyframeGraphEdits.Move("seq", "clip", "fx", "opacity", original, Time.Zero, S(2), 0.1, spec)));
        Assert.Equal(before, ProjectSerializer.Serialize(project));
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void PositionDragMovesMatchingPartnerKeys()
    {
        (Project project, UndoStack history, _) = Editable();
        ParameterSpec spec = ParameterSpec.For(BuiltInEffects.Transform, "x", new(1920, 1080, 10));
        history.Execute(KeyframeGraphEdits.Move("seq", "clip", "fx", "x", [.. Parameter(project, "x").Keyframes], Time.Zero, S(1), 2, spec, "y"));
        Assert.Equal([S(7), S(9)], Parameter(project, "y").Keyframes.Select(k => k.Time));
        Assert.Equal([2.0, 4.0], Parameter(project, "y").Keyframes.Select(k => k.Value));
    }

    [Fact]
    public void DeleteAllAndPasteUsingCoreClipboardAreUndoable()
    {
        (Project project, UndoStack history, _) = Editable();
        string before = ProjectSerializer.Serialize(project);
        Time[] times = [.. Parameter(project).Keyframes.Select(k => k.Time)];
        KeyframeClipboard clipboard = KeyframeClipboard.Copy(Parameter(project), times);
        history.Execute(KeyframeGraphEdits.Delete("seq", "clip", "fx", "opacity", times));
        Assert.Empty(Parameter(project).Keyframes);
        history.Execute(Edits.PasteKeyframes("seq", "clip", "fx", "opacity", clipboard, S(6)));
        Assert.Equal(BezierEase.Smooth, Parameter(project).Keyframes[0].Ease);
        Assert.True(history.Undo());
        Assert.Empty(Parameter(project).Keyframes);
        Assert.True(history.Undo());
        Assert.Equal(before, ProjectSerializer.Serialize(project));
    }
}
