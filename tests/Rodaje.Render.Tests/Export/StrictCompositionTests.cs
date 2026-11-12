using Rodaje.Core.Model;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Shaders;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using BuiltInEffectsColor = Rodaje.Core.Model.BuiltInEffects.ColorParameters;

namespace Rodaje.Render.Tests.Export;

/// <summary><see cref="Compositor.RenderStrict"/>: en la exportación un efecto saltado no se tolera.</summary>
public sealed class StrictCompositionTests
{
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    [GpuFact]
    public void Waits_for_the_shaders_instead_of_skipping()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var gate = new ManualResetEventSlim(false);
        var cache = new ShaderCache(null) { Compile = _ => { gate.Wait(); return TrivialShader.Code; } };
        using var compositor = new Compositor(gpu, 256, 128, CompositeFormat.Rgba16Float, cache);
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(256, 128, false, new YuvCode(120, 128, 128)), Bt709,
            new LayerLook([Fx.Color((BuiltInEffectsColor.Exposure, 1)), Fx.Blur(5)], BlendMode.Overlay));
        using CompositionFrame frame = Layers.Frame(256, 128, layer);

        using var release = new Timer(_ => gate.Set(), null, 200, Timeout.Infinite);
        CompositeResult result = compositor.RenderStrict(frame);

        Assert.Equal((3, 0), (result.EffectsApplied, result.EffectsSkipped));
    }

    [GpuFact]
    public void Shaders_that_never_arrive_are_an_export_error()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var gate = new ManualResetEventSlim(false);
        var cache = new ShaderCache(null) { Compile = _ => { gate.Wait(); return TrivialShader.Code; } };
        using var compositor = new Compositor(gpu, 64, 64, CompositeFormat.Rgba16Float, cache);
        using CompositionFrame frame = Layers.Frame(64, 64);
        try
        {
            ExportException error = Assert.Throws<ExportException>(() => compositor.RenderStrict(frame, shaderTimeout: TimeSpan.FromMilliseconds(50)));
            Assert.Equal(ExportErrorCode.EffectFailed, error.Code);
            Assert.True(error.CanContinueWithout);
        }
        finally
        {
            gate.Set();
        }
    }

    [GpuFact]
    public void A_pool_that_stays_full_is_an_export_error()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 256, 128, CompositeFormat.Rgba16Float, ShaderCache.Default, poolCapBytes: 1024);
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(256, 128, false, new YuvCode(120, 128, 128)), Bt709,
            new LayerLook([Fx.Blur(20)]));
        using CompositionFrame frame = Layers.Frame(256, 128, layer);

        ExportException error = Assert.Throws<ExportException>(() => compositor.RenderStrict(frame));

        Assert.Equal(ExportErrorCode.EffectFailed, error.Code);
    }

    [GpuFact]
    public void Unknown_effects_are_an_export_error()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 64, 64);
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(64, 64, false, new YuvCode(120, 128, 128)), Bt709,
            new LayerLook([new LayerEffect(9999, default, null)]));
        using CompositionFrame frame = Layers.Frame(64, 64, layer);

        Assert.Equal(ExportErrorCode.EffectFailed, Assert.Throws<ExportException>(() => compositor.RenderStrict(frame)).Code);
    }

    [GpuFact]
    public void A_clean_frame_is_composed_once()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 64, 64);
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(64, 64, false, new YuvCode(120, 128, 128)), Bt709,
            new LayerLook([Fx.Color((BuiltInEffectsColor.Exposure, 0.5))]));
        using CompositionFrame frame = Layers.Frame(64, 64, layer);

        CompositeResult result = compositor.RenderStrict(frame);
        Assert.Equal((1, 1, 0), (result.LayersDrawn, result.EffectsApplied, result.EffectsSkipped));
    }
}
