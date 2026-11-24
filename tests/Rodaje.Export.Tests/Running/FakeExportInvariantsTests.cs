using System.Runtime.CompilerServices;
using Rodaje.Core.Timing;
using Rodaje.Export.Tests.Cpu;
using Rodaje.Export.Tests.Support;
using Rodaje.Media.Export;
using Rodaje.Media.Tests.Export;

namespace Rodaje.Export.Tests.Running;

public sealed class FakeExportInvariantsTests : IDisposable
{
    private readonly string _folder = Scratch();

    public FakeExportInvariantsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("con espacios.mkv", false)]
    [InlineData("edición ñ.mkv", false)]
    [InlineData("100% terminado.mkv", false)]
    [InlineData("con espacios.mkv", true)]
    [InlineData("edición ñ.mkv", true)]
    [InlineData("100% terminado.mkv", true)]
    public void Canceling_after_half_the_frames_keeps_the_final_name_unpublished_or_its_old_bytes(string name, bool existing)
    {
        string path = Path.Combine(_folder, name);
        byte[] previous = [0x12, 0x34, 0x56];
        if (existing)
        {
            File.WriteAllBytes(path, previous);
        }

        ExportSinkRequest request = Request(path) with
        {
            Video = new ExportVideoFormat(1024, 1024, Rational.Fps30, ExportPixelFormat.Yuv420P, ExportColor.For(1024, 1024)),
        };
        using var cancellation = new CancellationTokenSource();
        using FfmpegPipeSink sink = FakeFfmpeg.Sink(new FfmpegPipeSinkOptions { WriteChunkBytes = 1024 * 1024 });
        try
        {
            sink.Start(request, cancellation.Token);
            LavfiFrames frames = LavfiFrames.Synthetic(request.Video!, 1);
            for (int frame = 0; frame < 2; frame++)
            {
                sink.WriteVideo(frames.Frame(frame), cancellation.Token);
            }

            Assert.Equal(2, sink.FramesWritten);
            // Tres bloques de tubería obligan al falso a consumir y escribir datos antes de cancelar, sin esperas de reloj.
            Assert.True(new FileInfo(ExportOutputGuard.TemporaryPath(path)).Length > 0);
            if (existing)
            {
                Assert.Equal(previous, File.ReadAllBytes(path));
            }
            else
            {
                Assert.False(File.Exists(path));
            }

            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => sink.WriteVideo(frames.Frame(2), cancellation.Token));
        }
        finally
        {
            sink.Abort();
            _ = CanceledSinkCleanup.Wait(sink);
        }

        Assert.Null(sink.ProcessId);
        Assert.Equal(existing ? new[] { path } : [], Directory.GetFiles(_folder));
        if (existing)
        {
            Assert.Equal(previous, File.ReadAllBytes(path));
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    public void A_fake_encoder_error_keeps_the_exit_code_diagnostic_and_removes_partial_output(int exitCode)
    {
        string path = Path.Combine(_folder, "fallo del codificador.mkv");
        ExportSinkRequest request = Request(path, FakeFfmpeg.ExitCode, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using FfmpegPipeSink sink = FakeFfmpeg.Sink();
        try
        {
            sink.Start(request, CancellationToken.None);
            LavfiFrames frames = LavfiFrames.Synthetic(request.Video!, 1);
            for (int frame = 0; frame < 4; frame++)
            {
                sink.WriteVideo(frames.Frame(frame), CancellationToken.None);
            }

            ExportException error = Assert.Throws<ExportException>(() => sink.Complete(CancellationToken.None));
            Assert.Equal(ExportErrorCode.EncoderFailed, error.Code);
            Assert.StartsWith($"ffmpeg.exe terminó con el código {exitCode}. (ffmpeg: Error falso de ffmpeg (", error.Message, StringComparison.Ordinal);
            Assert.EndsWith($" {exitCode}).)", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            sink.Abort();
            _ = CanceledSinkCleanup.Wait(sink);
        }

        Assert.Null(sink.ProcessId);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData("edición ñ con espacios.mkv")]
    [InlineData("100% listo.mkv")]
    public void A_completed_fake_export_contains_exactly_the_supplied_frame_bytes(string name)
    {
        string path = Path.Combine(_folder, name);
        ExportSinkRequest request = Request(path);
        using FfmpegPipeSink sink = FakeFfmpeg.Sink();
        sink.Start(request, CancellationToken.None);
        LavfiFrames frames = LavfiFrames.Synthetic(request.Video!, 2);
        for (int frame = 0; frame < 4; frame++)
        {
            sink.WriteVideo(frames.Frame(frame), CancellationToken.None);
        }

        ExportSinkResult result = sink.Complete(CancellationToken.None);
        byte[] expected = [.. frames.Packed(0).ToArray(), .. frames.Packed(1).ToArray(),
            .. frames.Packed(0).ToArray(), .. frames.Packed(1).ToArray()];
        Assert.Equal(path, result.OutputPath);
        Assert.Equal(4, result.FramesWritten);
        Assert.Equal(expected.Length, result.Bytes);
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Null(sink.ProcessId);
        Assert.Equal([path], Directory.GetFiles(_folder));
    }

    private static ExportSinkRequest Request(string path, params string[] extra) => new(path, ExportContainer.Mkv,
        new ExportVideoFormat(64, 64, Rational.Fps30, ExportPixelFormat.Yuv420P, ExportColor.For(64, 64)),
        TestEncoders.H264Nvenc with { Id = "ffv1", Codec = ExportCodec.Ffv1 }, extra, null, null, null, [], 4);

    private static string Scratch([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, ".fake-export-tests-" + Guid.NewGuid().ToString("N"));
}
