using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render.Export;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Export;

/// <summary>La misma fidelidad que en WARP, en la GPU de verdad (la generación de mips y la aritmética son del controlador).</summary>
public sealed class ExportHardwareTests(ITestOutputHelper output)
{
    private static readonly Rational Rate = new(30, 1);

    [GpuTheory]
    [InlineData(ExportPixelFormat.Nv12)]
    [InlineData(ExportPixelFormat.P010)]
    [InlineData(ExportPixelFormat.Yuv420P)]
    [InlineData(ExportPixelFormat.Yuv422P10)]
    [InlineData(ExportPixelFormat.Yuv444P)]
    [InlineData(ExportPixelFormat.Yuva444P10)]
    [InlineData(ExportPixelFormat.Rgba)]
    [InlineData(ExportPixelFormat.Rgb24)]
    public void Formats_match_the_reference(ExportPixelFormat pixels)
    {
        using GpuDevice device = GpuDevice.Create();
        using ExportGpu gpu = ExportGpu.Hardware(device);
        var format = new ExportVideoFormat(254, 144, Rate, pixels, ExportColor.For(1920, 1080, pixels));
        ExportCheck.Compare(output, gpu, Picture.Test(254, 144, pixels.HasAlpha()), format);
    }

    [GpuTheory]
    [InlineData(512, 288, ExportFit.Bars)]
    [InlineData(384, 216, ExportFit.Bars)]
    [InlineData(768, 432, ExportFit.Bars)]
    [InlineData(256, 256, ExportFit.Bars)]
    [InlineData(256, 256, ExportFit.Crop)]
    public void Scales_and_fits_match_the_reference(int sourceWidth, int sourceHeight, ExportFit fit)
    {
        using GpuDevice device = GpuDevice.Create();
        using ExportGpu gpu = ExportGpu.Hardware(device);
        var format = new ExportVideoFormat(256, 144, Rate, ExportPixelFormat.P010, ExportColor.Bt709Limited);
        ExportCheck.Compare(output, gpu, Picture.Test(sourceWidth, sourceHeight), format, fit);
    }

    [GpuFact]
    public void Compositor_output_converts_with_or_without_a_transparent_background()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 320, 180);
        using var frame = new CompositionFrame(Time.Zero, 320, 180, []);
        var rgba = new ExportVideoFormat(320, 180, Rate, ExportPixelFormat.Rgba, ExportColor.Srgb);
        var nv12 = new ExportVideoFormat(320, 180, Rate, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);

        compositor.TransparentBackground = true;
        compositor.Render(frame);
        int[][] transparent = Read(gpu, compositor, rgba);
        Assert.All(transparent[0], v => Assert.Equal(0, v));

        compositor.TransparentBackground = false;
        compositor.Render(frame);
        int[][] black = Read(gpu, compositor, nv12);
        Assert.All(black[0], v => Assert.Equal(16, v));
        Assert.All(black[1], v => Assert.Equal(128, v));
    }

    private static int[][] Read(GpuDevice gpu, Compositor compositor, ExportVideoFormat format)
    {
        using var converter = new ExportFrameConverter(gpu, compositor.Width, compositor.Height, format);
        converter.Submit(compositor.Texture, 0);
        return ExportCheck.ReadOne(converter, 0);
    }
}
