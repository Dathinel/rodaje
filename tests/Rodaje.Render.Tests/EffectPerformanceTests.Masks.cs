using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;

namespace Rodaje.Render.Tests;

/// <summary>Cuatro máscaras difuminadas sobre una capa, a 1080p y 4K, y una zona desenfocada con su máscara.</summary>
public sealed partial class EffectPerformanceTests
{
    [GpuTheory]
    [InlineData(1920, 1080, 1.5)]
    [InlineData(3840, 2160, 2.0)]
    public void Four_feathered_masks(int width, int height, double target)
    {
        using GpuDevice gpu = GpuDevice.Create();
        nint texture = Texture(gpu, width, height + (height % 16 == 0 ? 0 : 16 - (height % 16)), 1);
        try
        {
            LayerFrame layer = Layers.FromTexture(gpu, texture, 0, width, height, Bt709, LayerTransform.Identity, 0, new LayerLook(FourMasks()));
            (double median, _) = Measure(gpu, $"1 capa {height}p con 4 máscaras difuminadas (2 Bézier de 24 vértices, elipse y rectángulo redondeado)",
                width, height, width, height, layer);
            Require(median <= target, $"4 máscaras a {height}p: {median:0.000} ms (objetivo ≤{target}).");
        }
        finally
        {
            Layers.Release(texture);
        }
    }

    [GpuFact]
    public void A_blurred_zone_with_its_mask_in_4k()
    {
        using GpuDevice gpu = GpuDevice.Create();
        nint texture = Texture(gpu, 3840, 2160, 1);
        try
        {
            LayerFrame layer = Layers.FromTexture(gpu, texture, 0, 3840, 2160, Bt709, LayerTransform.Identity, 0, new LayerLook(
            [
                Fx.Blur(30),
                MaskEffect(MaskShape.Ellipse, 0.5, 0.4, 0.22, 0.3, 0.03, MaskMode.Add, target: 0),
            ]));
            (double median, _) = Measure(gpu, "1 capa 4K con una zona desenfocada (desenfoque limitado por una elipse)", 3840, 2160, 3840, 2160, layer);
            Require(median <= 3.0, $"Zona desenfocada 4K: {median:0.000} ms (objetivo ≤3).");
        }
        finally
        {
            Layers.Release(texture);
        }
    }

    private static LayerEffect[] FourMasks()
    {
        // Bézier de 24 vértices suaves (una flor) con borde variable.
        MaskVertex[] flower = MaskPath.Smooth([.. Enumerable.Range(0, 24).Select(i =>
        {
            double a = i * Math.Tau / 24;
            double r = 0.5 * (i % 2 == 0 ? 1 : 0.75);
            return new Vector2((float)(r * Math.Cos(a)), (float)(r * Math.Sin(a)));
        })], closed: true);
        flower = [.. flower.Select((v, i) => v with { Weight = 0.5f + (i % 3) })];
        string text = MaskPath.Format(flower);
        return
        [
            MaskEffect(MaskShape.Bezier, 0.35, 0.45, 0.4, 0.6, 0.04, MaskMode.Add, text),
            MaskEffect(MaskShape.Bezier, 0.7, 0.55, 0.35, 0.5, 0.04, MaskMode.Add, text),
            MaskEffect(MaskShape.Ellipse, 0.5, 0.5, 0.3, 0.3, 0.05, MaskMode.Subtract),
            MaskEffect(MaskShape.Rectangle, 0.5, 0.5, 0.9, 0.9, 0.03, MaskMode.Intersect, roundness: 0.4),
        ];
    }

    private static LayerEffect MaskEffect(MaskShape shape, double x, double y, double width, double height, double feather, MaskMode mode,
        string? text = null, int target = -1, double roundness = 0)
    {
        LayerEffect effect = Fx.Make(BuiltInEffects.Mask, text is null ? null : new ResourceKey(text),
            (M.X, x), (M.Y, y), (M.Width, width), (M.Height, height), (M.Feather, feather), (M.Roundness, roundness));
        EffectValues values = effect.Values;
        values[MaskGeometry.ShapeSlot] = (int)shape;
        values[MaskGeometry.ModeSlot] = (int)mode;
        values[MaskGeometry.TargetSlot] = target + 1;
        return effect with { Values = values };
    }
}
