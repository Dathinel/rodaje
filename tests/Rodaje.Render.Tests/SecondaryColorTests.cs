using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using Q = Rodaje.Core.Model.BuiltInEffects.HslQualifierParameters;
using V = Rodaje.Core.Model.BuiltInEffects.VignetteParameters;

namespace Rodaje.Render.Tests;

/// <summary>
/// Calificador HSL (mÃ¡scaras conocidas y correcciÃ³n), curvas de tono y saturaciÃ³n, protecciÃ³n de piel y viÃ±eta con forma en
/// la GPU frente a sus referencias en la CPU (â‰¤2/255).
/// </summary>
public sealed class SecondaryColorTests
{
    private const int Block = 32;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    /// <summary>Pieles claras a oscuras, verdes de follaje, cielo, rojo y amarillo saturados, grises.</summary>
    private static readonly (int R, int G, int B)[] Palette =
    [
        (224, 172, 140), (198, 134, 103), (141, 85, 54), (90, 160, 60), (40, 110, 35), (110, 160, 220), (220, 40, 40),
        (230, 210, 40), (128, 128, 128), (30, 30, 30), (235, 235, 235), (160, 60, 170),
    ];

    public static TheoryData<double, double, double, double, double, bool> MaskCases() => new()
    {
        // tono, medio ancho, suavidad, saturaciÃ³n mÃ­nima, suavidad de bordes, invertir
        { 100, 30, 15, 0.15, 0.1, false },
        { 25, 15, 10, 0.1, 0.05, false },
        { 210, 25, 20, 0.2, 0.1, true },
    };

    /// <summary>Con Â«ver mÃ¡scaraÂ» la vista previa muestra el calificador en gris; se compara con la mÃ¡scara de la referencia.</summary>
    [GpuTheory]
    [MemberData(nameof(MaskCases))]
    public void Qualifier_mask_matches_known_masks(double hue, double width, double softness, double satLow, double edge, bool invert)
    {
        var q = new FxReference.Qualifier(hue, width, softness, satLow, 1, 0.05, 0.95, edge, 0, 0, 0, invert);
        AssertPalette([Qualifier(q)], c => { double m = FxReference.QualifierMask(c, q); return new C3(m, m, m); }, showMask: true);
    }

    [GpuTheory]
    [InlineData(100, 30, -60, -0.5, 0.0)]
    [InlineData(25, 18, 10, 0.3, 0.4)]
    [InlineData(210, 25, 30, 0.6, -0.5)]
    public void Qualifier_correction_matches_the_reference(double hue, double width, double shift, double saturation, double exposure)
    {
        var q = new FxReference.Qualifier(hue, width, 15, 0.1, 1, 0, 1, 0.1, shift, saturation, exposure, false);
        AssertPalette([Qualifier(q)], c => FxReference.Qualify(c, q));
    }

    [GpuFact]
    public void Export_never_shows_the_qualifier_mask()
    {
        var q = new FxReference.Qualifier(100, 30, 15, 0.15, 1, 0, 1, 0.1, -40, 0.2, 0, false);
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, Block * Palette.Length, Block) { ShowQualifierMask = true };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using CompositionFrame frame = Layers.Frame(Block * Palette.Length, Block, Layers.From(gpu, PaletteImage(), Bt709, new LayerLook([Qualifier(q)])));
        compositor.RenderStrict(frame);
        var image = new RgbaImage(compositor.ReadbackRgba(), Block * Palette.Length, Block);
        for (int i = 0; i < Palette.Length; i++)
        {
            image.AssertColor((i * Block) + 16, 16, FxReference.Qualify(Decode(Palette[i]), q).To255());
        }
    }

    [GpuFact]
    public void Hue_and_saturation_curves_match_the_reference()
    {
        const string hueHue = "0,0.5;0.3,0.42;0.6,0.55";
        const string hueSat = "0,0.5;0.33,0.15;0.66,0.8";
        const string lumaSat = "0,0.3;0.5,0.5;1,0.65";
        const string master = "0,0;0.5,0.55;1,1";
        string key = string.Join('|', master, "", "", "", hueHue, hueSat, lumaSat);
        ToneCurve tone = ToneCurve.Parse(master);
        HueCurve a = HueCurve.Parse(hueHue, true), b = HueCurve.Parse(hueSat, true), c = HueCurve.Parse(lumaSat, false);
        AssertPalette([new LayerEffect(EffectRegistry.Find(BuiltInEffects.Curves)!.Code, default, new ResourceKey(key))],
            x => FxReference.HueCurves(x.Map(tone.Evaluate), a, b, c));
    }

    [GpuTheory]
    [InlineData(0.9, 0.0, 0.0, 1.0)]
    [InlineData(-1.0, 0.0, 0.0, 1.0)]
    [InlineData(0.5, 0.7, -0.4, 0.6)]
    public void Skin_protection_keeps_skin_tones(double saturation, double temperature, double tint, double protection)
    {
        AssertPalette([Fx.Color((C.Saturation, saturation), (C.Temperature, temperature), (C.Tint, tint), (C.SkinProtection, protection))],
            c => FxReference.GradeProtected(c, saturation, temperature, tint, protection));
    }

    [Fact]
    public void Skin_protection_leaves_skin_closer_to_the_original_than_the_rest()
    {
        C3 skin = Decode(Palette[0]);
        C3 green = Decode(Palette[3]);
        double Change(C3 c, double protection)
        {
            C3 graded = FxReference.GradeProtected(c, -1, 0, 0, protection);
            return Math.Abs(graded.R - c.R) + Math.Abs(graded.G - c.G) + Math.Abs(graded.B - c.B);
        }

        Assert.True(Change(skin, 1) < 0.25 * Change(skin, 0), "La piel tendrÃ­a que quedar casi igual.");
        Assert.Equal(Change(green, 0), Change(green, 1), 6);
    }

    [GpuTheory]
    [InlineData(0.7, 0.3, 0.4, 1.0, 0.5, 0.5)]
    [InlineData(0.9, 0.2, 0.3, 0.5, 0.3, 0.6)]
    public void Shaped_vignette_matches_the_reference(double amount, double size, double softness, double roundness, double cx, double cy)
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvCode gray = ReferenceColor.Encode(180, 170, 160, Bt709, false);
        C3 basis = C3.From(ReferenceColor.Decode(gray, Bt709, false));
        LayerEffect vignette = Fx.Make(BuiltInEffects.Vignette, null, (V.Amount, amount), (V.Size, size), (V.Softness, softness),
            (V.Roundness, roundness), (V.CenterX, cx), (V.CenterY, cy));
        RgbaImage image = EffectFidelityTests.Compose(gpu, 256, 128, out _, null,
            Layers.From(gpu, YuvImage.Solid(256, 128, false, gray), Bt709, new LayerLook([vignette])));
        for (int y = 1; y < 128; y += 9)
        {
            for (int x = 1; x < 256; x += 11)
            {
                double gain = FxReference.Vignette((x + 0.5) / 256, (y + 0.5) / 128, 2, amount, size, softness, roundness, cx, cy);
                image.AssertColor(x, y, (basis * gain).To255());
            }
        }
    }

    private static LayerEffect Qualifier(FxReference.Qualifier q) =>
        Fx.Make(BuiltInEffects.HslQualifier, null, (Q.HueCenter, q.HueCenter), (Q.HueWidth, q.HueWidth), (Q.HueSoftness, q.HueSoftness),
            (Q.SaturationLow, q.SatLow), (Q.SaturationHigh, q.SatHigh), (Q.LumaLow, q.LumaLow), (Q.LumaHigh, q.LumaHigh), (Q.Softness, q.Softness),
            (Q.HueShift, q.HueShift), (Q.Saturation, q.Saturation), (Q.Exposure, q.Exposure))
        .Choose(BuiltInEffects.HslQualifierOptions.Invert, q.Invert ? "on" : "off");

    private static YuvImage PaletteImage()
    {
        var image = new YuvImage(Block * Palette.Length, Block, false);
        for (int i = 0; i < Palette.Length; i++)
        {
            image.Fill(i * Block, 0, Block, Block, Encode(Palette[i]));
        }

        return image;
    }

    private static void AssertPalette(IReadOnlyList<LayerEffect> effects, Func<C3, C3> reference, bool showMask = false)
    {
        using GpuDevice gpu = GpuDevice.Create();
        int width = Block * Palette.Length;
        using var compositor = new Compositor(gpu, width, Block) { ShowQualifierMask = showMask };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using CompositionFrame frame = Layers.Frame(width, Block, Layers.From(gpu, PaletteImage(), Bt709, new LayerLook(effects)));
        CompositeResult result = compositor.Render(frame);
        Assert.Equal(0, result.EffectsSkipped);
        var image = new RgbaImage(compositor.ReadbackRgba(), width, Block);
        for (int i = 0; i < Palette.Length; i++)
        {
            Rgb expected = reference(Decode(Palette[i])).To255();
            image.AssertColor((i * Block) + 16, 16, expected);
            image.AssertColor((i * Block) + 4, 28, expected);
        }
    }

    private static YuvCode Encode((int R, int G, int B) c) => ReferenceColor.Encode(c.R, c.G, c.B, Bt709, false);

    private static C3 Decode((int R, int G, int B) c) => C3.From(ReferenceColor.Decode(Encode(c), Bt709, false));
}
