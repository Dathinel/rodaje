using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;

namespace Rodaje.Render.Tests;

/// <summary>MÃ¡scaras en la GPU frente a <see cref="MaskGeometry"/> (la referencia de la CPU), pÃ­xel a pÃ­xel.</summary>
public sealed class MaskTests
{
    private const int Width = 256;
    private const int Height = 128;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    public static TheoryData<int, double, double, double, double, double, double, double, bool> Shapes() => new()
    {
        // forma, x, y, ancho, alto, giro, borde suave, opacidad, invertir
        { 0, 0.5, 0.5, 0.5, 0.5, 0, 0, 1, false },
        { 0, 0.4, 0.6, 0.6, 0.3, 25, 0.1, 1, false },
        { 0, 0.5, 0.5, 0.7, 0.7, 0, 0.25, 0.6, true },
        { 1, 0.5, 0.5, 0.5, 0.8, 0, 0, 1, false },
        { 1, 0.6, 0.4, 0.4, 0.6, -40, 0.15, 0.8, false },
        { 1, 0.5, 0.5, 0.6, 0.9, 10, 0.05, 1, true },
        { 2, 0.5, 0.5, 0.6, 0.8, 0, 0, 1, false },
        { 2, 0.45, 0.55, 0.5, 0.7, 30, 0.12, 0.9, true },
    };

    private static readonly Vector2[] Star =
    [
        new(0, -0.5f), new(0.12f, -0.15f), new(0.5f, -0.1f), new(0.2f, 0.12f), new(0.32f, 0.5f),
        new(0, 0.28f), new(-0.32f, 0.5f), new(-0.2f, 0.12f), new(-0.5f, -0.1f), new(-0.12f, -0.15f),
    ];

    [GpuTheory]
    [MemberData(nameof(Shapes))]
    public void Each_shape_matches_the_reference(int shape, double x, double y, double width, double height, double rotation,
        double feather, double opacity, bool invert)
    {
        var spec = new MaskSpec((MaskShape)shape, x, y, width, height, rotation, feather, opacity, invert);
        LayerEffect mask = Mask(spec, spec.Shape == MaskShape.Path ? MaskGeometry.FormatPath(Star) : null);
        Vector2[] path = spec.Shape == MaskShape.Path ? MaskGeometry.ParsePath(MaskGeometry.FormatPath(Star)) : [];
        AssertMasked([mask], null, (px, py) => MaskGeometry.Factor(spec, px, py, Width, Height, path));
    }

    [GpuFact]
    public void Two_masks_multiply()
    {
        var a = new MaskSpec(MaskShape.Rectangle, 0.4, 0.5, 0.6, 0.8, 0, 0.1, 1, false);
        var b = new MaskSpec(MaskShape.Ellipse, 0.6, 0.5, 0.5, 0.9, 0, 0.1, 0.7, false);
        AssertMasked([Mask(a), Mask(b)], null,
            (px, py) => MaskGeometry.Factor(a, px, py, Width, Height) * MaskGeometry.Factor(b, px, py, Width, Height));
    }

    [GpuFact]
    public void An_image_mask_samples_the_gray_frame_at_its_time()
    {
        // Rampa horizontal de 64Ã—32: el render la estira a la imagen con filtro bilineal.
        var gray = new byte[64 * 32];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                gray[(y * 64) + x] = (byte)(x * 4);
            }
        }

        var resources = new MatteResources(new MatteFrame(64, 32, gray, 1));
        var spec = new MaskSpec(MaskShape.Image, 0.5, 0.5, 1, 1, 0, 0, 1, false);
        CompositeResult result = AssertMasked([Mask(spec, "file:mascara.mkv", 2.5)], resources, (px, _) =>
        {
            double u = Math.Clamp((px * 64.0 / Width) - 0.5, 0, 63);
            int i = Math.Min((int)u, 62);
            return ((gray[i] * (1 - (u - i))) + (gray[i + 1] * (u - i))) / 255.0;
        });

        Assert.Equal(2.5, resources.LastSeconds, 3);
        Assert.Equal(0, result.MissingContent);
    }

    [GpuFact]
    public void A_missing_image_mask_is_counted_and_the_layer_is_drawn_whole()
    {
        var spec = new MaskSpec(MaskShape.Image, 0.5, 0.5, 1, 1, 0, 0, 1, false);
        CompositeResult result = AssertMasked([Mask(spec, "file:no-esta.mkv")], new MatteResources(null), (_, _) => 1);
        Assert.Equal(1, result.MissingContent);
    }

    /// <summary>Capa gris clara con las mÃ¡scaras sobre negro: cada pÃ­xel es el color por el factor de la referencia.</summary>
    private static CompositeResult AssertMasked(IReadOnlyList<LayerEffect> masks, IEffectResources? resources, Func<double, double, double> factor)
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvCode light = ReferenceColor.Encode(220, 210, 200, Bt709, false);
        C3 color = C3.From(ReferenceColor.Decode(light, Bt709, false));
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(Width, Height, false, light), Bt709, new LayerLook(masks));
        RgbaImage image = EffectFidelityTests.Compose(gpu, Width, Height, out CompositeResult result, resources, layer);
        Assert.Equal(0, result.EffectsSkipped);
        for (int y = 0; y < Height; y += 3)
        {
            for (int x = 0; x < Width; x += 3)
            {
                // El borde de la capa misma (medio pÃ­xel) no es de la mÃ¡scara.
                if (x is 0 or Width - 1 || y is 0 or Height - 1)
                {
                    continue;
                }

                image.AssertColor(x, y, (color * factor(x + 0.5, y + 0.5)).To255(), 3);
            }
        }

        return result;
    }

    private static LayerEffect Mask(in MaskSpec spec, string? resource = null, double matteSeconds = 0)
    {
        LayerEffect effect = Fx.Make(BuiltInEffects.Mask, resource is null ? null : new ResourceKey(resource),
            (M.X, spec.X), (M.Y, spec.Y), (M.Width, spec.Width), (M.Height, spec.Height), (M.Rotation, spec.Rotation),
            (M.Feather, spec.Feather), (M.Opacity, spec.Opacity));
        EffectValues values = effect.Values;
        values[7] = (int)spec.Shape;
        values[8] = spec.Invert ? 1 : 0;
        values[MaskGeometry.MatteTimeSlot] = (float)matteSeconds;
        return effect with { Values = values };
    }

    private sealed class MatteResources(MatteFrame? frame) : IEffectResources
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public double LastSeconds { get; private set; } = double.NaN;

        public bool TryGetLut(ResourceKey key, out LutData? lut)
        {
            lut = null;
            return false;
        }

        public bool TryGetMatte(ResourceKey key, double seconds, out MatteFrame? matte)
        {
            LastSeconds = seconds;
            matte = frame;
            return frame is not null;
        }
    }
}
