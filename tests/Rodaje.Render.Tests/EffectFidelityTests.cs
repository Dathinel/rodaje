using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;

namespace Rodaje.Render.Tests;

/// <summary>Cada efecto en la GPU frente a su referencia en la CPU (criterio de la fase 5: ≤2/255; LUT identidad ≤1/255).</summary>
public sealed class EffectFidelityTests
{
    private const int Block = 64;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    private static readonly (int R, int G, int B)[] Palette =
    [
        (20, 20, 20), (235, 235, 235), (200, 40, 30), (40, 190, 60), (30, 60, 210), (128, 128, 128), (230, 200, 60), (90, 40, 160),
    ];

    public static TheoryData<string, double> ColorCases() => new()
    {
        { C.Exposure, 0.8 }, { C.Exposure, -1.5 },
        { C.Contrast, 0.6 }, { C.Contrast, -0.7 },
        { C.Saturation, 0.9 }, { C.Saturation, -1 },
        { C.Temperature, 0.8 }, { C.Temperature, -0.6 },
        { C.Tint, 0.7 }, { C.Tint, -0.9 },
        { C.Highlights, 0.8 }, { C.Highlights, -1 },
        { C.Shadows, 1 }, { C.Shadows, -0.6 },
    };

    [GpuTheory]
    [MemberData(nameof(ColorCases))]
    public void Each_color_control_matches_the_reference(string parameter, double value)
    {
        double P(string name) => name == parameter ? value : 0;
        AssertPalette(
            new LayerLook([Fx.Color((parameter, value))]),
            c => EffectReference.Grade(c, P(C.Exposure), P(C.Contrast), P(C.Saturation), P(C.Temperature), P(C.Tint), P(C.Highlights), P(C.Shadows)));
    }

    [GpuFact]
    public void All_color_controls_together_match_the_reference()
    {
        AssertPalette(
            new LayerLook([Fx.Color((C.Exposure, 0.3), (C.Contrast, 0.2), (C.Saturation, -0.3), (C.Temperature, 0.4), (C.Tint, -0.2),
                (C.Highlights, -0.4), (C.Shadows, 0.3))]),
            c => EffectReference.Grade(c, 0.3, 0.2, -0.3, 0.4, -0.2, -0.4, 0.3));
    }

    [GpuTheory]
    [InlineData(2)]
    [InlineData(17)]
    [InlineData(33)]
    [InlineData(65)]
    public void An_identity_lut_leaves_the_image_within_one_code(int size)
    {
        var resources = new FakeResources().Add("prueba@1/identidad", Luts.Identity(size));
        AssertPalette(new LayerLook([Fx.ColorWithLut("prueba@1/identidad", 1)]), c => c, tolerance: 1, resources: resources);
    }

    [GpuTheory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void A_curved_lut_matches_trilinear_interpolation(double intensity)
    {
        LutData lut = Luts.Create(17, (r, g, b) => (Math.Sqrt(r), g * g, 1 - (0.6 * b) + (0.2 * r * g)));
        var resources = new FakeResources().Add("prueba@1/curva", lut);
        AssertPalette(new LayerLook([Fx.ColorWithLut("prueba@1/curva", intensity)]), c => EffectReference.Lut(c, lut, intensity),
            resources: resources);
    }

    [GpuFact]
    public void A_lut_that_is_not_loaded_is_counted_and_the_layer_goes_without_it()
    {
        var resources = new FakeResources();
        CompositeResult result = AssertPalette(new LayerLook([Fx.Make(BuiltInEffects.Color, new ResourceKey("falta@1/lut"),
            (C.Exposure, 0.5))]), c => EffectReference.Grade(c, 0.5, 0, 0, 0, 0, 0, 0), resources: resources);

        Assert.Equal(1, result.MissingContent);
        Assert.Equal(1, result.EffectsApplied);
        Assert.Equal(1, resources.Requests);
    }

    [GpuTheory]
    [InlineData(0.7, 0.3, 0.4)]
    [InlineData(1.0, 0.0, 1.0)]
    [InlineData(0.4, 0.6, 0.05)]
    public void Vignette_matches_the_reference_across_the_frame(double amount, double size, double softness)
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvCode gray = ReferenceColor.Encode(180, 170, 160, Bt709, false);
        C3 expectedBase = C3.From(ReferenceColor.Decode(gray, Bt709, false));
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(256, 128, false, gray), Bt709, new LayerLook([Fx.Vignette(amount, size, softness)]));
        RgbaImage image = Compose(gpu, 256, 128, out _, null, layer);

        for (int y = 1; y < 128; y += 9)
        {
            for (int x = 1; x < 256; x += 11)
            {
                double gain = EffectReference.Vignette((x + 0.5) / 256, (y + 0.5) / 128, amount, size, softness);
                image.AssertColor(x, y, (expectedBase * gain).To255());
            }
        }
    }

    [GpuTheory]
    [InlineData(3.0)]
    [InlineData(9.0)]
    [InlineData(30.0)]
    [InlineData(100.0)]
    public void Blur_matches_the_reference_pyramid(double radius)
    {
        AssertSpatial(new LayerLook([Fx.Blur(radius)]), source => source.Blur(radius / 3));
    }

    [GpuTheory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.5, 3.0)]
    [InlineData(0.8, 6.0)]
    public void Sharpen_matches_the_unsharp_mask_reference(double amount, double radius)
    {
        // En la GPU real se exige ±2. WARP (CI de Windows) ya decodifica el origen con hasta ~1,5/255 de error (la GPU
        // real, 0,6) y el realce multiplica ese error por (1 + cantidad); por eso solo ahí se admite ±2 × (1 + cantidad).
        AssertSpatial(new LayerLook([Fx.Sharpen(amount, radius)]), source => source.Sharpen(amount, radius), softwareExtraTolerance: 2 * amount);
    }

    [GpuFact]
    public void Blur_then_sharpen_run_in_the_order_of_the_stack()
    {
        AssertSpatial(new LayerLook([Fx.Blur(6), Fx.Sharpen(1.5, 2)]), source => source.Blur(2).Sharpen(1.5, 2));
    }

    public static TheoryData<BlendMode, double> BlendCases()
    {
        var data = new TheoryData<BlendMode, double>();
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            data.Add(mode, 1);
            data.Add(mode, 0.6);
        }

        return data;
    }

    /// <summary>Los 13 modos sobre 8×8 combinaciones de fondo y capa, opacos y con opacidad 0,6.</summary>
    [GpuTheory]
    [MemberData(nameof(BlendCases))]
    public void Every_blend_mode_matches_the_w3c_formula(BlendMode mode, double opacity)
    {
        using GpuDevice gpu = GpuDevice.Create();
        (int R, int G, int B)[] top =
        [
            (0, 0, 0), (255, 255, 255), (64, 128, 192), (250, 20, 128), (128, 128, 128), (30, 200, 240), (190, 60, 10), (100, 220, 90),
        ];
        int size = Block * 8;
        var bottomImage = new YuvImage(size, size, false);
        var topImage = new YuvImage(size, size, false);
        for (int i = 0; i < 8; i++)
        {
            bottomImage.Fill(i * Block, 0, Block, size, Encode(Palette[i]));
            topImage.Fill(0, i * Block, size, Block, Encode(top[i]));
        }

        LayerFrame bottom = Layers.From(gpu, bottomImage, Bt709, track: 0);
        LayerFrame layer = Layers.From(gpu, topImage, Bt709, new LayerLook(Blend: mode), LayerTransform.Identity with { Opacity = opacity }, 1);
        RgbaImage image = Compose(gpu, size, size, out CompositeResult result, null, bottom, layer);

        Assert.Equal(mode == BlendMode.Normal ? 0 : 1, result.EffectsApplied);
        for (int column = 0; column < 8; column++)
        {
            for (int row = 0; row < 8; row++)
            {
                C3 cb = Decode(Palette[column]);
                C3 cs = Decode(top[row]);
                image.AssertColor((column * Block) + 32, (row * Block) + 32, EffectReference.Blend(mode, cb, cs, opacity).To255());
            }
        }
    }

    [GpuFact]
    public void A_shader_blend_on_a_moved_layer_reads_the_background_under_it()
    {
        using GpuDevice gpu = GpuDevice.Create();
        int size = Block * 8;
        var bottomImage = new YuvImage(size, size, false);
        for (int i = 0; i < 8; i++)
        {
            bottomImage.Fill(i * Block, 0, Block, size, Encode(Palette[i]));
        }

        (int, int, int) topColor = (200, 120, 60);
        LayerFrame bottom = Layers.From(gpu, bottomImage, Bt709, track: 0);
        // Un cuadrado de 128 px centrado en (320, 192): cubre las columnas 4 y 5.
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(128, 128, false, Encode(topColor)), Bt709, new LayerLook(Blend: BlendMode.Difference),
            new LayerTransform(64, -64, 128.0 / size, 0, 1), 1);
        RgbaImage image = Compose(gpu, size, size, out _, null, bottom, layer);

        foreach (int column in new[] { 4, 5 })
        {
            image.AssertColor((column * Block) + 32, 192, EffectReference.Blend(BlendMode.Difference, Decode(Palette[column]), Decode(topColor), 1).To255());
        }

        image.AssertColor(96, 192, Decode(Palette[1]).To255());
        image.AssertColor(352, 400, Decode(Palette[5]).To255());
    }

    /// <summary>La vista previa a la mitad se parece a la completa reducida: radios y posiciones están en píxeles de la secuencia.</summary>
    [GpuFact]
    public void Half_resolution_matches_the_full_one_reduced()
    {
        using GpuDevice gpu = GpuDevice.Create();
        byte[] full = HalfScene(gpu, 512, 256);
        byte[] half = HalfScene(gpu, 256, 128);

        double total = 0;
        for (int y = 0; y < 128; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int at(int fx, int fy) => full[(((fy * 512) + fx) * 4) + c];
                    double reduced = (at(2 * x, 2 * y) + at((2 * x) + 1, 2 * y) + at(2 * x, (2 * y) + 1) + at((2 * x) + 1, (2 * y) + 1)) / 4.0;
                    total += Math.Abs(reduced - half[(((y * 256) + x) * 4) + c]);
                }
            }
        }

        double mean = total / (256 * 128 * 3);
        Assert.True(mean <= 3, $"Diferencia media {mean:0.00}/255.");
    }

    private static byte[] HalfScene(GpuDevice gpu, int width, int height)
    {
        // Luma con detalle de 8 px y croma suave, como un video real: con croma al azar por bloque, el submuestreo 4:2:0
        // ya separa media y completa en los bordes, con o sin efectos.
        var random = new Random(11);
        var bottomImage = new YuvImage(512, 256, false);
        var topImage = new YuvImage(256, 256, false);
        for (int y = 0; y < 256; y += 8)
        {
            for (int x = 0; x < 512; x += 8)
            {
                var code = new YuvCode(random.Next(16, 236), 128 + (int)(60 * Math.Sin(x / 80.0)), 128 + (int)(60 * Math.Cos(y / 50.0)));
                bottomImage.Fill(x, y, 8, 8, code);
                if (x < 256)
                {
                    topImage.Fill(x, y, 8, 8, code with { Y = random.Next(16, 236), Cb = 256 - code.Cr });
                }
            }
        }

        LayerFrame bottom = Layers.From(gpu, bottomImage, Bt709,
            new LayerLook([Fx.Color((C.Contrast, 0.3), (C.Saturation, 0.4)), Fx.Vignette(0.6, 0.4, 0.5)]));
        LayerFrame top = Layers.From(gpu, topImage, Bt709, new LayerLook([Fx.Blur(12), Fx.Sharpen(0.5, 2)], BlendMode.Overlay),
            new LayerTransform(60, -20, 0.6, 8, 0.9), 1);
        using var compositor = new Compositor(gpu, width, height);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using CompositionFrame frame = Layers.Frame(512, 256, bottom, top);
        Assert.Equal(0, compositor.Render(frame).EffectsSkipped);
        return compositor.ReadbackRgba();
    }

    private static YuvCode Encode((int R, int G, int B) c) => ReferenceColor.Encode(c.R, c.G, c.B, Bt709, false);

    private static C3 Decode((int R, int G, int B) c) => C3.From(ReferenceColor.Decode(Encode(c), Bt709, false));

    /// <summary>Paleta de 8 bloques con <paramref name="look"/>; compara el centro de cada bloque con la referencia.</summary>
    private static CompositeResult AssertPalette(LayerLook look, Func<C3, C3> reference, double tolerance = 2, IEffectResources? resources = null)
    {
        using GpuDevice gpu = GpuDevice.Create();
        var source = new YuvImage(Block * Palette.Length, Block, false);
        for (int i = 0; i < Palette.Length; i++)
        {
            source.Fill(i * Block, 0, Block, Block, Encode(Palette[i]));
        }

        LayerFrame layer = Layers.From(gpu, source, Bt709, look);
        RgbaImage image = Compose(gpu, source.Width, Block, out CompositeResult result, resources, layer);
        for (int i = 0; i < Palette.Length; i++)
        {
            Rgb expected = reference(Decode(Palette[i])).To255();
            image.AssertColor((i * Block) + 32, 32, expected, tolerance);
            image.AssertColor((i * Block) + 4, 60, expected, tolerance);
        }

        return result;
    }

    /// <summary>Ruido de luma con croma fija (exacta en 4:2:0); compara todos los píxeles con la referencia.</summary>
    private static void AssertSpatial(LayerLook look, Func<FloatImage, FloatImage> reference, double softwareExtraTolerance = 0)
    {
        const int width = 128;
        const int height = 96;
        var random = new Random(5);
        var source = new YuvImage(width, height, false);
        var codes = new YuvCode[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Bloques de 4 px con valores al azar y un borde fuerte, para que haya bordes de todos los tamaños.
                int luma = x < 40 ? 30 : (((x / 4) + (y / 4)) % 3 == 0 ? 230 : random.Next(16, 236));
                codes[(y * width) + x] = new YuvCode(luma, 110, 150);
                source.Fill(x, y, 1, 1, codes[(y * width) + x]);
            }
        }

        FloatImage decoded = FloatImage.FromColors(width, height,
            (x, y) => C3.From(ReferenceColor.Decode(codes[(y * width) + x], Bt709, false)));
        FloatImage expected = reference(decoded);

        using GpuDevice gpu = GpuDevice.Create();
        bool software = gpu.AdapterName.Contains("Basic Render Driver", StringComparison.OrdinalIgnoreCase)
            || gpu.AdapterName.Contains("WARP", StringComparison.OrdinalIgnoreCase);
        double tolerance = 2 + (software ? softwareExtraTolerance : 0);
        RgbaImage image = Compose(gpu, width, height, out CompositeResult result, null, Layers.From(gpu, source, Bt709, look));
        Assert.Equal(0, result.EffectsSkipped);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (C3 color, _) = expected.Sample((x + 0.5) / width, (y + 0.5) / height);
                image.AssertColor(x, y, color.Map(EffectReference.Saturate).To255(), tolerance);
            }
        }
    }

    internal static RgbaImage Compose(GpuDevice gpu, int width, int height, out CompositeResult result, IEffectResources? resources,
        params LayerFrame[] layers)
    {
        using var compositor = new Compositor(gpu, width, height);
        compositor.Resources = resources;
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)), "Los shaders de efectos no estuvieron listos.");
        using CompositionFrame frame = Layers.Frame(width, height, layers);
        result = compositor.Render(frame);
        return new RgbaImage(compositor.ReadbackRgba(), width, height);
    }
}
