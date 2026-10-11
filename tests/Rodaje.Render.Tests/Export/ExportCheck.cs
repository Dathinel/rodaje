using Rodaje.Media.Export;
using Rodaje.Render.Export;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Export;

/// <summary>Convertir en la GPU y comparar con <see cref="ExportReference"/>, en WARP o en la GPU de verdad.</summary>
internal static class ExportCheck
{
    public static int[][] ReadOne(ExportFrameConverter converter, long expectedIndex)
    {
        int[][]? planes = null;
        long index = -1;
        Assert.True(converter.TryRead((in ExportVideoFrame frame) =>
        {
            index = frame.Index;
            planes = new int[frame.PlaneCount][];
            for (int p = 0; p < frame.PlaneCount; p++)
            {
                planes[p] = ExportReference.Read(frame, p);
            }
        }));
        Assert.Equal(expectedIndex, index);
        return planes!;
    }

    public static int[][] Convert(ExportGpu gpu, Picture source, ExportVideoFormat format, bool dither)
    {
        using ExportFrameConverter converter = gpu.Converter(source.Width, source.Height, format, dither: dither);
        nint texture = gpu.Upload(source);
        try
        {
            converter.Submit(texture, 0);
            return ReadOne(converter, 0);
        }
        finally
        {
            ExportGpu.Release(texture);
        }
    }

    /// <summary>Convierte en la GPU y exige que cada muestra de cada plano esté a <paramref name="tolerance"/> códigos de la referencia.</summary>
    public static int[][] Compare(ITestOutputHelper output, ExportGpu gpu, Picture source, ExportVideoFormat format,
        ExportFit fit = ExportFit.Bars, ExportScaleFilter filter = ExportScaleFilter.Auto, int tolerance = 1)
    {
        using ExportFrameConverter converter = gpu.Converter(source.Width, source.Height, format, fit, filter);
        nint texture = gpu.Upload(source);
        try
        {
            converter.Submit(texture, 0);
            int[][] got = ReadOne(converter, 0);
            Picture scaled = ExportReference.Scale(source, converter.Plan, format.Width, format.Height, format.PixelFormat.HasAlpha());
            int[][] expected = ExportReference.Convert(scaled, format, dither: true);
            int worst = Worst(got, expected);
            output.WriteLine($"{format.PixelFormat} {format.Width}×{format.Height} desde {source.Width}×{source.Height} ({fit}, {filter}, nivel {converter.Plan.Level}, {converter.Plan.Kernel}): diferencia máxima {worst} códigos.");
            Assert.True(worst <= tolerance, $"{format.PixelFormat}: diferencia máxima {worst} códigos (tolerancia {tolerance}).");
            return got;
        }
        finally
        {
            ExportGpu.Release(texture);
        }
    }

    public static int Worst(int[][] got, int[][] expected)
    {
        Assert.Equal(expected.Length, got.Length);
        int worst = 0;
        for (int p = 0; p < got.Length; p++)
        {
            Assert.Equal(expected[p].Length, got[p].Length);
            for (int i = 0; i < got[p].Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(got[p][i] - expected[p][i]));
            }
        }

        return worst;
    }
}
