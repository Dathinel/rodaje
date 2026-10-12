using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using Bl = Rodaje.Core.Model.BuiltInEffects.BlurParameters;
using Dn = Rodaje.Core.Model.BuiltInEffects.DenoiseParameters;
using Gr = Rodaje.Core.Model.BuiltInEffects.GrainParameters;
using Mk = Rodaje.Core.Model.BuiltInEffects.MaskParameters;

namespace Rodaje.Render.Tests;

/// <summary>
/// Los efectos del intermedio en la GPU frente a su referencia en la CPU, píxel a píxel (≤2/255): grano, resplandor,
/// aberración, espejo, distorsión, desenfoques de lente, direccional y radial, nitidez con umbral, reducción de ruido
/// (espacial y con el cuadro anterior) y la zona de las máscaras que limitan efectos.
/// </summary>
public sealed class ImageEffectTests
{
    private const int Width = 128;
    private const int Height = 96;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    [GpuTheory]
    [InlineData(0.5, 1.5, 0.0, 0.6, 0.25)]
    [InlineData(0.8, 3.0, 1.0, 0.0, 3.5)]
    public void Grain_matches_the_reference(double amount, double size, double color, double response, double seconds)
    {
        AssertSpatial(Fx.Make(BuiltInEffects.Grain, null, (Gr.Amount, amount), (Gr.Size, size), (Gr.Color, color), (Gr.Response, response)).At(seconds),
            s => FxReference.Grain(s, amount, size, color, response, seconds), outliers: 0.002);
    }

    [GpuFact]
    public void Grain_changes_every_frame_and_repeats_for_the_same_one()
    {
        using GpuDevice gpu = GpuDevice.Create();
        LayerEffect grain = Fx.Make(BuiltInEffects.Grain, null, (Gr.Amount, 0.6));
        byte[] first = Render(gpu, grain.At(1.0));
        byte[] again = Render(gpu, grain.At(1.0));
        byte[] next = Render(gpu, grain.At(1.04));
        Assert.Equal(first, again);
        int different = 0;
        for (int i = 0; i < first.Length; i += 4)
        {
            different += first[i] != next[i] ? 1 : 0;
        }

        Assert.True(different > first.Length / 4 / 2, $"Solo cambiaron {different} píxeles entre cuadros.");
    }

    [GpuTheory]
    [InlineData(0.6, 8.0, 1.0)]
    [InlineData(0.3, 30.0, 2.5)]
    public void Glow_matches_the_reference(double threshold, double radius, double intensity)
    {
        AssertSpatial(Fx.Make(BuiltInEffects.Glow, null, (BuiltInEffects.GlowParameters.Threshold, threshold),
            (BuiltInEffects.GlowParameters.Radius, radius), (BuiltInEffects.GlowParameters.Intensity, intensity)),
            s => FxReference.Glow(s, threshold, radius, intensity));
    }

    [GpuTheory]
    [InlineData("radial", 6.0, 0.0)]
    [InlineData("lateral", 4.0, 30.0)]
    public void Chromatic_aberration_matches_the_reference(string mode, double amount, double angle)
    {
        AssertSpatial(Fx.Make(BuiltInEffects.ChromaticAberration, null, (BuiltInEffects.ChromaticAberrationParameters.Amount, amount),
            (BuiltInEffects.ChromaticAberrationParameters.Angle, angle)).Choose(BuiltInEffects.ChromaticAberrationOptions.Mode, mode),
            s => FxReference.Aberration(s, mode == "lateral", amount, angle));
    }

    [GpuTheory]
    [InlineData("left", 0.5, 2.0)]
    [InlineData("right", 0.3, 2.0)]
    [InlineData("top", 0.5, 2.0)]
    [InlineData("bottom", 0.6, 2.0)]
    [InlineData("quad", 0.5, 2.0)]
    [InlineData("tiles", 0.5, 3.0)]
    public void Mirror_matches_the_reference(string mode, double axis, double tiles)
    {
        AssertSpatial(Fx.Make(BuiltInEffects.Mirror, null, (BuiltInEffects.MirrorParameters.Axis, axis), (BuiltInEffects.MirrorParameters.Tiles, tiles))
            .Choose(BuiltInEffects.MirrorOptions.Mode, mode),
            s => FxReference.Mirror(s, EffectRegistry.MirrorModes.IndexOf(mode), axis, tiles));
    }

    [GpuTheory]
    [InlineData(0.6, 1.0)]
    [InlineData(-0.5, 1.2)]
    public void Lens_distortion_matches_the_reference(double amount, double zoom)
    {
        AssertSpatial(Fx.Make(BuiltInEffects.LensDistortion, null, (BuiltInEffects.LensDistortionParameters.Amount, amount),
            (BuiltInEffects.LensDistortionParameters.Zoom, zoom)), s => FxReference.Distortion(s, amount, zoom));
    }

    [GpuTheory]
    [InlineData(6.0, 0.0)]
    [InlineData(20.0, 35.0)]
    [InlineData(70.0, 90.0)]
    public void Directional_blur_matches_the_reference(double radius, double angle)
    {
        AssertSpatial(Fx.Make(BuiltInEffects.Blur, null, (Bl.Radius, radius), (Bl.Angle, angle)).Choose(BuiltInEffects.BlurOptions.Kind, "directional"),
            s => FxReference.LineBlur(s, radius, angle));
    }

    [GpuTheory]
    [InlineData(8.0, 0.5, 0.5)]
    [InlineData(25.0, 0.3, 0.6)]
    public void Radial_blur_matches_the_reference(double radius, double centerX, double centerY)
    {
        AssertSpatial(Fx.Make(BuiltInEffects.Blur, null, (Bl.Radius, radius), (Bl.CenterX, centerX), (Bl.CenterY, centerY))
            .Choose(BuiltInEffects.BlurOptions.Kind, "radial"), s => FxReference.ZoomBlur(s, radius, centerX, centerY));
    }

    [GpuTheory]
    [InlineData(3.0)]
    [InlineData(24.0)]
    public void Lens_blur_matches_the_reference(double radius)
    {
        AssertSpatial(Fx.Make(BuiltInEffects.Blur, null, (Bl.Radius, radius)).Choose(BuiltInEffects.BlurOptions.Kind, "lens"),
            s => FxReference.LensBlur(s, radius));
    }

    [GpuTheory]
    [InlineData(1.5, 2.0, 0.05)]
    [InlineData(2.5, 1.0, 0.15)]
    public void Sharpen_with_threshold_matches_the_reference(double amount, double radius, double threshold)
    {
        AssertSpatial(Fx.Make(BuiltInEffects.Sharpen, null, (BuiltInEffects.SharpenParameters.Amount, amount),
            (BuiltInEffects.SharpenParameters.Radius, radius), (BuiltInEffects.SharpenParameters.Threshold, threshold)),
            s => FxReference.SharpenWithThreshold(s, amount, radius, threshold));
    }

    [GpuTheory]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 0.1)]
    public void Spatial_denoise_matches_the_bilateral_reference(double strength, double detail)
    {
        AssertSpatial(Fx.Make(BuiltInEffects.Denoise, null, (Dn.Strength, strength), (Dn.Temporal, 0), (Dn.Detail, detail)),
            s => FxReference.Bilateral(s, strength, detail));
    }

    /// <summary>
    /// Con el cuadro siguiente del mismo clip se mezcla con el resultado anterior; el mismo cuadro otra vez da los mismos
    /// bytes, y un salto (otro cuadro no contiguo) vuelve a solo la parte espacial.
    /// </summary>
    [GpuFact]
    public void Temporal_denoise_blends_with_the_previous_frame_only_when_it_is_the_next_one()
    {
        using GpuDevice gpu = GpuDevice.Create();
        LayerEffect denoise = Fx.Make(BuiltInEffects.Denoise, null, (Dn.Strength, 0.4), (Dn.Temporal, 0.8), (Dn.Detail, 0.5));
        (YuvImage first, FloatImage firstDecoded) = Noise(1);
        (YuvImage second, FloatImage secondDecoded) = Noise(2);
        Time frame = Time.FromSeconds(1 / 25.0);
        using var compositor = new Compositor(gpu, Width, Height);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));

        byte[] Draw(YuvImage image, Time pts)
        {
            using CompositionFrame composition = Layers.Frame(Width, Height,
                Layers.From(gpu, image, Bt709, new LayerLook([denoise]), pts: pts, duration: frame));
            Assert.Equal(0, compositor.Render(composition).EffectsSkipped);
            return compositor.ReadbackRgba();
        }

        FloatImage spatialFirst = FxReference.Bilateral(firstDecoded, 0.4, 0.5);
        FloatImage spatialSecond = FxReference.Bilateral(secondDecoded, 0.4, 0.5);
        AssertImage(new RgbaImage(Draw(first, Time.Zero), Width, Height), spatialFirst);

        byte[] blended = Draw(second, frame);
        AssertImage(new RgbaImage(blended, Width, Height), FxReference.Temporal(spatialSecond, spatialFirst, 0.8, 0.5));
        Assert.Equal(blended, Draw(second, frame));

        AssertImage(new RgbaImage(Draw(first, frame + frame + frame), Width, Height), spatialFirst);
    }

    /// <summary>Un desenfoque con zona «dentro» solo cambia lo que cubre la máscara que limita efectos; la capa no se recorta.</summary>
    [GpuFact]
    public void A_region_mask_limits_the_effect_without_cutting_the_layer()
    {
        LayerEffect mask = Fx.Make(BuiltInEffects.Mask, null, (Mk.X, 0.25), (Mk.Y, 0.5), (Mk.Width, 0.5), (Mk.Height, 1))
            .Choose(BuiltInEffects.MaskOptions.Use, "effects");
        LayerEffect blur = Fx.Make(BuiltInEffects.Blur, null, (Bl.Radius, 9)).Choose(BuiltInEffects.RegionOptions.Region, "inside");
        (YuvImage image, FloatImage decoded) = Noise(3);
        FloatImage blurred = decoded.Blur(3);
        using GpuDevice gpu = GpuDevice.Create();
        RgbaImage result = EffectFidelityTests.Compose(gpu, Width, Height, out CompositeResult stats, null,
            Layers.From(gpu, image, Bt709, new LayerLook([mask, blur])));
        Assert.Equal(0, stats.EffectsSkipped);
        for (int y = 2; y < Height - 2; y += 3)
        {
            for (int x = 2; x < Width - 2; x += 3)
            {
                if (Math.Abs(x - (Width / 2)) < 3)
                {
                    continue;
                }

                FloatImage expected = x < Width / 2 ? blurred : decoded;
                (C3 color, _) = expected.Sample((x + 0.5) / Width, (y + 0.5) / Height);
                result.AssertColor(x, y, color.To255());
            }
        }

        LayerEffect outside = blur.Choose(BuiltInEffects.RegionOptions.Region, "outside");
        result = EffectFidelityTests.Compose(gpu, Width, Height, out _, null, Layers.From(gpu, image, Bt709, new LayerLook([mask, outside])));
        (C3 left, _) = decoded.Sample(10.5 / Width, 40.5 / Height);
        (C3 right, _) = blurred.Sample(110.5 / Width, 40.5 / Height);
        result.AssertColor(10, 40, left.To255());
        result.AssertColor(110, 40, right.To255());
    }

    /// <summary>
    /// Un efecto que no cambia nada pasa por tres remuestreos (origen → intermedio → efecto → lienzo) y en WARP debe dar lo
    /// mismo que en la GPU: antes, el recorte del cuadrilátero en el viewport y el redondeo hacia abajo del bilineal de WARP
    /// sumaban hasta 2,5/255 en los bordes fuertes.
    /// </summary>
    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unchanged_pass_through_the_intermediate_is_exact_on_hardware_and_warp(bool warp)
    {
        (YuvImage image, FloatImage decoded) = Noise(5);
        LayerEffect identity = Fx.Make(BuiltInEffects.Mirror, null, (BuiltInEffects.MirrorParameters.Axis, 1.0))
            .Choose(BuiltInEffects.MirrorOptions.Mode, "left");
        using GpuDevice gpu = warp ? GpuDevice.CreateWarp() : GpuDevice.Create();
        foreach (LayerLook look in new[] { new LayerLook([]), new LayerLook([identity]) })
        {
            RgbaImage result = EffectFidelityTests.Compose(gpu, Width, Height, out _, null, Layers.From(gpu, image, Bt709, look));
            AssertImage(result, decoded, tolerance: 1);
        }
    }

    private static byte[] Render(GpuDevice gpu, LayerEffect effect)
    {
        (YuvImage image, _) = Noise(7);
        using var compositor = new Compositor(gpu, Width, Height);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using CompositionFrame frame = Layers.Frame(Width, Height, Layers.From(gpu, image, Bt709, new LayerLook([effect])));
        compositor.Render(frame);
        return compositor.ReadbackRgba();
    }

    /// <summary>
    /// Bloques de 4 px con luma al azar y bordes fuertes, con una croma rojiza fija (la única exacta en 4:2:0: el muestreo
    /// bilineal de la croma mezclaría dos zonas), para que haya detalle, luces que pasen del umbral y canales que se separen.
    /// </summary>
    private static (YuvImage Image, FloatImage Decoded) Noise(int seed)
    {
        var random = new Random(seed);
        var image = new YuvImage(Width, Height, false);
        var codes = new YuvCode[Width * Height];
        for (int y = 0; y < Height; y += 2)
        {
            for (int x = 0; x < Width; x += 2)
            {
                int luma = x < 40 ? 30 : (((x / 4) + (y / 4)) % 3 == 0 ? 230 : random.Next(16, 236));
                var code = new YuvCode(luma, 110, 150);
                image.Fill(x, y, 2, 2, code);
                codes[(y * Width) + x] = code;
                codes[(y * Width) + x + 1] = code;
                codes[((y + 1) * Width) + x] = code;
                codes[((y + 1) * Width) + x + 1] = code;
            }
        }

        return (image, FloatImage.FromColors(Width, Height, (x, y) => C3.From(ReferenceColor.Decode(codes[(y * Width) + x], Bt709, false))));
    }

    private static void AssertSpatial(LayerEffect effect, Func<FloatImage, FloatImage> reference, double outliers = 0)
    {
        (YuvImage image, FloatImage decoded) = Noise(5);
        using GpuDevice gpu = GpuDevice.Create();
        RgbaImage result = EffectFidelityTests.Compose(gpu, Width, Height, out CompositeResult stats, null,
            Layers.From(gpu, image, Bt709, new LayerLook([effect])));
        Assert.Equal(0, stats.EffectsSkipped);
        AssertImage(result, reference(decoded), outliers: outliers);
    }

    /// <summary>Cada píxel contra la referencia compuesta sobre negro; se admite una fracción de píxeles fuera (bordes de celda del grano).</summary>
    private static void AssertImage(RgbaImage result, FloatImage expected, double tolerance = 2, double outliers = 0)
    {
        int bad = 0;
        string? first = null;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                (C3 color, _) = expected.Sample((x + 0.5) / Width, (y + 0.5) / Height);
                Rgb want = color.Map(EffectReference.Saturate).To255();
                (byte r, byte g, byte b, _) = result.At(x, y);
                if (Math.Abs(r - want.R) > tolerance || Math.Abs(g - want.G) > tolerance || Math.Abs(b - want.B) > tolerance)
                {
                    bad++;
                    first ??= $"({x}, {y}) = ({r}, {g}, {b}); se esperaba ({want.R:0.#}, {want.G:0.#}, {want.B:0.#})";
                }
            }
        }

        Assert.True(bad <= outliers * Width * Height, $"{bad} píxeles fuera de ±{tolerance}; el primero {first}.");
    }
}
