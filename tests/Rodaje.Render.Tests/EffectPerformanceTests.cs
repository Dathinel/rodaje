using System.Globalization;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;

namespace Rodaje.Render.Tests;

/// <summary>
/// Criterios de rendimiento de la fase 5 con marcas de tiempo de la GPU (300 cuadros tras 30 de calentamiento). Los
/// objetivos son de la RTX 3050 Laptop: solo se exigen con <c>RODAJE_PRUEBAS_LARGAS=1</c>; sin ella, solo se informan
/// y se detecta una regresión grave.
/// </summary>
public sealed class EffectPerformanceTests(ITestOutputHelper output)
{
    private const int Warmup = 30;
    private const int Measured = 300;
    private const string Lut = "prueba@1/look";
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;
    private static readonly bool Strict = Environment.GetEnvironmentVariable(Skip.LongVariable) == "1";

    [GpuFact]
    public void Eight_plain_1080p_layers_do_not_regress()
    {
        using GpuDevice gpu = GpuDevice.Create();
        nint texture = Texture(gpu, 1920, 1088, 8);
        try
        {
            var layers = new LayerFrame[8];
            for (int i = 0; i < 8; i++)
            {
                layers[i] = Layers.FromTexture(gpu, texture, i, 1920, 1080, Bt709, LayerTransform.Identity with { Opacity = i == 0 ? 1 : 0.85 }, i);
            }

            (double median, _) = Measure(gpu, "8 capas 1080p sin efectos", 1920, 1080, 1920, 1080, layers);
            Require(median <= 0.6, $"8 capas 1080p: {median:0.000} ms (objetivo ≤0,6).");
        }
        finally
        {
            Layers.Release(texture);
        }
    }

    [GpuFact]
    public void One_4k_layer_with_color_and_lut()
    {
        using GpuDevice gpu = GpuDevice.Create();
        nint texture = Texture(gpu, 3840, 2160, 1);
        try
        {
            LayerFrame layer = Layers.FromTexture(gpu, texture, 0, 3840, 2160, Bt709, LayerTransform.Identity, 0, new LayerLook([Graded()]));
            (double median, _) = Measure(gpu, "1 capa 4K con color y LUT", 3840, 2160, 3840, 2160, layer);
            Require(median <= 1.0, $"Una capa 4K con color y LUT: {median:0.000} ms (objetivo ≤1,0).");
        }
        finally
        {
            Layers.Release(texture);
        }
    }

    [GpuFact]
    public void Three_4k_layers_with_effects_and_half_resolution()
    {
        using GpuDevice gpu = GpuDevice.Create();
        nint texture = Texture(gpu, 3840, 2160, 3);
        try
        {
            (double fullMedian, double fullP95) = Measure(gpu, "3 capas 4K con color, LUT, desenfoque y superponer", 3840, 2160, 3840, 2160,
                ThreeLayers(gpu, texture));
            (double halfMedian, _) = Measure(gpu, "Las mismas 3 capas a media resolución", 1920, 1080, 3840, 2160, ThreeLayers(gpu, texture));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Media resolución: {100 * halfMedian / fullMedian:0} % de la completa."));
            Require(fullMedian <= 6 && fullP95 <= 8, $"3 capas 4K: mediana {fullMedian:0.00} ms, p95 {fullP95:0.00} ms (objetivo ≤6 y ≤8).");
            Require(halfMedian <= 0.4 * fullMedian, $"Media resolución: {halfMedian:0.00} ms frente a {fullMedian:0.00} ms (objetivo ≤40 %).");
        }
        finally
        {
            Layers.Release(texture);
        }
    }

    [GpuTheory]
    [InlineData(BuiltInTransitions.Crossfade)]
    [InlineData(BuiltInTransitions.Wipe)]
    [InlineData(BuiltInTransitions.ZoomDissolve)]
    [InlineData(BuiltInTransitions.BlurDissolve)]
    public void A_4k_transition_with_effects(string type)
    {
        using GpuDevice gpu = GpuDevice.Create();
        nint texture = Texture(gpu, 3840, 2160, 2);
        try
        {
            LayerFrame incoming = Layers.FromTexture(gpu, texture, 1, 3840, 2160, Bt709, LayerTransform.Identity, 1, new LayerLook([Graded()]));
            var transition = new LayerTransition(EffectRegistry.Find(type)!.Code, 0.5f, Fx.Transition(type), incoming);
            LayerFrame outgoing = Layers.FromTexture(gpu, texture, 0, 3840, 2160, Bt709, LayerTransform.Identity, 0,
                new LayerLook([Graded()], Transition: transition));
            (double median, _) = Measure(gpu, $"Transición 4K {type} con color y LUT", 3840, 2160, 3840, 2160, outgoing);
            Require(median <= 5, $"Transición 4K {type}: {median:0.00} ms (objetivo ≤5).");
        }
        finally
        {
            Layers.Release(texture);
        }
    }

    private static LayerEffect Graded() => Fx.Make(BuiltInEffects.Color, new ResourceKey(Lut), (C.Exposure, 0.2), (C.Contrast, 0.15),
        (C.Saturation, 0.2), (C.Temperature, 0.1), (C.Highlights, -0.2), (C.Shadows, 0.1), (C.LutIntensity, 0.8));

    private static LayerFrame[] ThreeLayers(GpuDevice gpu, nint texture) =>
    [
        Layers.FromTexture(gpu, texture, 0, 3840, 2160, Bt709, LayerTransform.Identity, 0, new LayerLook([Graded()])),
        Layers.FromTexture(gpu, texture, 1, 3840, 2160, Bt709, LayerTransform.Identity with { Opacity = 0.9 }, 1,
            new LayerLook([Graded(), Fx.Blur(20)])),
        Layers.FromTexture(gpu, texture, 2, 3840, 2160, Bt709, LayerTransform.Identity, 2, new LayerLook([Graded()], BlendMode.Overlay)),
    ];

    private static void Require(bool condition, string message)
    {
        if (Strict)
        {
            Assert.True(condition, message);
        }
    }

    private static nint Texture(GpuDevice gpu, int width, int height, int slices)
    {
        var images = new YuvImage[slices];
        for (int s = 0; s < slices; s++)
        {
            images[s] = new YuvImage(width, height, tenBit: false);
            int band = width / 16;
            for (int b = 0; b < 16; b++)
            {
                images[s].Fill(b * band, 0, band, height, new YuvCode(16 + (((b * 13) + (s * 7)) % 219), 64 + (s * 16), 192 - (b * 8)));
            }
        }

        return YuvImage.CreateTexture(gpu, images);
    }

    private (double Median, double P95) Measure(GpuDevice gpu, string name, int width, int height, int sequenceWidth, int sequenceHeight,
        params LayerFrame[] layers)
    {
        using var compositor = new Compositor(gpu, width, height);
        compositor.Resources = new FakeResources().Add(Lut, Luts.Create(33, (r, g, b) => (Math.Sqrt(r), g, b * b)));
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using var timer = new GpuTimer(gpu);
        using var frame = new CompositionFrame(Rodaje.Core.Timing.Time.Zero, sequenceWidth, sequenceHeight, layers);
        var times = new List<double>(Measured);
        CompositeResult result = default;
        for (int n = 0; n < Warmup + Measured; n++)
        {
            timer.Begin();
            result = compositor.Render(frame);
            timer.End();
            if (timer.WaitMilliseconds(TimeSpan.FromSeconds(2)) is { } ms && n >= Warmup)
            {
                times.Add(ms);
            }
        }

        Assert.True(times.Count >= Measured / 2, "Demasiadas mediciones disjuntas de la GPU.");
        Assert.Equal(0, result.EffectsSkipped);
        Assert.True(result.PoolBytes <= compositor.PoolCapBytes);
        times.Sort();
        double median = times[times.Count / 2];
        double p95 = times[(int)Math.Round(0.95 * (times.Count - 1))];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name} ({gpu.AdapterName}, salida {width}×{height}): mediana {median:0.000} ms, p95 {p95:0.000} ms, pool {result.PoolBytes / (1024 * 1024)} MB"));
        Assert.True(median < 16, $"{name}: {median:0.00} ms en la GPU.");
        return (median, p95);
    }
}
