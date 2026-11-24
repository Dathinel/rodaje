using System.Runtime.InteropServices;
using Rodaje.Render.Export;
using Rodaje.Render.Gpu;
using Rodaje.Render.Tests.Gpu;

namespace Rodaje.Render.Tests.Export;

/// <summary>MaxCLL y MaxFALL (CTA-861.3) medidos en el motor Vulkan sobre una composición lineal conocida (1,0 = 203 nits).</summary>
public sealed class ContentLightMeterTests
{
    [VulkanFact]
    public void Peak_and_frame_average_come_out_in_nits()
    {
        const int width = 40, height = 24;
        var halves = new Half[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            halves[(i * 4) + 0] = (Half)0.5f;
            halves[(i * 4) + 1] = (Half)0.25f;
            halves[(i * 4) + 2] = (Half)0.125f;
            halves[(i * 4) + 3] = (Half)1f;
        }

        // Un píxel a 4,0 (812 nits) en el verde, fuera de la primera tesela.
        int bright = ((17 * width) + 33) * 4;
        halves[bright + 1] = (Half)4f;
        IGpu gpu = VulkanTestGpu.Gpu;
        using GpuTexture composite = gpu.CreateTexture(new GpuTextureDesc(width, height, GpuFormat.Rgba16Float, GpuTextureUsage.Sampled),
            MemoryMarshal.AsBytes(halves.AsSpan()));
        using var meter = new ContentLightMeter(gpu, width, height);

        meter.Measure(composite);

        double average = ((0.5 * ((width * height) - 1)) + 4.0) / (width * height);
        Assert.Equal(1, meter.Frames);
        Assert.Equal(new ContentLight(812, (int)Math.Round(average * 203)), meter.Result);
    }
}
