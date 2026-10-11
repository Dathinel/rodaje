using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Con un codificador sin pérdidas, lo que sale del archivo es byte a byte lo que entró por la tubería: el sink quita el
/// pitch de cada plano y no cambia el orden de filas, planos ni cuadros. Cuadros de lavfi con relleno al final de cada fila.
/// </summary>
public sealed class CpuExportLosslessTests(ITestOutputHelper output)
{
    [CpuExportTheory]
    [InlineData(ExportPixelFormat.Yuv420P)]
    [InlineData(ExportPixelFormat.Yuv422P10)]
    [InlineData(ExportPixelFormat.Yuv444P)]
    [InlineData(ExportPixelFormat.Yuva444P10)]
    public void Ffv1_gives_back_every_byte_that_went_down_the_pipe(ExportPixelFormat pixels)
    {
        const int frames = 24;
        using var scratch = new ExportScratch();
        ExportVideoFormat format = CpuExportEnvironment.Format(320, 180, pixels);
        var source = new LavfiFrames(format, distinct: 7);
        string outputPath = scratch.Combine($"sin pérdidas {pixels}.mkv");
        var request = new ExportSinkRequest(outputPath, ExportContainer.Mkv, format, CpuExportArguments.Spec("ffv1"),
            CpuExportArguments.Video("ffv1", pixels), null, null, null, [], frames);
        using var sink = new FfmpegPipeSink(CpuExportEnvironment.Ffmpeg);

        source.Run(sink, request);

        byte[] decoded = Decode(outputPath, pixels);
        AssertSameFrames(source, decoded, frames);
        Assert.Equal(frames, new ExportVerifier(CpuExportEnvironment.FfprobePath).Inspect(outputPath).Video!.FrameCount);
    }

    [CpuExportTheory]
    [InlineData(ExportPixelFormat.Rgb24)]
    [InlineData(ExportPixelFormat.Rgba)]
    public void A_png_sequence_gives_back_every_frame(ExportPixelFormat pixels)
    {
        const int frames = 6;
        using var scratch = new ExportScratch();
        ExportVideoFormat format = new(160, 90, Rational.Fps30, pixels, ExportColor.Srgb);
        var source = new LavfiFrames(format, distinct: 4);
        string folder = scratch.Combine("cuadros png");
        var request = new ExportSinkRequest(folder, ExportContainer.ImageSequence, format, CpuExportArguments.Spec("png"), [],
            null, null, null, [], frames);
        using var sink = new FfmpegPipeSink(CpuExportEnvironment.Ffmpeg);

        ExportSinkResult result = source.Run(sink, request).Result;

        Assert.Equal(folder, result.OutputPath);
        Assert.Equal([folder], scratch.Leftovers());
        string[] files = [.. Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        Assert.Equal([.. Enumerable.Range(0, frames).Select(i => $"cuadros png_{i:000000}.png")], files);
        byte[] decoded = Decode(Path.Combine(folder, "cuadros png_%06d.png"), pixels, "-start_number", "0");
        AssertSameFrames(source, decoded, frames);
    }

    private static byte[] Decode(string input, ExportPixelFormat pixels, params string[] inputOptions) =>
        CpuExportEnvironment.RunBinary(CpuExportEnvironment.Ffmpeg.Path,
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", .. inputOptions, "-i", input,
            "-f", "rawvideo", "-pix_fmt", pixels.FfmpegName(), "pipe:1",
        ]);

    private void AssertSameFrames(LavfiFrames source, byte[] decoded, int frames)
    {
        int frameBytes = (int)source.Format.FrameBytes;
        Assert.Equal((long)frameBytes * frames, decoded.Length);
        for (int i = 0; i < frames; i++)
        {
            ReadOnlySpan<byte> expected = source.Packed(i);
            ReadOnlySpan<byte> actual = decoded.AsSpan(i * frameBytes, frameBytes);
            int mismatch = expected.CommonPrefixLength(actual);
            Assert.True(mismatch == frameBytes, $"El cuadro {i} difiere en el byte {mismatch} de {frameBytes}.");
        }

        output.WriteLine($"{frames} cuadros {source.Format.PixelFormat.FfmpegName()} idénticos ({frameBytes} bytes cada uno).");
    }
}
