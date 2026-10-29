using Rodaje.Core.Effects;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Effects;
using Rodaje.Render.Shaders;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;

namespace Rodaje.Render.Tests;

public sealed class EffectPipelineTests
{
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    [Fact]
    public void Gpu_table_matches_the_core_registry()
    {
        foreach (EffectDefinition definition in EffectRegistry.All.Where(GpuEffectRegistry.AppliesOnGpu))
        {
            GpuEffect gpu = GpuEffectRegistry.Find(definition.Code)
                ?? throw new Xunit.Sdk.XunitException($"{definition} no tiene implementación en la GPU.");
            Assert.Equal(definition.Type, gpu.Type);
            Assert.Equal(definition.Stage, gpu.Stage);
        }

        foreach (GpuEffect gpu in GpuEffectRegistry.All)
        {
            EffectDefinition definition = EffectRegistry.Find(gpu.Code) ?? throw new Xunit.Sdk.XunitException($"{gpu.Code} no está en el núcleo.");
            Assert.True(GpuEffectRegistry.AppliesOnGpu(definition));
            Assert.Equal(definition.Type, gpu.Type);
        }

        Assert.Equal(GpuEffectRegistry.All.Length, GpuEffectRegistry.All.Select(e => e.Code).Distinct().Count());
    }

    [Fact]
    public void Blend_modes_follow_the_registry_order()
    {
        Assert.Equal(EffectRegistry.BlendModes.Length, Enum.GetValues<BlendMode>().Length);
        for (int i = 0; i < EffectRegistry.BlendModes.Length; i++)
        {
            Assert.Equal(EffectRegistry.BlendModes[i], JsonName((BlendMode)i));
        }
    }

    [Fact]
    public void Pool_cap_follows_video_memory()
    {
        const long mb = 1024 * 1024;
        Assert.Equal(300 * mb, Compositor.PoolCapFor(4096 * mb));
        Assert.Equal((long)(2048 * mb * 0.075), Compositor.PoolCapFor(2048 * mb));
        Assert.Equal(96 * mb, Compositor.PoolCapFor(512 * mb));
    }

    [Fact]
    public void Small_luts_are_subdivided_on_exact_cell_boundaries()
    {
        Assert.Equal(17, LutTextures.TextureSize(2));
        Assert.Equal(17, LutTextures.TextureSize(3));
        Assert.Equal(19, LutTextures.TextureSize(7));
        Assert.Equal(33, LutTextures.TextureSize(33));
    }

    /// <summary>Con el pool lleno, los efectos espaciales se saltan y se cuentan, y el cuadro sale igual.</summary>
    [GpuFact]
    public void A_full_pool_skips_spatial_effects_without_failing()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 256, 128, CompositeFormat.Rgba16Float, ShaderCache.Default, poolCapBytes: 1024);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(256, 128, false, new YuvCode(120, 128, 128)), Bt709,
            new LayerLook([Fx.Blur(20), Fx.Color((BuiltInEffectsColor.Exposure, 1))], BlendMode.Overlay));
        using CompositionFrame frame = Layers.Frame(256, 128, layer);

        CompositeResult result = compositor.Render(frame);

        Assert.Equal(1, result.LayersDrawn);
        Assert.Equal(2, result.EffectsSkipped);
        Assert.Equal(1, result.EffectsApplied);
        Assert.Equal(0, result.PoolBytes);
    }

    /// <summary>Un cuadro nunca espera a que compile un shader: lo que falta se salta y se cuenta.</summary>
    [GpuFact]
    public void A_frame_never_waits_for_a_shader()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var gate = new ManualResetEventSlim(false);
        // Un shader vacío en vez de compilar en frío las variantes: al abrir la puerta solo quedan sus CreatePixelShader
        // (la CI pasó de los 60 s). Que cada variante compila y pinta bien lo prueban ShaderCompileTests y EffectFidelityTests.
        var cache = new ShaderCache(null) { Compile = _ => { gate.Wait(); return TrivialShader.Code; } };
        using var compositor = new Compositor(gpu, 256, 128, CompositeFormat.Rgba16Float, cache);
        YuvCode gray = ReferenceColor.Encode(120, 120, 120, Bt709, false);
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(256, 128, false, gray), Bt709,
            new LayerLook([Fx.Color((BuiltInEffectsColor.Exposure, 1)), Fx.Blur(5)], BlendMode.Overlay));
        using CompositionFrame frame = Layers.Frame(256, 128, layer);

        CompositeResult result = compositor.Render(frame);
        gate.Set();

        Assert.Equal(new CompositeResult(1, 0, 0, 3, 0, 0), result);
        new RgbaImage(compositor.ReadbackRgba(), 256, 128).AssertColor(128, 64, ReferenceColor.Decode(gray, Bt709, false));
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        Assert.Equal(3, compositor.Render(frame).EffectsApplied);
    }

    [GpuFact]
    public void Unknown_and_repeated_effects_are_counted_as_skipped()
    {
        using GpuDevice gpu = GpuDevice.Create();
        LayerEffect unknown = new(9999, default, null);
        LayerEffect speed = Fx.Make(Rodaje.Core.Model.BuiltInEffects.Speed);
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(64, 64, false, new YuvCode(120, 128, 128)), Bt709,
            new LayerLook([Fx.Color(), Fx.Color(), Fx.Vignette(0.5, 0.5, 0.5), unknown, speed]));

        EffectFidelityTests.Compose(gpu, 64, 64, out CompositeResult result, null, layer);

        Assert.Equal(2, result.EffectsApplied);
        Assert.Equal(3, result.EffectsSkipped);
    }

    [GpuFact]
    public void Intermediates_are_reused_frame_after_frame()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 512, 256);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        var bytes = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            LayerFrame layer = Layers.From(gpu, YuvImage.Solid(512, 256, false, new YuvCode(120, 128, 128)), Bt709,
                new LayerLook([Fx.Blur(30), Fx.Sharpen(1, 2)], BlendMode.SoftLight));
            using CompositionFrame frame = Layers.Frame(512, 256, layer);
            bytes.Add(compositor.Render(frame).PoolBytes);
        }

        Assert.True(bytes[0] > 0);
        Assert.All(bytes, b => Assert.Equal(bytes[0], b));
        compositor.TrimCachedViews();
    }

    private static string JsonName(BlendMode mode)
    {
        string name = mode.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}

file static class BuiltInEffectsColor
{
    public const string Exposure = Rodaje.Core.Model.BuiltInEffects.ColorParameters.Exposure;
}
