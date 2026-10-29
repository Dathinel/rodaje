using Rodaje.Modules.Protocol;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Module.Raw.Tests;

/// <summary>
/// Cuántos de los 3840 códigos de un CinemaDNG de 12 bits (negro 256, blanco 4095) devuelve el revelado en rgba16f, en
/// luz lineal y con la curva Rec.709 que usa la importación (docs/hdr.md, fase D).
/// </summary>
public sealed class BitDepthTests(ITestOutputHelper output)
{
    private const int Levels = 3840;
    private const int Block = 4;
    private const int Width = Levels * Block;
    private const int Height = 32; // LibRaw rechaza cuadros de menos de 22 filas.

    /// <summary>Rampa gris de un código RAW por bloque de 4 columnas; neutro 1 para que los tres canales lleven el código exacto.</summary>
    internal static byte[] Ramp() => new SyntheticDng
    {
        Width = Width,
        Height = Height,
        Neutral = [1.0, 1.0, 1.0],
        Scene = (x, _) => { double v = x / Block / (double)(Levels - 1); return (v, v, v); },
    }.Write();

    /// <summary>Distintos en el verde de la fila central, en la columna 2 de cada bloque (lejos de la interpolación del borde).</summary>
    internal static int DistinctGreen(Func<int, double> green) =>
        Enumerable.Range(0, Levels).Select(i => green((i * Block) + 2)).Distinct().Count();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_12_bit_ramp_develops_to_rgba16f(bool linear)
    {
        OutputCurve curve = linear ? OutputCurve.Linear : OutputCurve.Rec709;
        if (!DevelopTests.HasLibRaw)
        {
            output.WriteLine(DevelopTests.Missing);
            return;
        }

        Half[] rgba = DevelopTests.Develop(Ramp(), new DevelopSettings { Curve = curve, HighQuality = false, WhiteBalance = WhiteBalanceMode.Camera }, Width, Height);
        int distinct = DistinctGreen(x => (double)rgba[((((Height / 2) * Width) + x) * 4) + 1]);
        double top = (double)rgba[((((Height / 2) * Width) + Width - 2) * 4) + 1];
        output.WriteLine($"{curve}: {Levels} códigos RAW de 12 bits → {distinct} valores rgba16f distintos; blanco {top:0.0000}.");
        Assert.True(distinct > 1024, $"Solo {distinct}: el revelado ya cabe en 10 bits.");
        Assert.True(distinct <= Levels);
    }

    [Fact]
    public void Eight_bit_output_keeps_at_most_256()
    {
        if (!DevelopTests.HasLibRaw)
        {
            output.WriteLine(DevelopTests.Missing);
            return;
        }

        var pixels = new byte[Width * Height * 4];
        var settings = new DevelopSettings { Curve = OutputCurve.Rec709, HighQuality = false };
        RawDeveloper.Develop(Ramp(), settings, new ToneTable(PixelFormats.Bgra8, 0, OutputCurve.Rec709), pixels,
            new VideoLayout(Width, Height, Width * 4, PixelFormats.Bgra8));
        int distinct = DistinctGreen(x => pixels[((((Height / 2) * Width) + x) * 4) + 1]);
        output.WriteLine($"bgra8: {Levels} códigos → {distinct}.");
        Assert.InRange(distinct, 200, 256);
    }
}
