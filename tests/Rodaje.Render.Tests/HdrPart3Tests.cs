using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Export;
using Rodaje.Render.Scopes;
using Rodaje.Render.Tests.Export;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;

namespace Rodaje.Render.Tests;

/// <summary>
/// Fase C del HDR, parte 3 (D-057): «Expandir a HDR» de un clip SDR, LUT y curvas por PQ en una secuencia HDR, la miniatura de
/// exportación y los osciloscopios recortados a SDR como el visor, y MaxCLL/MaxFALL medidos en la composición lineal.
/// </summary>
public sealed class HdrPart3Tests
{
    private static readonly VideoColor Pq2020 = new(YuvMatrix.Bt2020, false) { Transfer = HdrTransfer.Pq, Bt2020Primaries = true };

    // Código Y' limitado de 8 bits del gris del 18 % (BT.709: 0,18 lineal → 0,409) y del parche PQ de 1000 nits (≈ 0,752 → 723 en 10 bits).
    private const int Gray18 = 106;
    private const int Pq1000 = 723;

    [GpuFact]
    public void Expanding_an_sdr_clip_takes_white_to_the_peak_and_keeps_the_18_percent_gray()
    {
        using GpuDevice gpu = GpuDevice.Create();
        var image = new YuvImage(32, 16, false);
        image.Fill(0, 0, 16, 16, new YuvCode(235, 128, 128));
        image.Fill(16, 0, 16, 16, new YuvCode(Gray18, 128, 128));
        var expand = LayerTransform.Identity with { HdrExpandNits = 1000 };
        int[] plain = ExportLuma(gpu, SequenceColorSpace.HdrPq, Layers.From(gpu, image, VideoColor.Bt709Limited));
        int[] expanded = ExportLuma(gpu, SequenceColorSpace.HdrPq, Layers.From(gpu, image, VideoColor.Bt709Limited, expand));

        Assert.InRange(plain[0], 571, 575);
        Assert.InRange(expanded[0], Limited(Pq(1000)) - 4, Limited(Pq(1000)) + 4);
        Assert.InRange(expanded[16], plain[16] - 2, plain[16] + 2);
        Assert.True(expanded[16] < 500, $"El gris del 18 % quedó en {expanded[16]}.");
    }

    [GpuFact]
    public void The_shader_expansion_follows_the_cpu_reference()
    {
        using GpuDevice gpu = GpuDevice.Create();
        // Una rampa de 16 grises SDR por encima del gris del 18 %: cada uno debe salir en el código PQ de la referencia de la CPU.
        var image = new YuvImage(256, 16, false);
        var codes = new int[16];
        for (int i = 0; i < 16; i++)
        {
            codes[i] = 120 + (i * 7);
            image.Fill(i * 16, 0, 16, 16, new YuvCode(codes[i], 128, 128));
        }

        var expand = LayerTransform.Identity with { HdrExpandNits = 600 };
        int[] luma = ExportLuma(gpu, SequenceColorSpace.HdrPq, Layers.From(gpu, image, VideoColor.Bt709Limited, expand), 256);
        for (int i = 0; i < 16; i++)
        {
            double linear = Bt709Linear((codes[i] - 16) / 219.0);
            int expected = Limited(Pq(203 * SdrExpansion.Expand(linear, 600)));
            Assert.InRange(luma[(i * 16) + 8], expected - 3, expected + 3);
        }
    }

    [GpuFact]
    public void An_sdr_sequence_ignores_the_expansion()
    {
        using GpuDevice gpu = GpuDevice.Create();
        var image = new YuvImage(32, 16, false);
        image.Fill(0, 0, 16, 16, new YuvCode(235, 128, 128));
        image.Fill(16, 0, 16, 16, new YuvCode(Gray18, 90, 160));
        byte[] plain = Readback(gpu, SequenceColorSpace.Sdr709, Layers.From(gpu, image, VideoColor.Bt709Limited));
        byte[] expanded = Readback(gpu, SequenceColorSpace.Sdr709, Layers.From(gpu, image, VideoColor.Bt709Limited, LayerTransform.Identity with { HdrExpandNits = 1000 }));
        Assert.Equal(plain, expanded);
    }

    [GpuFact]
    public void A_lut_and_curves_keep_a_1000_nit_patch_in_an_hdr_sequence()
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvImage patch = YuvImage.Solid(16, 16, true, new YuvCode(Pq1000, 512, 512));
        var lut = new LayerLook([Fx.ColorWithLut("identidad", 1)]);
        int[] withLut = ExportLuma(gpu, SequenceColorSpace.HdrPq, Layers.From(gpu, patch, Pq2020, lut), 16,
            c => c.Resources = new FakeResources().Add("identidad", Luts.Identity(17)));
        string key = ToneCurve.Key(new Dictionary<string, string>(StringComparer.Ordinal) { [BuiltInEffects.CurvesOptions.Master] = "0,0;0.5,0.5;1,1" });
        var curves = new LayerLook([new LayerEffect(EffectRegistry.Find(BuiltInEffects.Curves)!.Code, default, new ResourceKey(key))]);
        int[] withCurves = ExportLuma(gpu, SequenceColorSpace.HdrPq, Layers.From(gpu, patch, Pq2020, curves), 16);

        // Antes de la parte 3 la LUT y las curvas tomaban el borde de la tabla: el parche salía en el código de 203 nits (≈ 573).
        Assert.InRange(withLut[0], Pq1000 - 8, Pq1000 + 8);
        Assert.InRange(withCurves[0], Pq1000 - 8, Pq1000 + 8);
    }

    [Fact]
    public void The_export_thumbnail_of_an_hdr_composition_is_clipped_to_sdr_like_the_viewer()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        double peak = 1000.0 / 203;
        var source = new Picture(64, 16).Fill(0, 0, 16, 16, peak, peak, peak).Fill(16, 0, 16, 16, 1, 1, 1).Fill(32, 0, 16, 16, 0.5, 0.5, 0.5);
        var format = new ExportVideoFormat(64, 16, Rational.Fps30, ExportPixelFormat.P010, ExportColor.Hdr10);
        using ExportFrameConverter converter = gpu.Converter(64, 16, format, dither: false, source: SequenceColorSpace.HdrPq);
        nint texture = gpu.Upload(source);
        try
        {
            converter.Submit(texture, 0);
            ExportCheck.ReadOne(converter, 0);
        }
        finally
        {
            ExportGpu.Release(texture);
        }

        ExportThumbnail thumbnail = Assert.IsType<ExportThumbnail>(converter.LatestThumbnail);
        Assert.Equal(64, thumbnail.Width);
        Assert.Equal(16, thumbnail.Height);
        int gray = (int)Math.Round(255 * ((1.099 * Math.Pow(0.5, 0.45)) - 0.099));
        Assert.Equal(255, thumbnail.Bgra[(8 * 4) + 2]);
        Assert.Equal(255, thumbnail.Bgra[(24 * 4) + 2]);
        Assert.InRange(thumbnail.Bgra[(40 * 4) + 1], gray - 3, gray + 3);
        Assert.Equal(0, thumbnail.Bgra[(56 * 4) + 2]);
    }

    [GpuFact]
    public void The_light_meter_reports_maxcll_and_maxfall_in_nits()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 32, 16) { ColorSpace = SequenceColorSpace.HdrPq };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using var meter = new ContentLightMeter(gpu, 32, 16);
        var half = new YuvImage(32, 16, true);
        half.Fill(0, 0, 16, 16, new YuvCode(Pq1000, 512, 512));
        half.Fill(16, 0, 16, 16, new YuvCode(64, 512, 512));
        using (CompositionFrame frame = Layers.Frame(32, 16, Layers.From(gpu, half, Pq2020)))
        {
            compositor.RenderStrict(frame);
        }

        meter.Measure(compositor.ShaderResourceView);
        ContentLight first = meter.Result;
        using (CompositionFrame frame = Layers.Frame(32, 16, Layers.From(gpu, YuvImage.Solid(32, 16, true, new YuvCode(64, 512, 512)), Pq2020)))
        {
            compositor.RenderStrict(frame);
        }

        meter.Measure(compositor.ShaderResourceView);

        // El código 723 son 1004 nits; la mitad del cuadro a ese nivel y la otra en negro dan una media de 502.
        Assert.InRange(first.MaxCll, 995, 1012);
        Assert.InRange(first.MaxFall, 495, 508);
        Assert.Equal(first, meter.Result);
        Assert.Equal(2, meter.Frames);
    }

    [GpuFact]
    public void The_scopes_measure_an_hdr_composition_clipped_to_sdr()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 48, 16) { ColorSpace = SequenceColorSpace.HdrPq };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        var image = new YuvImage(48, 16, true);
        image.Fill(0, 0, 16, 16, new YuvCode(Pq1000, 512, 512));
        image.Fill(16, 0, 16, 16, new YuvCode(573, 512, 512));
        image.Fill(32, 0, 16, 16, new YuvCode(450, 512, 512));
        using (CompositionFrame frame = Layers.Frame(48, 16, Layers.From(gpu, image, Pq2020)))
        {
            compositor.RenderStrict(frame);
        }

        using var scopes = new ScopeRenderer(gpu);
        ScopeCounts counts = scopes.MeasureNow(compositor);

        // 1000 y 203 nits se recortan al blanco (255); 50 nits (código 450) son 0,247 lineal → 124 en BT.709.
        Assert.Equal(512u, counts.Histogram(3, 255));
        uint near = 0;
        for (int level = 120; level <= 128; level++)
        {
            near += counts.Histogram(3, level);
        }

        Assert.Equal(256u, near);
    }

    private static int[] ExportLuma(GpuDevice gpu, SequenceColorSpace space, LayerFrame layer, int width = 32, Action<Compositor>? setup = null)
    {
        using var compositor = new Compositor(gpu, width, 16) { ColorSpace = space };
        setup?.Invoke(compositor);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using CompositionFrame frame = Layers.Frame(width, 16, layer);
        Assert.Equal(0, compositor.RenderStrict(frame).EffectsSkipped);
        var format = new ExportVideoFormat(width, 16, Rational.Fps30, ExportPixelFormat.P010, ExportColor.Hdr10);
        using var converter = new ExportFrameConverter(gpu, width, 16, format, source: space) { Dither = false };
        converter.Submit(compositor.Texture, 0);
        return ExportCheck.ReadOne(converter, 0)[0];
    }

    private static byte[] Readback(GpuDevice gpu, SequenceColorSpace space, LayerFrame layer)
    {
        using var compositor = new Compositor(gpu, 32, 16) { ColorSpace = space };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using CompositionFrame frame = Layers.Frame(32, 16, layer);
        compositor.RenderStrict(frame);
        return compositor.ReadbackRgba();
    }

    private static int Limited(double signal) => (int)Math.Round(64 + (876 * signal));

    private static double Bt709Linear(double x) => x < 0.081 ? x / 4.5 : Math.Pow((x + 0.099) / 1.099, 1 / 0.45);

    private static double Pq(double nits)
    {
        double y = Math.Pow(nits / 10000, 0.1593017578125);
        return Math.Pow((0.8359375 + (18.8515625 * y)) / (1 + (18.6875 * y)), 78.84375);
    }
}
