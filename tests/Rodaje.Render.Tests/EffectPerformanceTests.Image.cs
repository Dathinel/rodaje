using System.Globalization;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render.Effects;
using Rodaje.Render.Scopes;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using Bl = Rodaje.Core.Model.BuiltInEffects.BlurParameters;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using Q = Rodaje.Core.Model.BuiltInEffects.HslQualifierParameters;

namespace Rodaje.Render.Tests;

/// <summary>
/// Lo que cuesta cada efecto nuevo con una capa 1080p (lo que usa <see cref="EffectCost"/>), seis efectos juntos en 1080p
/// y 4K, y los monitores. Objetivos de la RTX 3050 Laptop, exigidos con <c>RODAJE_PRUEBAS_LARGAS=1</c>.
/// </summary>
public sealed partial class EffectPerformanceTests
{
    public static TheoryData<string, string> NewEffects() => new()
    {
        { BuiltInEffects.HslQualifier, "" },
        { BuiltInEffects.Curves, "hue" },
        { BuiltInEffects.Blur, "gaussian" },
        { BuiltInEffects.Blur, "lens" },
        { BuiltInEffects.Blur, "directional" },
        { BuiltInEffects.Blur, "radial" },
        { BuiltInEffects.Sharpen, "" },
        { BuiltInEffects.Grain, "" },
        { BuiltInEffects.Glow, "" },
        { BuiltInEffects.ChromaticAberration, "" },
        { BuiltInEffects.Denoise, "" },
        { BuiltInEffects.Mirror, "" },
        { BuiltInEffects.LensDistortion, "" },
    };

    /// <summary>El anclaje de esquinas no agrega pasadas: la capa sigue por el camino rápido con una w por esquina.</summary>
    [GpuFact]
    public void A_corner_pinned_1080p_layer_costs_the_same_as_a_plain_one()
    {
        using GpuDevice gpu = GpuDevice.Create();
        nint texture = Texture(gpu, 1920, 1088, 1);
        try
        {
            (double plain, _) = Measure(gpu, "1 capa 1080p", 1920, 1080, 1920, 1080,
                Layers.FromTexture(gpu, texture, 0, 1920, 1080, Bt709, LayerTransform.Identity with { Scale = 0.8 }, 0));
            var pin = new LayerCornerPin(120, 60, -300, 140, -260, -90, 80, -40);
            (double pinned, _) = Measure(gpu, "la misma con anclaje de esquinas", 1920, 1080, 1920, 1080,
                Layers.FromTexture(gpu, texture, 0, 1920, 1080, Bt709, LayerTransform.Identity with { Scale = 0.8, Pin = pin }, 0));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Anclaje de esquinas 1080p: {pinned:0.000} ms frente a {plain:0.000} ms."));
            Require(pinned - plain <= 0.1, $"Anclaje de esquinas: {pinned - plain:0.000} ms más (objetivo ≤0,1).");
        }
        finally
        {
            Layers.Release(texture);
        }
    }

    /// <summary>El costo medido frente al estimado de <see cref="EffectCost"/>: dentro de ×3 (o 0,1 ms).</summary>
    [GpuFact]
    public void Each_new_effect_costs_about_what_the_stack_shows()
    {
        using GpuDevice gpu = GpuDevice.Create();
        nint texture = Texture(gpu, 1920, 1088, 1);
        try
        {
            LayerFrame Plain() => Layers.FromTexture(gpu, texture, 0, 1920, 1080, Bt709, LayerTransform.Identity, 0,
                new LayerLook([Fx.Color((C.Exposure, 0.1))]));
            (double baseline, _) = Measure(gpu, "1 capa 1080p con color", 1920, 1080, 1920, 1080, Plain());
            foreach (object[] row in NewEffects())
            {
                string type = (string)row[0], variant = (string)row[1];
                LayerFrame layer = Layers.FromTexture(gpu, texture, 0, 1920, 1080, Bt709, LayerTransform.Identity, 0,
                    new LayerLook([Fx.Color((C.Exposure, 0.1)), Typical(type, variant)]));
                (double median, _) = Measure(gpu, $"La misma con {type} {variant}", 1920, 1080, 1920, 1080, layer);
                double measured = Math.Max(0, median - baseline);
                EffectCostEstimate estimate = EffectCost.Estimate(type, o => variant.Length > 0 ? variant : null, 1920, 1080);
                double shown = estimate.Milliseconds + (EffectRegistry.Find(type)!.Stage == EffectStage.Spatial ? EffectCost.IntermediateMs1080 : 0);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {type} {variant}: {measured:0.000} ms medidos, {shown:0.000} ms estimados ({estimate.Level})"));
                Require(measured <= Math.Max(0.1, 3 * shown) && shown <= Math.Max(0.1, 3 * measured),
                    $"{type} {variant}: {measured:0.000} ms medidos frente a {shown:0.000} estimados.");
            }
        }
        finally
        {
            Layers.Release(texture);
        }
    }

    [GpuTheory]
    [InlineData(1920, 1080, 4.0)]
    [InlineData(3840, 2160, 12.0)]
    public void Six_new_effects_fit_in_a_frame(int width, int height, double target)
    {
        using GpuDevice gpu = GpuDevice.Create();
        nint texture = Texture(gpu, width, height, 1);
        try
        {
            LayerFrame layer = Layers.FromTexture(gpu, texture, 0, width, height, Bt709, LayerTransform.Identity, 0, new LayerLook(SixEffects()));
            (double median, double p95) = Measure(gpu, $"1 capa {width}×{height} con 6 efectos (color y LUT, calificador, curvas de tono, desenfoque de lente, resplandor, grano)",
                width, height, width, height, layer);
            Require(median <= target, $"6 efectos en {width}×{height}: {median:0.00} ms (objetivo ≤{target}).");
            Assert.True(p95 < 16.7, $"p95 {p95:0.00} ms: no entra en un cuadro a 60 fps.");
        }
        finally
        {
            Layers.Release(texture);
        }
    }

    [GpuTheory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void Scopes_measure_a_frame_in_under_a_millisecond_and_a_half(int width, int height)
    {
        using GpuDevice gpu = GpuDevice.Create();
        nint texture = Texture(gpu, width, height, 1);
        try
        {
            using var compositor = new Compositor(gpu, width, height);
            Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
            using (var frame = new CompositionFrame(Rodaje.Core.Timing.Time.Zero, width, height,
                [Layers.FromTexture(gpu, texture, 0, width, height, Bt709, LayerTransform.Identity, 0)]))
            {
                compositor.Render(frame);
            }

            using var scopes = new ScopeRenderer(gpu);
            Assert.True(scopes.Ready.Wait(TimeSpan.FromSeconds(60)));
            using var timer = new GpuTimer(gpu);
            foreach (ScopeKind kind in Enum.GetValues<ScopeKind>())
            {
                var times = new List<double>();
                for (int n = 0; n < 130; n++)
                {
                    timer.Begin();
                    scopes.Submit(compositor, kind);
                    timer.End();
                    if (timer.WaitMilliseconds(TimeSpan.FromSeconds(2)) is { } ms && n >= 10)
                    {
                        times.Add(ms);
                    }

                    _ = scopes.TryRead();
                }

                times.Sort();
                double median = times[times.Count / 2];
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Monitor {kind} en {width}×{height}: mediana {median:0.000} ms de GPU, {scopes.LastSubmitMilliseconds:0.000} ms de CPU al encolar"));
                Require(median <= 1.5, $"Monitor {kind} en {width}×{height}: {median:0.000} ms (objetivo ≤1,5).");
            }
        }
        finally
        {
            Layers.Release(texture);
        }
    }

    private static LayerEffect[] SixEffects() =>
    [
        Graded(),
        Fx.Make(BuiltInEffects.HslQualifier, null, (Q.HueCenter, 30), (Q.HueShift, 10), (Q.Saturation, 0.2)),
        new LayerEffect(EffectRegistry.Find(BuiltInEffects.Curves)!.Code, default, new ResourceKey("0,0;0.5,0.55;1,1||||0,0.5;0.3,0.45|0.3,0.3;0.6,0.6|")),
        Fx.Make(BuiltInEffects.Blur, null, (Bl.Radius, 12)).Choose(BuiltInEffects.BlurOptions.Kind, "lens"),
        Fx.Make(BuiltInEffects.Glow, null),
        Fx.Make(BuiltInEffects.Grain, null).At(1.5),
    ];

    private static LayerEffect Typical(string type, string variant) => type switch
    {
        BuiltInEffects.HslQualifier => Fx.Make(type, null, (Q.HueShift, 15), (Q.Saturation, 0.3)),
        BuiltInEffects.Curves => new LayerEffect(EffectRegistry.Find(type)!.Code, default, new ResourceKey("||||0,0.5;0.3,0.45|0.3,0.3;0.6,0.6|")),
        BuiltInEffects.Blur => Fx.Make(type, null, (Bl.Radius, 20), (Bl.Angle, 30)).Choose(BuiltInEffects.BlurOptions.Kind, variant),
        BuiltInEffects.Grain => Fx.Make(type, null).At(2),
        _ => Fx.Make(type, null),
    };
}
