using System.Numerics;
using Rodaje.Analysis.Video;
using Rodaje.App.Services.Tracking;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Analysis;

/// <summary>Del cuadro codificado a la imagen de la capa y del seguimiento a los keyframes de una máscara.</summary>
public sealed class MaskTrackingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Image_and_coded_points_round_trip_with_rotation_and_crop(int rotation)
    {
        var view = new SourceView(1920, 1080, rotation, new CropRect(0.1, 0.05, 0.2, 0.15));
        foreach (Vector2 p in new Vector2[] { new(0, 0), new(1, 0), new(0.3f, 0.7f), new(1, 1) })
        {
            Vector2 back = view.ToImage(view.ToCoded(p));
            Assert.True(Vector2.Distance(p, back) < 1e-5f, $"{p} → {back}");
        }

        // Girado 90° en sentido horario, la esquina de arriba a la izquierda que se ve es la de abajo a la izquierda codificada.
        if (rotation == 90)
        {
            Vector2 corner = new SourceView(1920, 1080, 90, CropRect.None).ToCoded(Vector2.Zero);
            Assert.Equal(new Vector2(0, 1080), corner);
        }
    }

    [Fact]
    public void A_known_motion_moves_scales_and_turns_the_mask_box()
    {
        // Una imagen de 1920×1080 sin girar: el cuadro se movió 100 px a la derecha, creció 1,2× y giró 10° alrededor del origen.
        var spec = new MaskSpec(MaskShape.Bezier, 0.5, 0.5, 0.2, 0.3, 5, 0.02, 1, false);
        var view = new SourceView(1920, 1080, 0, CropRect.None);
        Similarity motion = Similarity.FromParts(10 * Math.PI / 180, 1.2, 100, 0);
        MaskVertex[] square = MaskPath.Rectangle();
        (_, Vector2[] anchors) = MaskTracking.Start(spec, square, 1920, 1080);
        Vector2[] moved = [.. anchors.Select(a =>
        {
            Vector2 coded = view.ToCoded(a);
            (double x, double y) = motion.Apply(coded.X, coded.Y);
            return view.ToImage(new Vector2((float)x, (float)y));
        })];
        var frame = new TrackedFrame(Time.FromSeconds(1), motion, moved, view, view, false);

        MaskTrackKey key = MaskTracking.Key(spec, square, frame, 1920, 1080, 5);

        (double cx, double cy) = motion.Apply(960, 540);
        Assert.Equal(cx / 1920, key.X, 5);
        Assert.Equal(cy / 1080, key.Y, 5);
        Assert.Equal(0.24, key.Width, 4);
        Assert.Equal(0.36, key.Height, 4);
        Assert.Equal(15, key.Rotation, 3);
        // Los vértices siguieron a la caja: relativos a ella no cambian.
        Assert.All(key.Vertices!.Zip(square), p => Assert.True(Vector2.Distance(p.First.Point, p.Second.Point) < 1e-4f));
    }

    [Fact]
    public void The_rotation_does_not_jump_a_full_turn()
    {
        var spec = new MaskSpec(MaskShape.Rectangle, 0.5, 0.5, 0.2, 0.2, 175, 0, 1, false);
        var view = new SourceView(1000, 1000, 0, CropRect.None);
        Similarity motion = Similarity.FromParts(10 * Math.PI / 180, 1, 0, 0);
        var frame = new TrackedFrame(Time.Zero, motion, [], view, view, false);
        MaskTrackKey key = MaskTracking.Key(spec, [], frame, 1000, 1000, 175);
        Assert.Equal(185, key.Rotation, 3);
    }
}
