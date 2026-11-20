using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Render.Export;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Tests.Gpu;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Export;

/// <summary>
/// La excepción abierta de la exportación en Vulkan (<c>docs/mac-y-linux-fase4.md</c>): fuera de D3D11 el conversor escala sin mips y
/// con el núcleo ensanchado como mucho 8 veces. Una composición 4K de ruido gris (el peor caso de aliasing) reducida 12 veces a 320×180
/// frente a la media exacta de cada bloque de 12×12; en Windows, también D3D11 con mips (WARP), para que la diferencia quede en cifras.
/// </summary>
public sealed class ExportDownscaleTests(ITestOutputHelper output)
{
    private const int SourceWidth = 3840;
    private const int SourceHeight = 2160;
    private const int OutputWidth = 320;
    private const int OutputHeight = 180;
    private const int Block = SourceWidth / OutputWidth;

    /// <summary>Error medio y máximo de la luma en Vulkan frente a la media exacta, en códigos de 8 bits (RTX 3050: 2,50 y 13,0; D3D11 WARP con mips: 2,48 y 15,1).</summary>
    private const double VulkanMeanLimit = 3.5, VulkanMaxLimit = 18.0;

    [VulkanFact]
    public void Strong_downscale_without_mips_stays_within_the_documented_gap()
    {
        (double mean, double max) = Measure(VulkanTestGpu.Gpu, format => new ExportFrameConverter(VulkanTestGpu.Gpu, SourceWidth, SourceHeight, format));
        output.WriteLine($"Vulkan ({VulkanTestGpu.Gpu.Caps.Adapter}), sin mips: error medio {mean:0.000} y máximo {max:0.000} códigos de 8 bits.");
        if (OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            (double d3dMean, double d3dMax) = MeasureD3D11();
            output.WriteLine($"D3D11 WARP, con mips: error medio {d3dMean:0.000} y máximo {d3dMax:0.000} códigos de 8 bits.");
        }

        Assert.True(mean <= VulkanMeanLimit && max <= VulkanMaxLimit,
            $"La reducción sin mips se alejó más de lo anotado: media {mean:0.000} (límite {VulkanMeanLimit}), máximo {max:0.000} (límite {VulkanMaxLimit}).");
    }

    /// <summary>El constructor de <see cref="GpuDevice"/> es el de D3D11 con vistas de mips; el de IGpu escala sin ellas en cualquier motor.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    private static (double Mean, double Max) MeasureD3D11()
    {
        using GpuDevice device = GpuDevice.CreateWarp();
        return Measure(D3D11Gpu.For(device), format => new ExportFrameConverter(device, SourceWidth, SourceHeight, format));
    }

    private static (double Mean, double Max) Measure(IGpu gpu, Func<ExportVideoFormat, ExportFrameConverter> create)
    {
        // Ruido gris opaco en RGBA16F (el formato de la composición), con la media exacta de cada bloque de salida.
        var random = new Random(1234);
        byte[] pixels = new byte[SourceWidth * SourceHeight * 8];
        Span<Half> halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(pixels.AsSpan());
        double[] blocks = new double[OutputWidth * OutputHeight];
        for (int y = 0; y < SourceHeight; y++)
        {
            for (int x = 0; x < SourceWidth; x++)
            {
                var value = (Half)random.NextSingle();
                int i = ((y * SourceWidth) + x) * 4;
                halves[i] = value;
                halves[i + 1] = value;
                halves[i + 2] = value;
                halves[i + 3] = Half.One;
                blocks[((y / Block) * OutputWidth) + (x / Block)] += (double)value / (Block * Block);
            }
        }

        // Con mips, como la textura del compositor: el conversor de D3D11 lee el nivel que toca; el portátil, solo el 0.
        using GpuTexture picture = gpu.CreateTexture(new GpuTextureDesc(SourceWidth, SourceHeight, GpuFormat.Rgba16Float,
            GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget | GpuTextureUsage.GenerateMips, MipLevels: 0));
        using (IGpuFrame frame = gpu.BeginFrame())
        {
            frame.Write(picture, pixels, SourceWidth * 8);
            frame.GenerateMips(picture);
        }

        var format = new ExportVideoFormat(OutputWidth, OutputHeight, new Rational(30, 1), ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        using ExportFrameConverter converter = create(format);
        converter.Submit(picture, 0);
        byte[] luma = new byte[OutputWidth * OutputHeight];
        Assert.True(converter.TryRead((in ExportVideoFrame frame) =>
        {
            for (int y = 0; y < OutputHeight; y++)
            {
                frame.Plane0.Slice(y * frame.Pitch0, OutputWidth).CopyTo(luma.AsSpan(y * OutputWidth));
            }
        }));

        double sum = 0, worst = 0;
        for (int i = 0; i < luma.Length; i++)
        {
            // Gris en rango limitado: 16 + 219 × valor.
            double error = Math.Abs(luma[i] - (16 + (219 * blocks[i])));
            sum += error;
            worst = Math.Max(worst, error);
        }

        return (sum / luma.Length, worst);
    }
}
