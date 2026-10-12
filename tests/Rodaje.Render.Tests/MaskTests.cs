using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;

namespace Rodaje.Render.Tests;

/// <summary>Máscaras en la GPU frente a <see cref="MaskGeometry"/> (la referencia de la CPU), píxel a píxel.</summary>
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
        { 4, 0.5, 0.5, 0.7, 0.8, 0, 0, 1, false },
        { 4, 0.45, 0.5, 0.6, 0.7, -20, 0.08, 1, false },
        { 4, 0.55, 0.45, 0.5, 0.8, 15, 0.04, 0.7, true },
        { 5, 0.5, 0.5, 0.8, 0.6, 0, 0, 1, false },
        { 5, 0.5, 0.5, 0.7, 0.5, 30, 0.03, 0.9, false },
    };

    private static readonly Vector2[] Star =
    [
        new(0, -0.5f), new(0.12f, -0.15f), new(0.5f, -0.1f), new(0.2f, 0.12f), new(0.32f, 0.5f),
        new(0, 0.28f), new(-0.32f, 0.5f), new(-0.2f, 0.12f), new(-0.5f, -0.1f), new(-0.12f, -0.15f),
    ];

    /// <summary>Una gota con manejadores y borde variable (peso 2,5 arriba, 0,5 abajo).</summary>
    private static readonly MaskVertex[] Blob =
    [
        new(new Vector2(-0.4f, -0.3f), new Vector2(-0.05f, 0.15f), new Vector2(0.05f, -0.15f)),
        new(new Vector2(0.1f, -0.45f), new Vector2(-0.2f, 0), new Vector2(0.2f, 0), 2.5f),
        new(new Vector2(0.45f, 0.2f), Vector2.Zero, Vector2.Zero),
        new(new Vector2(-0.1f, 0.4f), new Vector2(0.15f, 0.05f), new Vector2(-0.15f, -0.05f), 0.5f),
    ];

    /// <summary>Una pluma en S con grosor de 1 a 3.</summary>
    private static readonly MaskVertex[] Pen =
    [
        new(new Vector2(-0.45f, 0.3f), Vector2.Zero, new Vector2(0.2f, -0.4f), 1),
        new(new Vector2(0, 0), new Vector2(-0.15f, 0), new Vector2(0.15f, 0), 2),
        new(new Vector2(0.45f, -0.3f), new Vector2(-0.2f, 0.4f), Vector2.Zero, 3),
    ];

    [GpuTheory]
    [MemberData(nameof(Shapes))]
    public void Each_shape_matches_the_reference(int shape, double x, double y, double width, double height, double rotation,
        double feather, double opacity, bool invert)
    {
        var spec = new MaskSpec((MaskShape)shape, x, y, width, height, rotation, feather, opacity, invert) { Thickness = 0.05 };
        (string? text, MaskVertex[] vertices) = spec.Shape switch
        {
            MaskShape.Path => (MaskGeometry.FormatPath(Star), [.. Star.Select(p => new MaskVertex(p.X, p.Y))]),
            MaskShape.Bezier => (MaskPath.Format(Blob), Blob),
            MaskShape.Stroke => (MaskPath.Format(Pen), Pen),
            _ => ((string?)null, Array.Empty<MaskVertex>()),
        };
        Vector3[] polyline = vertices.Length == 0 ? [] : MaskGeometry.Polyline(spec.Shape, vertices, (float)(width * Width), (float)(height * Height),
            (float)(feather * Math.Min(Width, Height)));
        AssertMasked([Mask(spec, text)], null, (px, py) => MaskGeometry.Factor(spec, px, py, Width, Height, polyline));
    }

    [GpuTheory]
    [InlineData(0.5, 0)]
    [InlineData(1, 0.04)]
    [InlineData(0.3, -0.03)]
    public void Rounded_corners_and_expansion_match_the_reference(double roundness, double expansion)
    {
        var spec = new MaskSpec(MaskShape.Rectangle, 0.5, 0.5, 0.6, 0.7, 12, 0.03, 1, false) { Roundness = roundness, Expansion = expansion };
        AssertMasked([Mask(spec)], null, (px, py) => MaskGeometry.Factor(spec, px, py, Width, Height));
    }

    [GpuTheory]
    [InlineData(MaskMode.Add)]
    [InlineData(MaskMode.Subtract)]
    [InlineData(MaskMode.Intersect)]
    [InlineData(MaskMode.Difference)]
    public void Modes_combine_like_the_reference(MaskMode mode)
    {
        var a = new MaskSpec(MaskShape.Rectangle, 0.4, 0.5, 0.5, 0.8, 0, 0.1, 1, false) { Mode = MaskMode.Add };
        var b = new MaskSpec(MaskShape.Ellipse, 0.6, 0.5, 0.5, 0.9, 0, 0.1, 0.7, false) { Mode = mode };
        var c = new MaskSpec(MaskShape.Ellipse, 0.2, 0.2, 0.2, 0.3, 0, 0, 1, true) { Mode = MaskMode.Subtract };
        AssertMasked([Mask(a), Mask(b), Mask(c)], null, (px, py) => MaskGeometry.Combine(
        [
            (a.Mode, MaskGeometry.Factor(a, px, py, Width, Height)),
            (b.Mode, MaskGeometry.Factor(b, px, py, Width, Height)),
            (c.Mode, MaskGeometry.Factor(c, px, py, Width, Height)),
        ]));
    }

    [GpuFact]
    public void Two_format_6_masks_still_multiply()
    {
        var a = new MaskSpec(MaskShape.Rectangle, 0.4, 0.5, 0.6, 0.8, 0, 0.1, 1, false);
        var b = new MaskSpec(MaskShape.Ellipse, 0.6, 0.5, 0.5, 0.9, 0, 0.1, 0.7, false);
        AssertMasked([Mask(a), Mask(b)], null,
            (px, py) => MaskGeometry.Factor(a, px, py, Width, Height) * MaskGeometry.Factor(b, px, py, Width, Height));
    }

    [GpuFact]
    public void An_animated_shape_is_drawn_at_its_time()
    {
        MaskVertex[] small = MaskPath.Rectangle();
        MaskVertex[] big = [.. small.Select(v => v with { Point = v.Point * 1.6f })];
        string keys = MaskShapeTrack.Format([(Rodaje.Core.Timing.Time.FromSeconds(1), small), (Rodaje.Core.Timing.Time.FromSeconds(3), big)]);
        var spec = new MaskSpec(MaskShape.Bezier, 0.5, 0.5, 0.4, 0.5, 0, 0.02, 1, false);
        MaskVertex[] middle = MaskShapeTrack.Parse(keys)!.Evaluate(Rodaje.Core.Timing.Time.FromSeconds(2));
        Vector3[] polyline = MaskGeometry.Polyline(MaskShape.Bezier, middle, (float)(0.4 * Width), (float)(0.5 * Height), 0.02f * Height);
        AssertMasked([Mask(spec, keys, seconds: 2)], null, (px, py) => MaskGeometry.Factor(spec, px, py, Width, Height, polyline));
    }

    [GpuFact]
    public void A_mask_on_the_color_effect_grades_only_inside_it()
    {
        var spec = new MaskSpec(MaskShape.Ellipse, 0.5, 0.5, 0.6, 0.7, 0, 0.08, 1, false) { Mode = MaskMode.Add };
        LayerEffect color = Fx.Color((C.Exposure, 0.6), (C.Saturation, -0.5));
        LayerEffect mask = Mask(spec, target: 0);
        YuvCode code = ReferenceColor.Encode(150, 90, 60, Bt709, false);
        C3 original = C3.From(ReferenceColor.Decode(code, Bt709, false));
        C3 graded = EffectReference.Grade(original, 0.6, 0, -0.5, 0, 0, 0, 0);
        using GpuDevice gpu = GpuDevice.Create();
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(Width, Height, false, code), Bt709, new LayerLook([color, mask]));
        RgbaImage image = EffectFidelityTests.Compose(gpu, Width, Height, out CompositeResult result, null, layer);
        Assert.Equal(0, result.EffectsSkipped);
        for (int y = 1; y < Height - 1; y += 3)
        {
            for (int x = 1; x < Width - 1; x += 3)
            {
                double m = MaskGeometry.Factor(spec, x + 0.5, y + 0.5, Width, Height);
                image.AssertColor(x, y, C3.Mix(original, graded, m).To255(), 3);
            }
        }
    }

    [GpuFact]
    public void A_masked_blur_softens_only_inside_and_a_dangling_mask_does_nothing()
    {
        // Mitad izquierda oscura y derecha clara: el borde vertical en x = 128 se desenfoca solo dentro de la elipse de arriba.
        YuvCode dark = ReferenceColor.Encode(40, 40, 40, Bt709, false);
        YuvCode light = ReferenceColor.Encode(220, 220, 220, Bt709, false);
        YuvImage halves = YuvImage.Solid(Width, Height, false, dark).Fill(Width / 2, 0, Width / 2, Height, light);
        var spec = new MaskSpec(MaskShape.Ellipse, 0.5, 0.25, 0.5, 0.4, 0, 0, 1, false) { Mode = MaskMode.Add };
        using GpuDevice gpu = GpuDevice.Create();
        LayerFrame layer = Layers.From(gpu, halves, Bt709, new LayerLook([Fx.Blur(24), Mask(spec, target: 0), Mask(spec with { X = 0.1 }, target: MaskGeometry.NoTarget - 1)]));
        RgbaImage image = EffectFidelityTests.Compose(gpu, Width, Height, out CompositeResult result, null, layer);
        Assert.Equal(0, result.EffectsSkipped);

        // Dentro de la elipse, al lado del borde, gris intermedio; abajo (fuera), el borde sigue duro.
        Assert.InRange(image.At(124, 32).R, 70, 190);
        Assert.InRange(image.At(124, 110).R, 30, 50);
        Assert.InRange(image.At(132, 110).R, 210, 230);
        // La máscara sin efecto no recorta la capa: la esquina sigue opaca.
        Assert.InRange(image.At(4, 4).R, 30, 50);
    }

    [GpuFact]
    public void Pixelate_averages_each_block_and_a_mask_limits_it()
    {
        // Rayas verticales de 2 px: cada bloque de 16 px promedia la mitad clara y la mitad oscura.
        YuvCode dark = ReferenceColor.Encode(20, 20, 20, Bt709, false);
        YuvCode light = ReferenceColor.Encode(200, 200, 200, Bt709, false);
        YuvImage stripes = YuvImage.Solid(Width, Height, false, dark);
        for (int x = 0; x < Width; x += 4)
        {
            stripes.Fill(x, 0, 2, Height, light);
        }

        var spec = new MaskSpec(MaskShape.Rectangle, 0.25, 0.5, 0.5, 1, 0, 0, 1, false) { Mode = MaskMode.Add };
        LayerEffect pixelate = Fx.Make(BuiltInEffects.Pixelate, null, (BuiltInEffects.PixelateParameters.Size, 16));
        using GpuDevice gpu = GpuDevice.Create();
        LayerFrame layer = Layers.From(gpu, stripes, Bt709, new LayerLook([pixelate, Mask(spec, target: 0)]));
        RgbaImage image = EffectFidelityTests.Compose(gpu, Width, Height, out CompositeResult result, null, layer);
        Assert.Equal(0, result.EffectsSkipped);
        double average = (C3.From(ReferenceColor.Decode(dark, Bt709, false)).R + C3.From(ReferenceColor.Decode(light, Bt709, false)).R) / 2 * 255;
        for (int y = 20; y < 100; y += 7)
        {
            for (int x = 20; x < 100; x += 5)
            {
                Assert.InRange(image.At(x, y).R, average - 6, average + 6);
            }
        }

        // A la derecha (fuera del rectángulo) siguen las rayas.
        Assert.True(Math.Abs(image.At(200, 60).R - image.At(202, 60).R) > 100);
    }

    [GpuTheory]
    [InlineData(MaskViewMode.Matte)]
    [InlineData(MaskViewMode.Overlay)]
    public void The_mask_view_shows_the_matte_or_tints_what_it_hides(MaskViewMode mode)
    {
        var spec = new MaskSpec(MaskShape.Ellipse, 0.5, 0.5, 0.5, 0.7, 0, 0.1, 1, false) { Mode = MaskMode.Add };
        YuvCode code = ReferenceColor.Encode(60, 140, 200, Bt709, false);
        C3 color = C3.From(ReferenceColor.Decode(code, Bt709, false));
        using GpuDevice gpu = GpuDevice.Create();
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(Width, Height, false, code), Bt709, new LayerLook([Mask(spec)]));
        using var compositor = new Compositor(gpu, Width, Height) { MaskView = new MaskPreview("clip-0", 0, mode) };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using CompositionFrame frame = Layers.Frame(Width, Height, layer);
        compositor.Render(frame);
        var image = new RgbaImage(compositor.ReadbackRgba(), Width, Height);
        var tint = new C3(1, 0.22, 0.18);
        for (int y = 2; y < Height - 2; y += 5)
        {
            for (int x = 2; x < Width - 2; x += 5)
            {
                double m = MaskGeometry.Factor(spec, x + 0.5, y + 0.5, Width, Height);
                C3 expected = mode == MaskViewMode.Matte ? new C3(m, m, m) : C3.Mix(color, tint, 0.55 * (1 - m));
                image.AssertColor(x, y, expected.To255(), 3);
            }
        }

        // La exportación no la ve: la capa sale recortada, con su color.
        compositor.RenderStrict(frame);
        var strict = new RgbaImage(compositor.ReadbackRgba(), Width, Height);
        strict.AssertColor(Width / 2, Height / 2, color.To255(), 3);
        strict.AssertColor(3, 3, new C3(0, 0, 0).To255(), 3);
    }

    [GpuFact]
    public void The_preview_and_the_export_draw_the_same_masks()
    {
        // Bézier con borde variable que suma, elipse que resta y una zona desenfocada: vista previa y exportación iguales.
        YuvCode dark = ReferenceColor.Encode(30, 60, 90, Bt709, false);
        YuvCode light = ReferenceColor.Encode(220, 200, 120, Bt709, false);
        YuvImage halves = YuvImage.Solid(Width, Height, false, dark).Fill(Width / 2, 0, Width / 2, Height, light);
        var blob = new MaskSpec(MaskShape.Bezier, 0.5, 0.5, 0.8, 0.9, 10, 0.05, 1, false) { Mode = MaskMode.Add };
        var hole = new MaskSpec(MaskShape.Ellipse, 0.6, 0.5, 0.2, 0.3, 0, 0.03, 1, false) { Mode = MaskMode.Subtract };
        var zone = new MaskSpec(MaskShape.Ellipse, 0.5, 0.3, 0.3, 0.4, 0, 0.05, 1, false) { Mode = MaskMode.Add };
        using GpuDevice gpu = GpuDevice.Create();
        LayerFrame Layer() => Layers.From(gpu, halves, Bt709, new LayerLook(
            [Fx.Blur(12), Mask(blob, MaskPath.Format(Blob)), Mask(hole), Mask(zone, target: 0)]));
        using var compositor = new Compositor(gpu, Width, Height);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using CompositionFrame frame = Layers.Frame(Width, Height, Layer());
        compositor.Render(frame);
        byte[] preview = compositor.ReadbackRgba();
        compositor.RenderStrict(frame);
        Assert.Equal(preview, compositor.ReadbackRgba());
    }

    [GpuFact]
    public void An_image_mask_samples_the_gray_frame_at_its_time()
    {
        // Rampa horizontal de 64×32: el render la estira a la imagen con filtro bilineal.
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

    /// <summary>Capa gris clara con las máscaras sobre negro: cada píxel es el color por el factor de la referencia.</summary>
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
                // El borde de la capa misma (medio píxel) no es de la máscara.
                if (x is 0 or Width - 1 || y is 0 or Height - 1)
                {
                    continue;
                }

                // ±4: con bordes anchos la textura de máscaras va a menos resolución y el filtro bilineal rehace la rampa.
                image.AssertColor(x, y, (color * factor(x + 0.5, y + 0.5)).To255(), 4);
            }
        }

        return result;
    }

    private static LayerEffect Mask(in MaskSpec spec, string? resource = null, double seconds = 0, int target = -1)
    {
        LayerEffect effect = Fx.Make(BuiltInEffects.Mask, resource is null ? null : new ResourceKey(resource),
            (M.X, spec.X), (M.Y, spec.Y), (M.Width, spec.Width), (M.Height, spec.Height), (M.Rotation, spec.Rotation),
            (M.Feather, spec.Feather), (M.Opacity, spec.Opacity), (M.Expansion, spec.Expansion), (M.Roundness, spec.Roundness),
            (M.Thickness, spec.Thickness));
        EffectValues values = effect.Values;
        values[MaskGeometry.ShapeSlot] = (int)spec.Shape;
        values[MaskGeometry.InvertSlot] = spec.Invert ? 1 : 0;
        values[MaskGeometry.ModeSlot] = (int)spec.Mode;
        values[MaskGeometry.MatteTimeSlot] = (float)seconds;
        values[MaskGeometry.TargetSlot] = target == MaskGeometry.NoTarget - 1 ? MaskGeometry.NoTarget : target + 1;
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
