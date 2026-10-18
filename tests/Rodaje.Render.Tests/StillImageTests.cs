using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;

namespace Rodaje.Render.Tests;

public sealed class StillImageTests
{
    [HardwareTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Png_alpha_composes_over_the_lower_track_with_and_without_effects(bool effects)
    {
        using var image = new StillImageFile();
        using var background = new StillImageFile(blue: true);
        using var gpu = GpuDevice.TryCreate(out _)!;
        using var front = VideoDecoder.Open(image.Path, new VideoDecoderOptions { Device = gpu });
        using var back = VideoDecoder.Open(background.Path, new VideoDecoderOptions { Device = gpu });
        var layer = new LayerFrame(1, "foto", front.GetFrame(0)!, LayerTransform.Identity)
        {
            Effects = effects ? [Fx.Make(BuiltInEffects.Color)] : [],
        };
        using var composition = new CompositionFrame(Time.Zero, StillImageFile.Width, StillImageFile.Height,
            [new LayerFrame(0, "fondo", back.GetFrame(0)!, LayerTransform.Identity), layer]);
        using var compositor = new Compositor(gpu, StillImageFile.Width, StillImageFile.Height);
        CompositeResult result = compositor.RenderStrict(composition);
        Assert.Equal(0, result.LayersSkipped);
        byte[] rgba = compositor.ReadbackRgba();
        AssertPixel(rgba, 8, 0, 255);
        AssertPixel(rgba, 32, 128, 127);
        AssertPixel(rgba, 56, 255, 0);
        Assert.Equal(FramePixelFormat.Bgra8, Assert.IsType<D3D11TexturePayload>(layer.Frame.Payload).PixelFormat);
        Assert.Equal(1, front.Statistics.DecodedFrames);
    }

    private static void AssertPixel(byte[] rgba, int x, int red, int blue)
    {
        int at = ((24 * StillImageFile.Width) + x) * 4;
        Assert.InRange((int)rgba[at], Math.Max(0, red - 1), Math.Min(255, red + 1));
        Assert.Equal(0, rgba[at + 1]);
        Assert.InRange((int)rgba[at + 2], Math.Max(0, blue - 1), Math.Min(255, blue + 1));
        Assert.Equal(255, rgba[at + 3]);
    }

    [HardwareFact]
    public void A_transformed_photo_keeps_its_alpha_through_a_mask()
    {
        using var image = new StillImageFile();
        using var background = new StillImageFile(blue: true);
        using var gpu = GpuDevice.TryCreate(out _)!;
        using var front = VideoDecoder.Open(image.Path, new VideoDecoderOptions { Device = gpu });
        using var back = VideoDecoder.Open(background.Path, new VideoDecoderOptions { Device = gpu });
        var mask = Fx.Make(BuiltInEffects.Mask, null,
            (BuiltInEffects.MaskParameters.Width, 1), (BuiltInEffects.MaskParameters.Height, 1),
            (BuiltInEffects.MaskParameters.Feather, 0), (BuiltInEffects.MaskParameters.Opacity, 0.5));
        using var composition = new CompositionFrame(Time.Zero, StillImageFile.Width, StillImageFile.Height,
            [new LayerFrame(0, "fondo", back.GetFrame(0)!, LayerTransform.Identity),
             new LayerFrame(1, "foto", front.GetFrame(0)!, new LayerTransform(8, 0, 0.75, 0, 1)) { Effects = [mask] }]);
        using var compositor = new Compositor(gpu, StillImageFile.Width, StillImageFile.Height);
        Assert.Equal(0, compositor.RenderStrict(composition).LayersSkipped);
        byte[] rgba = compositor.ReadbackRgba();
        AssertPixel(rgba, 0, 0, 255);
        AssertPixel(rgba, 32, 64, 191);
    }

    [HardwareFact]
    public void Bilinear_filtering_does_not_reveal_the_rgb_hidden_by_zero_alpha()
    {
        using var image = new StillImageFile();
        using var background = new StillImageFile(blue: true);
        using var gpu = GpuDevice.TryCreate(out _)!;
        using var front = VideoDecoder.Open(image.Path, new VideoDecoderOptions { Device = gpu });
        using var back = VideoDecoder.Open(background.Path, new VideoDecoderOptions { Device = gpu });
        using var composition = new CompositionFrame(Time.Zero, StillImageFile.Width, StillImageFile.Height,
            [new LayerFrame(0, "fondo", back.GetFrame(0)!, LayerTransform.Identity),
             new LayerFrame(1, "foto", front.GetFrame(0)!, new LayerTransform(0.5, 0, 1, 0, 1))]);
        using var compositor = new Compositor(gpu, StillImageFile.Width, StillImageFile.Height);
        compositor.RenderStrict(composition);
        AssertPixel(compositor.ReadbackRgba(), 21, 64, 191);
    }
}
