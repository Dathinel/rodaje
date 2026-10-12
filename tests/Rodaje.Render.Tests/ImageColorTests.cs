using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using K = Rodaje.Core.Model.BuiltInEffects.ChromaKeyParameters;
using W = Rodaje.Core.Model.BuiltInEffects.ColorWheelsParameters;

namespace Rodaje.Render.Tests;

/// <summary>Croma, ruedas, curvas, recorte y antes/después en la GPU frente a sus referencias en la CPU (≤2/255).</summary>
public sealed class ImageColorTests
{
    private const int Block = 64;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    private static readonly (int R, int G, int B)[] Palette =
    [
        (40, 190, 60), (30, 140, 45), (220, 170, 140), (200, 40, 30), (235, 235, 235), (140, 170, 140), (30, 60, 210), (20, 20, 20),
    ];

    public static TheoryData<double, double, double, double, double, double> WheelCases() => new()
    {
        // lift general, lift rojo, gamma general, gamma azul, gain general, gain verde
        { 0.1, 0, 0, 0, 0, 0 },
        { -0.1, 0.05, 0, 0, 0, 0 },
        { 0, 0, 0.5, 0, 0, 0 },
        { 0, 0, -0.4, 0.3, 0, 0 },
        { 0, 0, 0, 0, 0.3, -0.2 },
        { 0.05, -0.05, 0.2, -0.2, -0.15, 0.1 },
    };

    [GpuTheory]
    [MemberData(nameof(WheelCases))]
    public void Wheels_match_the_reference(double lift, double liftRed, double gamma, double gammaBlue, double gain, double gainGreen)
    {
        LayerEffect wheels = Fx.Make(BuiltInEffects.ColorWheels, null, (W.Lift, lift), (W.LiftRed, liftRed), (W.Gamma, gamma),
            (W.GammaBlue, gammaBlue), (W.Gain, gain), (W.GainGreen, gainGreen));
        AssertPalette([wheels], c => EffectReference.Wheels(c, new C3(lift + liftRed, lift, lift), new C3(gamma, gamma, gamma + gammaBlue),
            new C3(gain, gain + gainGreen, gain)));
    }

    [GpuFact]
    public void Curves_match_the_reference_master_after_channel()
    {
        const string master = "0,0;0.25,0.18;0.75,0.85;1,1";
        const string red = "0,0.1;0.5,0.6;1,0.9";
        const string blue = "0,0;1,0.7";
        string key = string.Join('|', master, red, "", blue);
        ToneCurve m = ToneCurve.Parse(master), r = ToneCurve.Parse(red), b = ToneCurve.Parse(blue);
        AssertPalette([new LayerEffect(EffectRegistry.Find(BuiltInEffects.Curves)!.Code, default, new ResourceKey(key))],
            c => new C3(m.Evaluate(r.Evaluate(c.R)), m.Evaluate(c.G), m.Evaluate(b.Evaluate(c.B))));
    }

    [GpuFact]
    public void Green_screen_matches_the_reference_and_keeps_the_subject()
    {
        C3 key = Decode(Palette[0]);
        LayerEffect effect = Key(key, 0.25, 0.1, 0.6, 0);
        RgbaImage image = AssertPalette([effect], c => Premultiplied(EffectReference.ChromaKey(c, key, 0.25, 0.1, 0.6, 0)));

        image.AssertColor(32, 32, new Rgb(0, 0, 0), 1);
        image.AssertColor(Block + 32, 32, new Rgb(0, 0, 0), 2);
        Rgb skin = ReferenceColor.Decode(Encode(Palette[2]), Bt709, false);
        (byte sr, _, _, _) = image.At((2 * Block) + 32, 32);
        Assert.InRange(sr, skin.R - 12, skin.R + 2);
        (byte gr, byte gg, byte gb, _) = image.At((5 * Block) + 32, 32);
        Assert.True(gg < 170 && gg - Math.Max(gr, gb) < 20, $"El derrame verde sigue: ({gr}, {gg}, {gb}).");
    }

    [GpuFact]
    public void Blue_screen_with_cleanup_matches_the_reference()
    {
        C3 key = Decode(Palette[6]);
        RgbaImage image = AssertPalette([Key(key, 0.2, 0.15, 0.5, 0.5)],
            c => Premultiplied(EffectReference.ChromaKey(c, key, 0.2, 0.15, 0.5, 0.5)));
        image.AssertColor((6 * Block) + 32, 32, new Rgb(0, 0, 0), 1);
        (byte r, _, _, _) = image.At((3 * Block) + 32, 32);
        Assert.True(r > 150, "El rojo debe quedar opaco con una clave azul.");
    }

    [GpuFact]
    public void Key_combines_with_a_mask_and_the_preview_can_leave_it_out()
    {
        using GpuDevice gpu = GpuDevice.Create();
        C3 key = Decode(Palette[0]);
        LayerFrame Layer() => Layers.From(gpu, PaletteImage(), Bt709, new LayerLook([Key(key, 0.15, 0.1, 0, 0), Fx.Make(BuiltInEffects.Mask, null,
            (BuiltInEffects.MaskParameters.X, 0.25), (BuiltInEffects.MaskParameters.Width, 0.5), (BuiltInEffects.MaskParameters.Height, 1))]));

        RgbaImage masked = EffectFidelityTests.Compose(gpu, Block * Palette.Length, Block, out CompositeResult result, null, Layer());
        Assert.Equal(0, result.EffectsSkipped);
        masked.AssertColor(32, 32, new Rgb(0, 0, 0), 1);
        masked.AssertColor((5 * Block) + 32, 32, new Rgb(0, 0, 0), 1);
        Assert.True(masked.At((2 * Block) + 32, 32).R > 150, "Lo que la máscara deja ver y no es la clave queda.");

        using var compositor = new Compositor(gpu, Block * Palette.Length, Block) { Bypass = PreviewBypass.Key };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using CompositionFrame frame = Layers.Frame(Block * Palette.Length, Block, [Layer()]);
        compositor.Render(frame);
        var image = new RgbaImage(compositor.ReadbackRgba(), Block * Palette.Length, Block);
        image.AssertColor(32, 32, Decode(Palette[0]).To255());
    }

    [GpuFact]
    public void Compare_split_shows_the_left_without_color_and_export_ignores_it()
    {
        using GpuDevice gpu = GpuDevice.Create();
        int width = Block * Palette.Length;
        LayerFrame Layer() => Layers.From(gpu, PaletteImage(), Bt709, new LayerLook([Fx.Color((BuiltInEffects.ColorParameters.Exposure, 0.7)),
            Fx.Make(BuiltInEffects.ColorWheels, null, (W.Gain, -0.2))]));
        using var compositor = new Compositor(gpu, width, Block) { CompareSplit = 0.5f };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using (CompositionFrame frame = Layers.Frame(width, Block, [Layer()]))
        {
            compositor.Render(frame);
        }

        var image = new RgbaImage(compositor.ReadbackRgba(), width, Block);
        for (int i = 0; i < Palette.Length; i++)
        {
            C3 c = Decode(Palette[i]);
            C3 graded = EffectReference.Wheels(EffectReference.Grade(c, 0.7, 0, 0, 0, 0, 0, 0), default, default, new C3(-0.2, -0.2, -0.2));
            image.AssertColor((i * Block) + 32, 32, (i < Palette.Length / 2 ? c : graded).To255());
        }

        using (CompositionFrame frame = Layers.Frame(width, Block, [Layer()]))
        {
            compositor.RenderStrict(frame);
        }

        image = new RgbaImage(compositor.ReadbackRgba(), width, Block);
        C3 first = Decode(Palette[2]);
        image.AssertColor((2 * Block) + 32, 32,
            EffectReference.Wheels(EffectReference.Grade(first, 0.7, 0, 0, 0, 0, 0, 0), default, default, new C3(-0.2, -0.2, -0.2)).To255());
    }

    [GpuTheory]
    [InlineData(0, 0, 0.5, 0)]
    [InlineData(0.25, 0, 0.25, 0)]
    [InlineData(0.5, 0.25, 0, 0.25)]
    public void Crop_fits_the_remaining_part_like_a_whole_image(double left, double top, double right, double bottom)
    {
        // 256×128: mitad izquierda roja, derecha azul, con una franja blanca arriba de 32 px.
        const int width = 256, height = 128;
        using GpuDevice gpu = GpuDevice.Create();
        YuvCode red = Encode((200, 40, 30)), blue = Encode((30, 60, 210)), white = Encode((235, 235, 235));
        var source = YuvImage.Solid(width, height, false, red).Fill(width / 2, 0, width / 2, height, blue).Fill(0, 0, width, 32, white);
        var crop = new CropRect(left, top, right, bottom);
        LayerFrame layer = Layers.From(gpu, source, Bt709, LayerTransform.Identity with { Crop = crop });
        RgbaImage image = EffectFidelityTests.Compose(gpu, width, height, out _, null, layer);

        // Lo que queda (en píxeles de la imagen) ajustado al cuadro, como Fit con el tamaño recortado.
        int cw = (int)Math.Round(width * crop.Width), ch = (int)Math.Round(height * crop.Height);
        PixelRect fit = VideoLayout.Fit(width, height, cw, ch);
        for (int y = 2; y < height; y += 7)
        {
            for (int x = 2; x < width; x += 7)
            {
                double u = (x + 0.5 - fit.X) / fit.Width, v = (y + 0.5 - fit.Y) / fit.Height;
                if (Math.Abs(u) < 0.03 || Math.Abs(u - 1) < 0.03 || Math.Abs(v) < 0.03 || Math.Abs(v - 1) < 0.03)
                {
                    continue;
                }

                if (u < 0 || u > 1 || v < 0 || v > 1)
                {
                    image.AssertColor(x, y, new Rgb(0, 0, 0), 0);
                    continue;
                }

                double sx = (left + (u * crop.Width)) * width, sy = (top + (v * crop.Height)) * height;
                if (Math.Abs(sx - (width / 2)) < 2 || Math.Abs(sy - 32) < 2)
                {
                    continue;
                }

                YuvCode expected = sy < 32 ? white : sx < width / 2 ? red : blue;
                image.AssertColor(x, y, ReferenceColor.Decode(expected, Bt709, false));
            }
        }
    }

    private static LayerEffect Key(C3 key, double tolerance, double softness, double spill, double cleanup) =>
        Fx.Make(BuiltInEffects.ChromaKey, null, (K.Red, key.R), (K.Green, key.G), (K.Blue, key.B), (K.Tolerance, tolerance),
            (K.Softness, softness), (K.Spill, spill), (K.Cleanup, cleanup));

    private static C3 Premultiplied((C3 Color, double Alpha) keyed) => keyed.Color * keyed.Alpha;

    private static YuvImage PaletteImage()
    {
        var source = new YuvImage(Block * Palette.Length, Block, false);
        for (int i = 0; i < Palette.Length; i++)
        {
            source.Fill(i * Block, 0, Block, Block, Encode(Palette[i]));
        }

        return source;
    }

    private static RgbaImage AssertPalette(IReadOnlyList<LayerEffect> effects, Func<C3, C3> reference)
    {
        using GpuDevice gpu = GpuDevice.Create();
        LayerFrame layer = Layers.From(gpu, PaletteImage(), Bt709, new LayerLook(effects));
        RgbaImage image = EffectFidelityTests.Compose(gpu, Block * Palette.Length, Block, out CompositeResult result, null, layer);
        Assert.Equal(0, result.EffectsSkipped);
        for (int i = 0; i < Palette.Length; i++)
        {
            Rgb expected = reference(Decode(Palette[i])).To255();
            image.AssertColor((i * Block) + 32, 32, expected);
            image.AssertColor((i * Block) + 4, 60, expected);
        }

        return image;
    }

    private static YuvCode Encode((int R, int G, int B) c) => ReferenceColor.Encode(c.R, c.G, c.B, Bt709, false);

    private static C3 Decode((int R, int G, int B) c) => C3.From(ReferenceColor.Decode(Encode(c), Bt709, false));
}
