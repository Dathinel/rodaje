using System.Numerics;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using P = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Tests.Inspector;

/// <summary>Animaciones de fábrica, gráfica, guías de ajuste, trayectoria y asas del visor: la lógica sin WinUI.</summary>
public sealed class TransformModuleTests
{
    private static async Task<(EditorSession Session, ClipPropertyEditor Editor, Clip Clip)> Open(double at = 2)
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        Clip clip = session.MainTrack!.Clips[0];
        session.Select([clip.Id]);
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(at), Duration = session.Sequence.Duration });
        return (session, new ClipPropertyEditor(session), clip);
    }

    private static Clip Current(EditorSession session, string id) => session.Sequence.GetClip(id).Clip;

    private static EffectInstance Transform(EditorSession session, string id) => ClipPropertyEditor.FindEffect(Current(session, id), BuiltInEffects.Transform)!;

    [Theory]
    [InlineData(MotionPresets.KenBurns)]
    [InlineData(MotionPresets.SlowZoom)]
    [InlineData(MotionPresets.SlideIn)]
    [InlineData(MotionPresets.SlideOut)]
    [InlineData(MotionPresets.Shake)]
    [InlineData(MotionPresets.Heartbeat)]
    [InlineData(MotionPresets.Spin)]
    [InlineData(MotionPresets.PunchZoom)]
    public void A_preset_is_one_undo_step_inside_the_clip(string id) => UiThread.Run(async () =>
    {
        (EditorSession session, _, Clip clip) = await Open();
        int steps = session.History.Count;
        Assert.True(session.Execute(MotionPresetCommands.Apply(session.SequenceId, clip.Id, id)));
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Empty(ModelValidator.Validate(session.Project));

        EffectInstance transform = Transform(session, clip.Id);
        Assert.Contains(transform.Parameters.Values, p => p.Keyframes.Count >= 2);
        Assert.All(transform.Parameters.Values.SelectMany(p => p.Keyframes), k => Assert.InRange(k.Time.Ticks, clip.SourceIn.Ticks, clip.SourceOut.Ticks));

        session.Undo();
        Assert.Null(ClipPropertyEditor.FindEffect(Current(session, clip.Id), BuiltInEffects.Transform));
    });

    [Fact]
    public void A_preset_starts_from_the_scale_the_clip_has() => UiThread.Run(async () =>
    {
        (EditorSession session, ClipPropertyEditor editor, Clip clip) = await Open();
        editor.SetValue(clip, BuiltInEffects.Transform, P.Scale, 1.5, "Cambiar escala");
        session.Execute(MotionPresetCommands.Apply(session.SequenceId, clip.Id, MotionPresets.KenBurns));
        List<Keyframe> keys = Transform(session, clip.Id).Parameters[P.Scale].Keyframes;
        Assert.Equal(1.5, keys[0].Value, 9);
        Assert.Equal(1.5 * 1.15, keys[^1].Value, 9);
    });

    [Fact]
    public void Preview_samples_every_preset()
    {
        foreach (MotionPreset preset in MotionPresets.All)
        {
            IReadOnlyList<(double X, double Y, double Scale, double Rotation)> frames = MotionPresetCommands.Preview(preset.Id, 160, 90, 9);
            Assert.Equal(9, frames.Count);
            Assert.True(frames.Distinct().Count() > 1, preset.Id + " no se mueve");
        }
    }

    [Fact]
    public void Graph_handles_round_trip_through_a_drag()
    {
        var from = new Keyframe(Time.Zero, 10, Interpolation.Bezier) { Ease = new BezierEase(0.2, 0.1, 0.7, 1.3) };
        var to = new Keyframe(Time.FromSeconds(2), 30);
        (GraphPoint outgoing, GraphPoint incoming) = KeyframeGraph.Handles(from, to, Time.Zero);
        Assert.Equal(new GraphPoint(0.4, 12), outgoing);
        Assert.Equal(1.4, incoming.Seconds, 9);
        Assert.Equal(36, incoming.Value, 9);

        BezierEase moved = KeyframeGraph.Drag(from, to, Time.Zero, true, new GraphPoint(0.5, 40));
        Assert.Equal(new BezierEase(0.25, 1.5, 0.7, 1.3), moved);
        Assert.Equal(BezierEase.MaxY, KeyframeGraph.Drag(from, to, Time.Zero, false, new GraphPoint(9, 900)).Y2);
        Assert.Equal(1, KeyframeGraph.Drag(from, to, Time.Zero, false, new GraphPoint(9, 900)).X2);
    }

    [Fact]
    public void Graph_samples_value_and_speed()
    {
        var p = new Parameter { Keyframes = [new Keyframe(Time.Zero, 0), new Keyframe(Time.FromSeconds(2), 100)] };
        IReadOnlyList<GraphPoint> value = KeyframeGraph.Sample(p, GraphMode.Value);
        Assert.Equal(KeyframeGraph.SamplesPerSegment + 1, value.Count);
        Assert.Equal(0, value[0].Value);
        Assert.Equal(100, value[^1].Value, 3);
        IReadOnlyList<GraphPoint> speed = KeyframeGraph.Sample(p, GraphMode.Speed);
        Assert.All(speed.Skip(1).SkipLast(1), s => Assert.Equal(50, s.Value, 3));
    }

    [Fact]
    public void Snapping_pulls_the_center_and_the_edges_within_the_magnet()
    {
        // 1920×1080 mostrado a la mitad: el imán de 8 px de pantalla son 16 px de la secuencia.
        (Vector2 correction, IReadOnlyList<SnapLine> hits) = SnapGuides.Snap(new Vector2(970, 300), new Vector2(200, 100), 1920, 1080, 0.5);
        Assert.Equal(new Vector2(-10, 0), correction);
        Assert.Equal(SnapKind.Center, Assert.Single(hits).Kind);

        // El borde izquierdo de la capa a 10 px del borde del cuadro, el de arriba a 5 px del tercio.
        (correction, hits) = SnapGuides.Snap(new Vector2(210, 355 + 100), new Vector2(200, 100), 1920, 1080, 0.5);
        Assert.Equal(new Vector2(-10, 5), correction);
        Assert.Equal([SnapKind.Edge, SnapKind.Third], hits.Select(h => h.Kind));

        (correction, hits) = SnapGuides.Snap(new Vector2(800, 800), new Vector2(50, 50), 1920, 1080, 0.5);
        Assert.Equal(Vector2.Zero, correction);
        Assert.Empty(hits);
    }

    [Fact]
    public void Path_handles_start_straight_and_mirror_when_dragged()
    {
        var transform = new EffectInstance { Type = BuiltInEffects.Transform };
        transform.Parameters[P.X] = new Parameter { Keyframes = [new Keyframe(Time.Zero, 0), new Keyframe(Time.FromSeconds(1), 300), new Keyframe(Time.FromSeconds(2), 300)] };
        transform.Parameters[P.Y] = new Parameter { Keyframes = [new Keyframe(Time.Zero, 0), new Keyframe(Time.FromSeconds(1), 0), new Keyframe(Time.FromSeconds(2), 300)] };
        IReadOnlyList<PathKey> keys = MotionPathView.Handles(transform, 1000, 1000);
        Assert.Equal(3, keys.Count);
        Assert.Null(keys[0].In);
        Assert.Equal(new Vector2(800, 500), keys[1].Point);
        Assert.Equal(new Vector2(700, 500), keys[1].In);
        Assert.Equal(new Vector2(800, 600), keys[1].Out);

        ((double X, double Y)? spatialIn, (double X, double Y)? spatialOut) = MotionPathView.Tangents(keys[1], true, new Vector2(900, 500), mirror: true);
        Assert.Equal((100.0, 0.0), spatialOut);
        Assert.Equal((-100.0, 0.0), spatialIn);
        Assert.Equal(new Vector2(500, 500), MotionPathView.At(transform, 1000, 1000, Time.Zero));
        Assert.Equal(49, MotionPathView.Sample(transform, 1000, 1000).Count);
    }

    [Fact]
    public void Each_viewer_gesture_is_one_undo_step_and_the_anchor_does_not_move_the_image() => UiThread.Run(async () =>
    {
        (EditorSession session, ClipPropertyEditor editor, Clip clip) = await Open();
        editor.SetValue(clip, BuiltInEffects.Transform, P.Scale, 0.5, "Cambiar escala");
        editor.SetValue(Current(session, clip.Id), BuiltInEffects.Transform, P.Rotation, 30, "Girar");
        int steps = session.History.Count;

        ClipTransformDrag move = ClipTransformDrag.Begin(session, editor, clip.Id, ClipGesture.Move)!;
        for (int i = 1; i <= 20; i++)
        {
            Assert.True(move.Move(i * 10, i * 5));
        }

        move.Complete();
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal(200, Transform(session, clip.Id).Parameters[P.X].Value);

        LayerTransform Shown() => ToLayer(TransformValues.Read(Transform(session, clip.Id), clip.SourceIn));
        Vector2 cornerBefore = LayerLayout.Place(1920, 1080, 1920, 1080, Shown())!.Value.TopLeft;
        ClipTransformDrag anchor = ClipTransformDrag.Begin(session, editor, clip.Id, ClipGesture.Anchor)!;
        anchor.MoveAnchor(120, -80);
        anchor.MoveAnchor(300, 150);
        anchor.Complete();
        Assert.Equal(steps + 2, session.History.Count);
        Vector2 cornerAfter = LayerLayout.Place(1920, 1080, 1920, 1080, Shown())!.Value.TopLeft;
        Assert.True(Vector2.Distance(cornerBefore, cornerAfter) < 0.2f, $"{cornerBefore} → {cornerAfter}");
        Assert.Equal(300, Transform(session, clip.Id).Parameters[P.AnchorX].Value);

        ClipTransformDrag cancelled = ClipTransformDrag.Begin(session, editor, clip.Id, ClipGesture.Scale)!;
        cancelled.Scale(2);
        cancelled.Cancel();
        Assert.Equal(0.5, Transform(session, clip.Id).Parameters[P.Scale].Value);
        Assert.Equal(steps + 2, session.History.Count);
    });

    [Fact]
    public void A_preset_at_double_speed_lasts_the_same_on_screen_and_keeps_its_destination()
    {
        var clip = new Clip { Id = "c", MediaId = "m", SourceIn = Time.Zero, SourceOut = Time.FromSeconds(8), Start = Time.FromSeconds(1) };
        clip.Speed = new ClipSpeed { Rate = new Rational(2, 1) };
        ClipTimeMap.Fit(clip.Start, Time.Zero, Time.FromSeconds(8), clip.Speed).ApplyTo(clip);
        List<Keyframe> keys = MotionPresetCommands.ToClip(clip, [new MotionKey(0, 0, Interpolation.Linear), new MotionKey(0.6, 100, Interpolation.Linear)]);
        Assert.Equal(Time.FromSeconds(1.2), keys[1].Time);

        // Dos que caen en el mismo instante: gana el último (el destino), en el instante del primero.
        List<Keyframe> collided = MotionPresetCommands.ToClip(clip, [new MotionKey(0, 0, Interpolation.Elastic), new MotionKey(1e-12, 100, Interpolation.Linear)]);
        Assert.Equal(100, Assert.Single(collided).Value);
    }

    [Fact]
    public void The_anchor_drag_inverts_rotation_skew_and_stretch()
    {
        var start = new TransformValues(0, 0, 0.8, 30, 0, 0, 1.5, 0.7, 20, -10);
        (double x, double y) = start.ToLocal(40, -25)!.Value;
        (double cx, double cy) = start.CompensateAnchor(x, y);
        // M·local = el desplazamiento de pantalla: CompensateAnchor da t + (M − I)·d, así que M·d = t' − t + d.
        Assert.Equal(40, cx + x, 6);
        Assert.Equal(-25, cy + y, 6);
    }

    private static LayerTransform ToLayer(TransformValues v) => new(v.X, v.Y, v.Scale, v.Rotation, 1)
    {
        Shape = new LayerShape(v.AnchorX, v.AnchorY, v.ScaleX, v.ScaleY, v.SkewX, v.SkewY, false, false),
    };
}
