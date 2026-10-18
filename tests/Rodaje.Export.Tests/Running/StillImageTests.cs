using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.TestMedia;

namespace Rodaje.Export.Tests.Running;

[Collection(ExportGpuGroup.Name)]
public sealed class StillImageTests
{
    [HardwareFact]
    public async Task A_two_second_photo_exports_sixty_visible_frames()
    {
        using var image = new StillImageFile();
        var media = new MediaRef { Id = "foto", Path = image.Path };
        MediaProbe.Probe(image.Path).ApplyTo(media);
        var sequence = new Sequence
        {
            Id = "s", Width = 320, Height = 240, FrameRate = Rational.Fps30,
            VideoTracks = [new Track { Id = "v", Kind = TrackKind.Video, Clips = [new Clip { Id = "c", MediaId = media.Id, SourceOut = Time.FromSeconds(2) }] }],
        };
        var project = new Project { Id = "p", Media = [media], Sequences = [sequence] };
        string path = Path.Combine(Path.GetDirectoryName(image.Path)!, "salida.mkv");
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = FfmpegExecutable.Locate() });
        var settings = new ExportSettings
        {
            Container = ExportContainer.Mkv,
            Video = new VideoExportSettings { Codec = ExportCodec.Ffv1, EncoderId = "ffv1", RateControl = RateControl.Lossless },
        };
        using ExportJob job = runner.Start(new ExportRequest(project, sequence.Id, path, settings), [ExportEncoderCatalog.Find("ffv1")!]);
        await job.Completion;
        Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
        Assert.False(job.StreamCopy!.Copies);
        Assert.Contains("imagen fija", job.StreamCopy.Explanation, StringComparison.Ordinal);
        Assert.Equal(60, job.Report!.Video!.FrameCount);
        int count = FfmpegTool.Default!.DecodeGrayFrames(path, 320, 240, (index, pixels) =>
        {
            Assert.True(pixels[(120 * 320) + 280] > 40, $"Cuadro {index}: la foto debe seguir visible.");
        });
        Assert.Equal(60, count);
    }
}
