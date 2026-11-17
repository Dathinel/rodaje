using Rodaje.Core.Timing;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Gpu;
using Rodaje.Render.Tests.Gpu;
using Rodaje.TestMedia;

namespace Rodaje.Render.Tests;

/// <summary>
/// Cuadros de CPU en el motor Vulkan (<see cref="CpuFrameUploads"/>): cualquier formato que decodifique FFmpeg sube y se compone
/// (antes, lo que no era 4:2:0 de 8 o 10 bits salía omitido), y los 8 bits conservan su normalización también en rango completo.
/// </summary>
public sealed class CpuUploadTests : IDisposable
{
    private const int Width = 64;
    private const int Height = 48;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rodaje-subidas-" + Guid.NewGuid().ToString("N"));

    public CpuUploadTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Rampa de luma 0..255 en rango completo: 255 debe dar 1 (no 1020/1023) y cada código x, x/255, en coma flotante.</summary>
    [VulkanFact]
    public void Full_range_8_bit_samples_keep_their_normalization()
    {
        // setparams marca el rango sin convertir las muestras: la luma de cada columna es su código.
        string path = Encode("rampa.mkv", "nullsrc=size=256x16:rate=25,format=yuv420p,geq=lum='X':cb=128:cr=128,setparams=range=pc", "yuv420p");
        using var decoder = VideoDecoder.Open(path, new VideoDecoderOptions { AllowHardwareDecoding = false });
        using var frame = decoder.GetFrame(0)!;
        var payload = Assert.IsType<CpuFramePayload>(frame.Payload);
        Assert.True(payload.Color.FullRange);
        IGpu gpu = VulkanTestGpu.Gpu;
        using var uploads = new CpuFrameUploads(gpu);
        GpuTextureSurface surface = uploads.Get(payload)!;
        YuvRange range = YuvRange.For(surface.PixelFormat, payload.Color.FullRange);
        ushort[] luma = Read(gpu, surface.Luma, 256, 16, 1);
        ushort[] chroma = Read(gpu, surface.Chroma, 128, 8, 2);
        for (int x = 0; x < 256; x++)
        {
            double normalized = (luma[(8 * 256) + x] / 65535.0 * range.LumaScale) + range.LumaBias;
            Assert.True(Math.Abs(normalized - (x / 255.0)) < 1e-5, $"código {x}: {normalized} en vez de {x / 255.0}");
        }

        Assert.True(Math.Abs((chroma[0] / 65535.0 * range.ChromaScale) + range.ChromaBias) < 1e-5);
    }

    /// <summary>4:2:2 de 10 bits (ProRes), 4:4:4, 12 bits y 4:2:2 de 8 bits: se convierten y se componen, sin capas omitidas.</summary>
    [VulkanTheory]
    [InlineData("yuv422p10le")]
    [InlineData("yuv444p")]
    [InlineData("yuv420p12le")]
    [InlineData("yuv422p")]
    public void Other_yuv_formats_compose_instead_of_being_skipped(string format)
    {
        string path = Encode($"{format}.mkv", $"color=c=gray:size={Width}x{Height}:rate=25", format);
        using var decoder = VideoDecoder.Open(path, new VideoDecoderOptions { AllowHardwareDecoding = false });
        var layer = new LayerFrame(0, "gris", decoder.GetFrame(0)!, LayerTransform.Identity);
        using var composition = new CompositionFrame(Time.Zero, Width, Height, [layer]);
        Assert.Equal(FramePixelFormat.Other, Assert.IsType<CpuFramePayload>(layer.Frame.Payload).PixelFormat);
        using var compositor = new Compositor(VulkanTestGpu.Gpu, Width, Height);
        CompositeResult result = compositor.RenderStrict(composition);
        Assert.Equal((1, 0), (result.LayersDrawn, result.LayersSkipped));
        byte[] rgba = compositor.ReadbackRgba();
        int at = ((Height / 2 * Width) + (Width / 2)) * 4;
        for (int c = 0; c < 3; c++)
        {
            Assert.InRange((int)rgba[at + c], 126, 130);
        }
    }

    /// <summary>Un PNG con alfa sobre otro opaco: sube como BGRA premultiplicado, igual que en D3D11 (StillImageTests).</summary>
    [VulkanTheory]
    [InlineData(null)]
    [InlineData("rgba64be")]
    public void Png_alpha_composes_premultiplied_over_the_lower_track(string? pixelFormat)
    {
        using var image = new StillImageFile(pixelFormat: pixelFormat);
        using var background = new StillImageFile(blue: true);
        using var front = VideoDecoder.Open(image.Path, new VideoDecoderOptions { AllowHardwareDecoding = false });
        using var back = VideoDecoder.Open(background.Path, new VideoDecoderOptions { AllowHardwareDecoding = false });
        using var composition = new CompositionFrame(Time.Zero, StillImageFile.Width, StillImageFile.Height,
            [new LayerFrame(0, "fondo", back.GetFrame(0)!, LayerTransform.Identity), new LayerFrame(1, "foto", front.GetFrame(0)!, LayerTransform.Identity)]);
        using var compositor = new Compositor(VulkanTestGpu.Gpu, StillImageFile.Width, StillImageFile.Height);
        Assert.Equal(0, compositor.RenderStrict(composition).LayersSkipped);
        byte[] rgba = compositor.ReadbackRgba();
        AssertPixel(rgba, 8, 0, 255);
        AssertPixel(rgba, 32, 128, 127);
        AssertPixel(rgba, 56, 255, 0);
    }

    private static void AssertPixel(byte[] rgba, int x, int red, int blue)
    {
        int at = ((24 * StillImageFile.Width) + x) * 4;
        Assert.InRange((int)rgba[at], Math.Max(0, red - 1), Math.Min(255, red + 1));
        Assert.InRange((int)rgba[at + 1], 0, 1);
        Assert.InRange((int)rgba[at + 2], Math.Max(0, blue - 1), Math.Min(255, blue + 1));
        Assert.Equal(255, rgba[at + 3]);
    }

    private static ushort[] Read(IGpu gpu, GpuTexture texture, int width, int height, int channels)
    {
        using GpuReadback read = gpu.CreateReadback(width, height, channels == 1 ? GpuFormat.R16Unorm : GpuFormat.Rg16Unorm);
        using (IGpuFrame frame = gpu.BeginFrame())
        {
            frame.Copy(texture, new GpuBox(0, 0, width, height), read);
        }

        byte[] bytes = new byte[width * height * channels * 2];
        read.Read(bytes);
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(bytes).ToArray();
    }

    private string Encode(string name, string source, string pixelFormat)
    {
        string path = Path.Combine(_folder, name);
        FfmpegTool.Default!.Run(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", source, "-frames:v", "1",
            "-pix_fmt", pixelFormat, "-c:v", "ffv1", path]);
        return path;
    }
}
