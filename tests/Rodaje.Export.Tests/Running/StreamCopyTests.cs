using System.Diagnostics;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.Running;

/// <summary>
/// Copia directa («smart render»): un corte sin cambios en keyframes se copia sin recodificar y cada cuadro es el del
/// original; lo demás se recodifica con el motivo.
/// </summary>
[Collection(ExportGpuGroup.Name)]
public sealed class StreamCopyTests(ITestOutputHelper output)
{
    // H.264 1080p a 29,97 con GOP 60 y cuadros B: keyframes en 0, 60, 120…
    private static readonly MediaSpec Source = TestMediaPresets.H264Ntsc30BFrames;

    // El catálogo como lo deja la sonda en una RTX 3050: sin AMD, sin Intel y sin el AV1 de NVENC (solo RTX 40).
    private static readonly IReadOnlyList<EncoderSpec> Encoders =
        [.. ExportEncoderCatalog.All.Where(e => e.Vendor is not (EncoderVendor.Amd or EncoderVendor.Intel) && e.Id != "av1_nvenc")];

    private static readonly ExportSettings H264 = new() { Video = new VideoExportSettings { Codec = ExportCodec.H264 } };

    [ExportFact]
    public async Task A_cut_on_keyframes_is_copied_frame_by_frame()
    {
        (Project project, string source) = Cut(60, 240);
        string path = Path.Combine(Path.GetTempPath(), $"rodaje-copia-{Guid.NewGuid():N}.mp4");
        try
        {
            IReadOnlyList<EncoderSpec> encoders = await new CatalogEncoderSource(Skip.ProxyExecutable!).GetAsync();
            var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable! });
            var watch = Stopwatch.StartNew();
            using ExportJob job = runner.Start(new ExportRequest(project, "sec", path, H264), encoders);
            await job.Completion.WaitAsync(TimeSpan.FromMinutes(2));
            watch.Stop();

            Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
            output.WriteLine($"{job.StreamCopy!.Explanation} {watch.Elapsed.TotalSeconds:0.00} s; {job.Report!.Video}");
            Assert.True(job.StreamCopy.Copies);
            Assert.Equal(240L, job.Report.Video!.FrameCount);
            Assert.Equal(("bt709", "tv"), (job.Report.Video.ColorSpace, job.Report.Video.ColorRange));

            using VideoDecoder copied = VideoDecoder.Open(path);
            using VideoDecoder original = VideoDecoder.Open(source);
            foreach (long n in new long[] { 0, 1, 2, 59, 60, 61, 150, 239 })
            {
                using DecodedFrame a = copied.GetFrame(n)!;
                using DecodedFrame b = original.GetFrame(60 + n)!;
                Assert.Equal(FrameReadback.ReadLuma(b).Pixels, FrameReadback.ReadLuma(a).Pixels);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [ExportFact]
    public void A_cut_off_a_keyframe_is_reencoded_and_says_why()
    {
        (Project project, _) = Cut(61, 239);
        ExportPlan plan = ExportPlan.Create(new ExportRequest(project, "sec", "x.mp4", H264), Encoders);

        StreamCopyDecision decision = StreamCopyPlanner.Decide(plan);

        Assert.False(decision.Copies);
        Assert.Contains("keyframe", decision.Explanation, StringComparison.Ordinal);
    }

    [ExportFact]
    public void Changes_or_a_requested_bitrate_are_reencoded()
    {
        (Project project, _) = Cut(60, 240);
        Clip clip = project.Sequences[0].VideoTracks[0].Clips[0];

        Assert.True(Decide(project, H264).Copies);
        Assert.Contains("tasa de bits", Decide(project, H264 with { Video = H264.Video! with { RateControl = RateControl.Cbr(8000) } }).Explanation,
            StringComparison.Ordinal);
        Assert.Contains("HEVC", Decide(project, H264 with { Video = H264.Video! with { Codec = ExportCodec.Hevc } }).Explanation, StringComparison.Ordinal);
        Assert.Contains("desactivada", Decide(project, H264 with { SmartRender = false }).Explanation, StringComparison.Ordinal);
        Assert.Contains("tamaño", Decide(project, H264 with { Video = H264.Video! with { Width = 1280, Height = 720 } }).Explanation,
            StringComparison.Ordinal);

        clip.Effects.Add(new EffectInstance { Id = "c", Type = BuiltInEffects.Color });
        Assert.Contains("efectos", Decide(project, H264).Explanation, StringComparison.Ordinal);
        clip.Effects.Clear();
        clip.Speed = new ClipSpeed { Rate = new Rational(1, 1), Reverse = true };
        clip.Duration = clip.SourceDuration;
        Assert.Contains("velocidad", Decide(project, H264).Explanation, StringComparison.Ordinal);
    }

    private static StreamCopyDecision Decide(Project project, ExportSettings settings) =>
        StreamCopyPlanner.Decide(ExportPlan.Create(new ExportRequest(project, "sec", "x.mp4", settings), Encoders));

    private static (Project Project, string Source) Cut(long firstFrame, long frames)
    {
        string source = TestMediaGenerator.CreateDefault()!.GetOrCreate(Source).Path;
        var media = new MediaRef { Id = "m", Path = source };
        MediaProbe.Probe(source).ApplyTo(media);
        var sequence = new Sequence { Id = "sec", Name = "Corte", FrameRate = Source.FrameRate, Width = Source.Width, Height = Source.Height };
        var track = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        track.Clips.Add(new Clip
        {
            Id = "c", MediaId = "m", Start = Time.Zero,
            SourceIn = Time.FromFrames(firstFrame, Source.FrameRate), SourceOut = Time.FromFrames(firstFrame + frames, Source.FrameRate),
        });
        sequence.VideoTracks.Add(track);
        return (new Project { Id = "p", Name = "Corte", Sequences = [sequence], Media = [media] }, source);
    }
}
