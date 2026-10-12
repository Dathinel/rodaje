using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using Bl = Rodaje.Core.Model.BuiltInEffects.BlurParameters;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using Q = Rodaje.Core.Model.BuiltInEffects.HslQualifierParameters;

namespace Rodaje.Render.Tests;

/// <summary>
/// Lo que se ve es lo que se exporta con los efectos nuevos (mismos bytes a la misma resolución; media resolución cerca de
/// la completa reducida) y el color trabaja sin bandas en 10 bits: una rampa P010 con contraste y curvas conserva sus
/// códigos de 10 bits en el intermedio de media precisión.
/// </summary>
public sealed class PreviewExportTests
{
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    private static LayerEffect[] SixEffects() =>
    [
        Fx.Color((C.Contrast, 0.2), (C.Saturation, 0.3), (C.SkinProtection, 0.5)),
        Fx.Make(BuiltInEffects.HslQualifier, null, (Q.HueCenter, 30), (Q.HueShift, 12), (Q.Saturation, 0.2)),
        new LayerEffect(EffectRegistry.Find(BuiltInEffects.Curves)!.Code, default, new ResourceKey("0,0;0.5,0.55;1,1||||0,0.5;0.3,0.45|0.3,0.3;0.6,0.6|")),
        Fx.Make(BuiltInEffects.Blur, null, (Bl.Radius, 6)).Choose(BuiltInEffects.BlurOptions.Kind, "lens"),
        Fx.Make(BuiltInEffects.Glow, null, (BuiltInEffects.GlowParameters.Radius, 16)),
        Fx.Make(BuiltInEffects.Grain, null, (BuiltInEffects.GrainParameters.Amount, 0.3)).At(2.0),
    ];

    [GpuFact]
    public void Preview_and_export_give_the_same_bytes_with_six_new_effects()
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvImage image = Scene(512, 288);
        using var compositor = new Compositor(gpu, 512, 288);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using (CompositionFrame frame = Layers.Frame(512, 288, Layers.From(gpu, image, Bt709, new LayerLook(SixEffects()))))
        {
            Assert.Equal(0, compositor.Render(frame).EffectsSkipped);
        }

        byte[] preview = compositor.ReadbackRgba();
        using var export = new Compositor(gpu, 512, 288);
        using (CompositionFrame frame = Layers.Frame(512, 288, Layers.From(gpu, image, Bt709, new LayerLook(SixEffects()))))
        {
            export.RenderStrict(frame);
        }

        Assert.Equal(preview, export.ReadbackRgba());
    }

    [GpuFact]
    public void Half_resolution_preview_is_close_to_the_full_one_with_the_new_effects()
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvImage image = Scene(512, 288);
        byte[] Render(int width, int height)
        {
            using var compositor = new Compositor(gpu, width, height);
            Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
            LayerEffect[] effects = [.. SixEffects().Where(e => e.Code != EffectRegistry.Find(BuiltInEffects.Grain)!.Code)];
            using CompositionFrame frame = Layers.Frame(512, 288, Layers.From(gpu, image, Bt709, new LayerLook(effects)));
            Assert.Equal(0, compositor.Render(frame).EffectsSkipped);
            return compositor.ReadbackRgba();
        }

        byte[] full = Render(512, 288);
        byte[] half = Render(256, 144);
        double total = 0;
        for (int y = 0; y < 144; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int At(int fx, int fy) => full[(((fy * 512) + fx) * 4) + c];
                    double reduced = (At(2 * x, 2 * y) + At((2 * x) + 1, 2 * y) + At(2 * x, (2 * y) + 1) + At((2 * x) + 1, (2 * y) + 1)) / 4.0;
                    total += Math.Abs(reduced - half[(((y * 256) + x) * 4) + c]);
                }
            }
        }

        double mean = total / (256 * 144 * 3);
        Assert.True(mean <= 3, $"Diferencia media {mean:0.00}/255.");
    }

    /// <summary>
    /// Rampa P010 de un código cada dos columnas (64 a 939) con contraste +30 % y una curva: a la salida, en 10 bits, la
    /// pendiente es de 1,5 códigos por código y ningún salto pasa de 2 (leída en 8 bits serían saltos de 4).
    /// </summary>
    [GpuFact]
    public void A_ten_bit_ramp_keeps_its_codes_through_the_grade()
    {
        using GpuDevice gpu = GpuDevice.Create();
        const int width = 1752;
        var image = new YuvImage(width, 16, tenBit: true);
        for (int x = 0; x < width; x += 2)
        {
            image.Fill(x, 0, 2, 16, new YuvCode(64 + (x / 2), 512, 512));
        }

        using var compositor = new Compositor(gpu, width, 16);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        var curves = new LayerEffect(EffectRegistry.Find(BuiltInEffects.Curves)!.Code, default, new ResourceKey("0,0;0.5,0.5;1,1|||"));
        using (CompositionFrame frame = Layers.Frame(width, 16, Layers.From(gpu, image, Bt709, new LayerLook([Fx.Color((C.Contrast, 0.3)), curves]))))
        {
            compositor.Render(frame);
        }

        float[] pixels = compositor.ReadbackRgbaFloat();
        var codes = new List<int>();
        for (int x = 0; x < width; x++)
        {
            codes.Add((int)Math.Round(pixels[((8 * width) + x) * 4 + 1] * 1023));
        }

        List<int> inside = [.. codes.Where(c => c is > 0 and < 1023)];
        int distinct = inside.Distinct().Count();
        int maxGap = inside.Zip(inside.Skip(1), (a, b) => b - a).Max();
        Assert.True(maxGap <= 2, $"Salto de {maxGap} códigos de 10 bits.");
        // 876 códigos de entrada: casi todos tienen que seguir distintos (en 8 bits quedarían unos 220).
        Assert.True(distinct >= 600, $"Solo {distinct} códigos distintos.");
    }

    private static YuvImage Scene(int width, int height)
    {
        var random = new Random(3);
        var image = new YuvImage(width, height, false);
        for (int y = 0; y < height; y += 8)
        {
            for (int x = 0; x < width; x += 8)
            {
                image.Fill(x, y, 8, 8, new YuvCode(random.Next(16, 236), 128 + (int)(50 * Math.Sin(x / 60.0)), 128 + (int)(50 * Math.Cos(y / 40.0))));
            }
        }

        return image;
    }
}
