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
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "rgba64be")]
    [InlineData(false, "pal8")]
    public void Png_alpha_composes_over_the_lower_track_with_and_without_effects(bool effects, string? pixelFormat)
    {
        using var image = new StillImageFile(pixelFormat: pixelFormat);
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
        Assert.Equal(pixelFormat == "rgba64be" ? FramePixelFormat.Rgba16 : FramePixelFormat.Bgra8, Assert.IsType<D3D11TexturePayload>(layer.Frame.Payload).PixelFormat);
        Assert.Equal(1, front.Statistics.DecodedFrames);
    }

    /// <summary>Un PNG de 16 bits con una rampa de 4096 códigos sube como RGBA16 y el intermedio conserva más de 1024 niveles (8 bits daba 256).</summary>
    [HardwareFact]
    public void A_16_bit_png_keeps_more_than_8_bits_in_the_intermediate()
    {
        const int width = 4096;
        const int height = 8;
        string folder = Path.Combine(Path.GetTempPath(), "rodaje-png16-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "rampa.png");
            var pixels = new byte[width * height * 6];
            for (int i = 0; i < width * height; i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    BitConverter.TryWriteBytes(pixels.AsSpan((i * 6) + (c * 2)), (ushort)((i % width) * 16));
                }
            }

            FfmpegTool.Default!.Run(["-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pixel_format", "rgb48le", "-video_size", $"{width}x{height}",
                "-i", "pipe:0", "-frames:v", "1", "-pix_fmt", "rgb48be", "-update", "1", path], input => input.Write(pixels));

            using var gpu = GpuDevice.TryCreate(out _)!;
            using var decoder = VideoDecoder.Open(path, new VideoDecoderOptions { Device = gpu });
            using var frame = decoder.GetFrame(0)!;
            Assert.Equal(FramePixelFormat.Rgba16, Assert.IsType<D3D11TexturePayload>(frame.Payload).PixelFormat);
            using var composition = new CompositionFrame(Time.Zero, width, height, [new LayerFrame(0, "foto", frame, LayerTransform.Identity)]);
            using var compositor = new Compositor(gpu, width, height);
            Assert.Equal(0, compositor.RenderStrict(composition).LayersSkipped);
            float[] halves = compositor.ReadbackRgbaFloat();
            int levels = Enumerable.Range(0, width).Select(x => (int)Math.Round(halves[(((height / 2) * width) + x) * 4 + 1] * 4095)).Distinct().Count();
            Assert.True(levels > 2000, $"El intermedio solo guarda {levels} niveles de 4096.");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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
    [HardwareTheory]
    [InlineData(10000, 100)]
    [InlineData(100, 10000)]
    public void Oversized_images_are_scaled_before_upload(int width, int height)
    {
        using var image = new StillImageFile(width: width, height: height);
        using var gpu = GpuDevice.TryCreate(out _)!;
        using var decoder = VideoDecoder.Open(image.Path, new VideoDecoderOptions { Device = gpu });
        using var frame = decoder.GetFrame(0)!;
        var payload = Assert.IsType<D3D11TexturePayload>(frame.Payload);
        Assert.Equal(width > height ? 8192 : 82, payload.Width);
        Assert.Equal(height > width ? 8192 : 82, payload.Height);
    }
}
