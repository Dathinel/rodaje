using Rodaje.Media.Video;

namespace Rodaje.Media.Tests;

/// <summary>La conversión HDR → SDR de la CPU da lo mismo que el shader de D-052 (vectores de <c>HdrConversionTests</c>).</summary>
public sealed class HdrToneMapTests
{
    // Y'CbCr de 10 bits limitado BT.2020 → R'G'B' de 8 bits (la entrada del mapa) y el SDR esperado del shader.
    [Theory]
    [InlineData(HdrTransfer.Pq, 400, 512, 512, 74.764, 74.764, 74.764)]
    [InlineData(HdrTransfer.Pq, 500, 480, 550, 188.906, 95.007, 72.173)]
    [InlineData(HdrTransfer.Hlg, 400, 512, 512, 74.427, 74.427, 74.427)]
    [InlineData(HdrTransfer.Hlg, 500, 480, 550, 126.960, 92.688, 82.357)]
    public void Matches_the_shader_reference_vectors(HdrTransfer transfer, int y, int cb, int cr, double r, double g, double b)
    {
        double luma = (y - 64) / 876.0;
        double pb = (cb - 512) / 896.0;
        double pr = (cr - 512) / 896.0;
        double red = luma + (1.4746 * pr);
        double blue = luma + (1.8814 * pb);
        double green = (luma - (0.2627 * red) - (0.0593 * blue)) / 0.6780;
        byte[] bgra = [Code(blue), Code(green), Code(red), 255];

        // Los vectores del shader se calcularon con 1000 nits de referencia.
        HdrToneMap.ToSdrInPlace(bgra, new VideoColor(YuvMatrix.Bt2020, false) { Transfer = transfer, Bt2020Primaries = true }, 1000);

        // La entrada ya viene redondeada a 8 bits: hasta 3 códigos de diferencia con el shader, que trabaja en flotante.
        Assert.InRange(bgra[2], r - 3, r + 3);
        Assert.InRange(bgra[1], g - 3, g + 3);
        Assert.InRange(bgra[0], b - 3, b + 3);
        Assert.Equal(255, bgra[3]);
    }

    [Fact]
    public void Reference_white_is_sdr_white_black_stays_black_and_sdr_is_untouched()
    {
        byte pq100 = Code(Pq(100));
        byte[] pq = [pq100, pq100, pq100, 255, 0, 0, 0, 255];
        HdrToneMap.ToSdrInPlace(pq, new VideoColor(YuvMatrix.Bt2020, false) { Transfer = HdrTransfer.Pq, Bt2020Primaries = true });
        Assert.Equal([255, 255, 255, 255, 0, 0, 0, 255], pq);

        byte[] hlg = [255, 255, 255, 255, 0, 0, 0, 255];
        HdrToneMap.ToSdrInPlace(hlg, new VideoColor(YuvMatrix.Bt2020, false) { Transfer = HdrTransfer.Hlg, Bt2020Primaries = true });
        Assert.Equal([255, 255, 255, 255, 0, 0, 0, 255], hlg);

        byte[] sdr = [10, 120, 200, 255, 7, 8, 9, 0];
        HdrToneMap.ToSdrInPlace(sdr, VideoColor.Bt709Limited);
        Assert.Equal([10, 120, 200, 255, 7, 8, 9, 0], sdr);
    }

    private static byte Code(double x) => (byte)Math.Clamp(Math.Round(x * 255), 0, 255);

    private static double Pq(double nits)
    {
        double y = Math.Pow(nits / 10000, 0.1593017578125);
        return Math.Pow((0.8359375 + (18.8515625 * y)) / (1 + (18.6875 * y)), 78.84375);
    }
}
