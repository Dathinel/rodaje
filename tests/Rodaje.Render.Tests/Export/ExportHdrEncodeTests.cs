using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Render.Export;
using Rodaje.Render.Shaders;
using Rodaje.Render.Tests.Support;

namespace Rodaje.Render.Tests.Export;

/// <summary>
/// Fase C del HDR, parte 1: la exportación codifica la composición SDR a BT.2020 con PQ o HLG (blanco en 203 nits) y la
/// salida SDR es, byte a byte, la de antes.
/// </summary>
public sealed class ExportHdrEncodeTests
{
    private static readonly Rational Rate = new(30, 1);

    [Fact]
    public void The_sdr_convert_shader_compiles_to_the_same_bytecode_as_before_hdr()
    {
        byte[] legacy = ShaderCompiler.Compile(LegacyExportShaders.Convert, "CsConvert", "cs_5_0", "rodaje-export-legacy.hlsl");
        byte[] current = ShaderCompiler.Compile(ExportShaders.ConvertVariant.FullSource(), "CsConvert", "cs_5_0", "rodaje-export.hlsl");

        Assert.Equal(legacy, current);
        Assert.NotEqual(legacy, ShaderCompiler.Compile(ExportShaders.ConvertHdrVariant.FullSource(), "CsConvert", "cs_5_0", "rodaje-export.hlsl"));
    }

    [Theory]
    [InlineData(ExportPixelFormat.P010)]
    [InlineData(ExportPixelFormat.Yuv420P10)]
    [InlineData(ExportPixelFormat.Yuv422P10)]
    public void Sdr_white_lands_on_the_pq_code_of_203_nits_and_gray_follows_the_curve(ExportPixelFormat pixels)
    {
        using ExportGpu gpu = ExportGpu.Warp();
        int[] luma = Convert(gpu, pixels, ExportColor.Hdr10);

        // 203 nits en PQ ≈ 0,581 (BT.2408): en 10 bits limitado, 64 + 876 × 0,581 ≈ 573; el gris del 50 % BT.709 ≈ 454.
        Assert.InRange(luma[0], Limited(Pq(203)) - 1, Limited(Pq(203)) + 1);
        Assert.InRange(luma[0], 572, 574);
        Assert.InRange(luma[16], Limited(Pq(203 * Bt709Linear(0.5))) - 1, Limited(Pq(203 * Bt709Linear(0.5))) + 1);
        Assert.Equal(64, luma[48]);
    }

    /// <summary>Parte 2 (D-055): la composición de una secuencia HDR es lineal BT.2020 con 1,0 = 203 nits y no pasa por la curva BT.709 ni se recorta.</summary>
    [Fact]
    public void A_linear_hdr_composition_is_encoded_as_nits_without_clipping()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(64, 16, Rate, ExportPixelFormat.P010, ExportColor.Hdr10);
        double peak = 1000.0 / 203;
        var source = new Picture(64, 16).Fill(0, 0, 16, 16, 1, 1, 1).Fill(16, 0, 32, 16, 0.5, 0.5, 0.5).Fill(48, 0, 16, 16, peak, peak, peak);
        using ExportFrameConverter converter = gpu.Converter(64, 16, format, dither: false, source: SequenceColorSpace.HdrPq);
        nint texture = gpu.Upload(source);
        try
        {
            converter.Submit(texture, 0);
            int[] luma = ExportCheck.ReadOne(converter, 0)[0];
            Assert.InRange(luma[0], 572, 574);
            Assert.InRange(luma[16], Limited(Pq(101.5)) - 1, Limited(Pq(101.5)) + 1);
            Assert.InRange(luma[48], Limited(Pq(1000)) - 1, Limited(Pq(1000)) + 1);
        }
        finally
        {
            ExportGpu.Release(texture);
        }

        Assert.NotEqual(ShaderCompiler.Compile(ExportShaders.ConvertVariant.FullSource(), "CsConvert", "cs_5_0", "rodaje-export.hlsl"),
            ShaderCompiler.Compile(ExportShaders.ConvertToneMapVariant.FullSource(), "CsConvert", "cs_5_0", "rodaje-export.hlsl"));
    }

    [Fact]
    public void Sdr_white_is_75_percent_in_hlg()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        int[] luma = Convert(gpu, ExportPixelFormat.P010, ExportColor.Hlg);

        // BT.2408: el blanco de 203 nits cae en el 75 % de la señal HLG con pico de 1000 nits → 64 + 876 × 0,75 = 721.
        Assert.InRange(luma[0], 720, 722);
        Assert.Equal(64, luma[48]);
    }

    /// <summary>Luma de la primera fila: blanco en 0..15, gris del 50 % en 16..47 y negro en 48..63.</summary>
    private static int[] Convert(ExportGpu gpu, ExportPixelFormat pixels, ExportColor color)
    {
        var format = new ExportVideoFormat(64, 16, Rate, pixels, color);
        var source = new Picture(64, 16).Fill(0, 0, 16, 16, 1, 1, 1).Fill(16, 0, 32, 16, 0.5, 0.5, 0.5).Fill(48, 0, 16, 16, 0, 0, 0);
        return ExportCheck.Convert(gpu, source, format, dither: false)[0];
    }

    private static int Limited(double signal) => (int)Math.Round(64 + (876 * signal));

    private static double Bt709Linear(double x) => x < 0.081 ? x / 4.5 : Math.Pow((x + 0.099) / 1.099, 1 / 0.45);

    private static double Pq(double nits)
    {
        double y = Math.Pow(nits / 10000, 0.1593017578125);
        return Math.Pow((0.8359375 + (18.8515625 * y)) / (1 + (18.6875 * y)), 78.84375);
    }
}
