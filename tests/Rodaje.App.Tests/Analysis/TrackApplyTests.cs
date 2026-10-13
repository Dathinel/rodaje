using System.Numerics;
using Rodaje.App.Services.Tracking;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using O = Rodaje.Core.Model.BuiltInEffects.TrackOptions;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Tests.Analysis;

/// <summary>
/// Aplicar un seguimiento: los keyframes que salen para otro clip, un anclaje de esquinas, una máscara o la
/// estabilización, en un paso del historial que se deshace entero.
/// </summary>
public sealed class TrackApplyTests
{
    private const int Frames = 10;
    private static readonly Rational Rate = Rational.Fps30;

    private static Time F(long frame) => Time.FromFrames(frame, Rate);

    /// <summary>Un video 1920×1080 en V1 con un seguimiento que corre 0,01 de la imagen por cuadro, y una imagen encima en V2.</summary>
    private static Project Project(PointTrackKind kind, Func<int, Vector2[]> points)
    {
        var video = new Track { Id = "v1", Kind = TrackKind.Video, Name = "V1", Clips = [new Clip { Id = "a", MediaId = "m", SourceOut = F(30) }] };
        var over = new Track { Id = "v2", Kind = TrackKind.Video, Name = "V2", Clips = [new Clip { Id = "b", MediaId = "img", SourceOut = F(30) }] };
        var project = new Project { Id = "p", Sequences = [new Sequence { Id = "s", VideoTracks = [video, over] }] };
        project.Media.Add(new MediaRef { Id = "m", Path = @"C:\cam.mp4", HasVideo = true, FrameRate = Rate, Width = 1920, Height = 1080 });
        project.Media.Add(new MediaRef { Id = "img", Path = @"C:\cartel.png", HasVideo = true, FrameRate = Rate, Width = 960, Height = 540 });
        var track = new PointTrack([.. Enumerable.Range(0, Frames).Select(k => F(k))],
            [.. Enumerable.Range(0, Frames).Select(k => points(k).Select(p => new TrackSample(p, k == 4 ? 0 : 0.95f)).ToArray())]);
        var effect = new EffectInstance { Id = "t", Type = BuiltInEffects.Track };
        effect.Options[O.Kind] = PointTrack.KindName(kind);
        effect.Options[O.Samples] = track.Format();
        video.Clips[0].Effects.Add(effect);
        return project;
    }

    private static TrackApplication Prepare(Project project, TrackUse use, string? target, bool autoCrop = false)
    {
        Sequence sequence = project.Sequences[0];
        TrackApplication? application = TrackApply.Prepare(project, sequence, sequence.FindClip("a")!.Value.Clip, "t", use, Time.Zero, target, autoCrop,
            out string problem);
        Assert.True(application is not null, problem);
        return application;
    }

    private static Parameter Transform(Project project, string clip, string name) =>
        project.Sequences[0].FindClip(clip)!.Value.Clip.Effects.First(e => e.Type == BuiltInEffects.Transform).Parameters[name];

    [Fact]
    public void Another_clip_follows_the_point_with_its_offset_in_one_undo_step()
    {
        Project project = Project(PointTrackKind.Point, k => [new Vector2(0.4f + (0.01f * k), 0.5f)]);
        var undo = new UndoStack(project);
        undo.Execute(Edits.SetParameter("s", "b", EnsureTransform(undo, project, "b"), T.X, 100, Time.Zero));
        string before = Rodaje.Core.Serialization.ProjectSerializer.Serialize(project);

        TrackApplication application = Prepare(project, TrackUse.FollowPosition, "b");
        undo.Execute(application.Command);

        // El cuadro 4 se perdió: no pone keyframe y entre el 3 y el 5 se interpola.
        Parameter x = Transform(project, "b", T.X);
        Assert.Equal(Frames - 1, x.Keyframes.Count);
        for (int k = 0; k < Frames; k++)
        {
            Assert.Equal(100 + (19.2 * k), x.Evaluate(F(k)), 3);
        }

        Assert.Equal(0, Transform(project, "b", T.Y).Evaluate(F(7)), 6);
        undo.Undo();
        Assert.Equal(before, Rodaje.Core.Serialization.ProjectSerializer.Serialize(project));
    }

    [Fact]
    public void Two_points_carry_scale_and_rotation_around_the_tracked_point()
    {
        // Dos puntos que giran 2° por cuadro y se separan un 1 % por cuadro alrededor del centro de la imagen.
        Project project = Project(PointTrackKind.Two, k =>
        {
            double angle = 2.0 * k * Math.PI / 180, r = 200 * (1 + (0.01 * k));
            var c = new Vector2(960, 540);
            var d = new Vector2((float)(r * Math.Cos(angle)), (float)(r * Math.Sin(angle)));
            return [(c - d) / new Vector2(1920, 1080), (c + d) / new Vector2(1920, 1080)];
        });
        var undo = new UndoStack(project);
        undo.Execute(Edits.SetParameter("s", "b", EnsureTransform(undo, project, "b"), T.X, 300, Time.Zero));
        undo.Execute(Prepare(project, TrackUse.FollowAll, "b").Command);

        Assert.Equal(1.09, Transform(project, "b", T.Scale).Evaluate(F(9)), 4);
        Assert.Equal(18, Transform(project, "b", T.Rotation).Evaluate(F(9)), 3);
        // El centro de la imagen de arriba gira alrededor del centro del cuadro: 300 px a 18° y ×1,09.
        Assert.Equal(300 * 1.09 * Math.Cos(18 * Math.PI / 180), Transform(project, "b", T.X).Evaluate(F(9)), 1);
        Assert.Equal(300 * 1.09 * Math.Sin(18 * Math.PI / 180), Transform(project, "b", T.Y).Evaluate(F(9)), 1);
    }

    [Fact]
    public void Corner_pin_sends_the_four_corners_to_the_tracked_points()
    {
        Vector2[] Quad(int k) =>
        [
            new(0.30f + (0.01f * k), 0.20f), new(0.70f + (0.01f * k), 0.25f), new(0.68f + (0.01f * k), 0.80f), new(0.32f + (0.01f * k), 0.75f),
        ];
        Project project = Project(PointTrackKind.Corners, Quad);
        new UndoStack(project).Execute(Prepare(project, TrackUse.CornerPin, "b").Command);

        // La imagen de 960×540 ocupa todo el cuadro (1920×1080 locales): cada esquina se corre hasta el punto seguido.
        EffectInstance pin = project.Sequences[0].FindClip("b")!.Value.Clip.Effects.First(e => e.Type == BuiltInEffects.CornerPin);
        Vector2[] corners = [new(0, 0), new(1920, 0), new(1920, 1080), new(0, 1080)];
        for (int c = 0; c < 4; c++)
        {
            Vector2 expected = (Quad(6)[c] * new Vector2(1920, 1080)) - corners[c];
            Assert.Equal(expected.X, pin.Parameters[BuiltInEffects.CornerPinParameters.All[2 * c]].Evaluate(F(6)), 2);
            Assert.Equal(expected.Y, pin.Parameters[BuiltInEffects.CornerPinParameters.All[(2 * c) + 1]].Evaluate(F(6)), 2);
        }
    }

    [Fact]
    public void Stabilizing_moves_the_clip_back_and_crops_automatically()
    {
        Project project = Project(PointTrackKind.Point, k => [new Vector2(0.5f + (0.01f * k), 0.5f)]);
        TrackApplication free = Prepare(project, TrackUse.Stabilize, null);
        Assert.Equal(1, free.Zoom);
        new UndoStack(project).Execute(free.Command);
        Assert.Equal(-19.2 * 9, Transform(project, "a", T.X).Evaluate(F(9)), 3);

        Project cropped = Project(PointTrackKind.Point, k => [new Vector2(0.5f + (0.01f * k), 0.5f)]);
        TrackApplication auto = Prepare(cropped, TrackUse.Stabilize, null, autoCrop: true);
        new UndoStack(cropped).Execute(auto.Command);

        // Corrido 172,8 px a la izquierda, la imagen tiene que crecer hasta que su borde derecho llegue al del cuadro.
        Assert.InRange(auto.Zoom, (1920 / (1920 - (2 * 172.8))) - 1e-4, (1920 / (1920 - (2 * 172.8))) + 1e-4);
        Assert.Equal(-172.8 * auto.Zoom, Transform(cropped, "a", T.X).Evaluate(F(9)), 2);
        Assert.Equal(auto.Zoom, Transform(cropped, "a", T.Scale).Evaluate(F(0)), 4);
    }

    [Fact]
    public void A_pixelated_zone_follows_the_point()
    {
        Project project = Project(PointTrackKind.Point, k => [new Vector2(0.4f + (0.01f * k), 0.3f)]);
        new UndoStack(project).Execute(Prepare(project, TrackUse.PixelateZone, null).Command);
        Clip clip = project.Sequences[0].FindClip("a")!.Value.Clip;
        EffectInstance mask = clip.Effects.Single(e => e.Type == BuiltInEffects.Mask);
        Assert.Contains(clip.Effects, e => e.Type == BuiltInEffects.Pixelate && e.Id == mask.Options[BuiltInEffects.MaskOptions.Target]);
        Assert.Equal(0.4 + 0.08, MaskGeometry.Evaluate(mask, F(8)).X, 4);
        Assert.Equal(0.3, MaskGeometry.Evaluate(mask, F(8)).Y, 4);
    }

    [Fact]
    public void Scale_and_rotation_need_two_points_and_a_pin_needs_four()
    {
        Project project = Project(PointTrackKind.Point, k => [new Vector2(0.5f, 0.5f)]);
        Sequence sequence = project.Sequences[0];
        Assert.Null(TrackApply.Prepare(project, sequence, sequence.FindClip("a")!.Value.Clip, "t", TrackUse.CornerPin, Time.Zero, "b", false, out string why));
        Assert.Contains("4 esquinas", why, StringComparison.Ordinal);
        Assert.Null(TrackApply.Prepare(project, sequence, sequence.FindClip("a")!.Value.Clip, "t", TrackUse.FollowAll, Time.Zero, "b", false, out _));
    }

    private static string EnsureTransform(UndoStack undo, Project project, string clip)
    {
        undo.Execute(Edits.EnsureEffect("s", clip, BuiltInEffects.Transform));
        return project.Sequences[0].FindClip(clip)!.Value.Clip.Effects.First(e => e.Type == BuiltInEffects.Transform).Id;
    }
}
