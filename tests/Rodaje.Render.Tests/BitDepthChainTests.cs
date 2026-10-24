using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.Render.Export;
using Rodaje.Render.Tests.Export;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests;

/// <summary>
/// Mide cuántos niveles de una rampa P010 de 1024 códigos llegan a cada punto de la cadena (docs/hdr.md): el intermedio
/// del compositor, la lectura de 8 bits de la vista previa y el conversor de exportación en 10 y 8 bits.
/// </summary>
public sealed class BitDepthChainTests(ITestOutputHelper output)
{
    private const int Width = 2048;
    private const int Height = 16;

    [GpuFact]
    public void A_1024_level_p010_ramp_through_compositor_and_export_converter()
    {
        // Rango completo: el código c (0..1023) es R'G'B' = c/1023, dos columnas por código.
        var image = new YuvImage(Width, Height, tenBit: true);
        for (int x = 0; x < Width; x += 2)
        {
            image.Fill(x, 0, 2, Height, new YuvCode(x / 2, 512, 512));
        }

        var color = new VideoColor(YuvMatrix.Bt709, FullRange: true);
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, Width, Height);
        using (CompositionFrame frame = Layers.Frame(Width, Height, Layers.From(gpu, image, color)))
        {
            compositor.Render(frame);
        }

        float[] halves = compositor.ReadbackRgbaFloat();
        int intermediate = Distinct(Width, x => (int)Math.Round(halves[(((Height / 2) * Width) + x) * 4 + 1] * 1023));
        byte[] bytes = compositor.ReadbackRgba();
        int preview8 = Distinct(Width, x => bytes[(((Height / 2) * Width) + x) * 4 + 1]);

        int p010 = ExportLevels(gpu, compositor, ExportPixelFormat.P010);
        int yuv420p10 = ExportLevels(gpu, compositor, ExportPixelFormat.Yuv420P10);
        int nv12 = ExportLevels(gpu, compositor, ExportPixelFormat.Nv12);

        using var narrow = new Compositor(gpu, Width, Height, CompositeFormat.Rgba8Unorm);
        using (CompositionFrame frame = Layers.Frame(Width, Height, Layers.From(gpu, image, color)))
        {
            narrow.Render(frame);
        }

        int rgba8 = ExportLevels(gpu, narrow, ExportPixelFormat.P010);

        output.WriteLine($"Entrada: 1024 niveles. Intermedio RGBA16F: {intermediate}. Lectura de 8 bits (vista previa, miniaturas): {preview8}.");
        output.WriteLine($"Exportación BT.709 limitada (máx. 877 en 10 bits, 220 en 8): P010 {p010}, yuv420p10 {yuv420p10}, NV12 {nv12}; P010 desde un compositor RGBA8: {rgba8}.");
        Assert.True(intermediate >= 1020, $"El intermedio solo guarda {intermediate} niveles.");
        Assert.True(p010 >= 870 && yuv420p10 >= 870, $"La exportación de 10 bits solo guarda {p010}/{yuv420p10} niveles.");
        Assert.InRange(nv12, 200, 220);
        Assert.InRange(preview8, 250, 256);
        Assert.InRange(rgba8, 200, 256);
    }

    /// <summary>
    /// Un FFV1 yuv444p12 de rango completo con los 4096 códigos sube como P016 (no se baja a P010) y el intermedio conserva
    /// más de 2000 (el techo de media precisión para 12 bits es 3073).
    /// </summary>
    [HardwareFact]
    public void A_4096_level_yuv444p12_file_keeps_more_than_10_bits_in_the_intermediate()
    {
        const int width = 4096;
        const int height = 16;
        string folder = Path.Combine(Path.GetTempPath(), "rodaje-12-bits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "rampa.mkv");
            var frame = new byte[width * height * 6];
            for (int i = 0; i < width * height; i++)
            {
                BitConverter.TryWriteBytes(frame.AsSpan(i * 2), (ushort)(i % width));
                BitConverter.TryWriteBytes(frame.AsSpan((width * height * 2) + (i * 2)), (ushort)2048);
                BitConverter.TryWriteBytes(frame.AsSpan((width * height * 4) + (i * 2)), (ushort)2048);
            }

            string[] color = ["-color_range", "pc", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"];
            FfmpegTool.Default!.Run(["-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "yuv444p12le", "-s", $"{width}x{height}",
                "-r", "30", .. color, "-i", "pipe:0", "-c:v", "ffv1", .. color, path], input => input.Write(frame));

            using GpuDevice gpu = GpuDevice.Create();
            using VideoDecoder decoder = VideoDecoder.Open(path, new VideoDecoderOptions { Device = gpu });
            using DecodedFrame decoded = decoder.GetFrame(0)!;
            var payload = Assert.IsType<D3D11TexturePayload>(decoded.Payload);
            Assert.Equal(FramePixelFormat.P016, payload.PixelFormat);
            Assert.True(payload.Color.FullRange);

            using var compositor = new Compositor(gpu, width, height);
            using (var composition = new CompositionFrame(Time.Zero, width, height, [new LayerFrame(0, "rampa", decoded, LayerTransform.Identity)]))
            {
                Assert.Equal(0, compositor.RenderStrict(composition).LayersSkipped);
            }

            float[] halves = compositor.ReadbackRgbaFloat();
            int levels = Distinct(width, x => (int)Math.Round(halves[(((height / 2) * width) + x) * 4 + 1] * 4095));
            output.WriteLine($"Entrada: 4096 niveles en yuv444p12. Intermedio RGBA16F: {levels}.");
            Assert.True(levels > 2000, $"El intermedio solo guarda {levels} niveles.");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Techo del intermedio de media precisión para rampas uniformes de 10 a 16 bits en [0, 1] (como las codifica una curva).</summary>
    [Fact]
    public void Half_float_intermediate_keeps_10_bits_and_loses_from_12()
    {
        var counts = new Dictionary<int, int>();
        foreach (int bits in new[] { 10, 12, 14, 16 })
        {
            int levels = 1 << bits;
            counts[bits] = Enumerable.Range(0, levels).Select(i => (Half)(i / (double)(levels - 1))).Distinct().Count();
            // La mitad alta (0,5..1) es la que se apelmaza: media precisión tiene 1024 valores ahí.
            int upper = Enumerable.Range(levels / 2, levels / 2).Select(i => (Half)(i / (double)(levels - 1))).Distinct().Count();
            output.WriteLine($"{bits} bits: {levels} niveles → {counts[bits]} en RGBA16F ({upper} de {levels / 2} en la mitad alta).");
        }

        Assert.Equal(1024, counts[10]);
        Assert.True(counts[12] < 4096);
        Assert.True(counts[16] < 16_384);
    }

    private static int ExportLevels(GpuDevice gpu, Compositor compositor, ExportPixelFormat pixels)
    {
        var format = new ExportVideoFormat(Width, Height, new Rational(30, 1), pixels, ExportColor.Bt709Limited);
        using var converter = new ExportFrameConverter(gpu, compositor.Width, compositor.Height, format);
        converter.Submit(compositor.Texture, 0);
        int[] luma = ExportCheck.ReadOne(converter, 0)[0];
        return Distinct(Width, x => luma[((Height / 2) * Width) + x]);
    }

    private static int Distinct(int count, Func<int, int> value) => Enumerable.Range(0, count).Select(value).Distinct().Count();
}
