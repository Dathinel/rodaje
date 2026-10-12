using System.Diagnostics;
using System.Runtime.InteropServices;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media;
using Rodaje.Media.Export;
using Rodaje.Render;

namespace Rodaje.Export.Tests.Running;

/// <summary>Lo que se decide antes de exportar, sin GPU ni procesos.</summary>
public sealed class ExportPlanTests
{
    private static readonly IReadOnlyList<EncoderSpec> Encoders = ExportEncoderCandidates.All;

    private static ExportSettings H264 { get; } = new()
    {
        Video = new VideoExportSettings { Codec = ExportCodec.H264 },
        Audio = new AudioExportSettings { Codec = ExportCodec.Aac },
    };

    [Fact]
    public void The_whole_sequence_at_its_own_rate_with_the_preferred_encoders()
    {
        ExportPlan plan = ExportPlan.Create(Request(Project(Time.FromSeconds(2))), Encoders);

        Assert.Equal(("h264_nvenc", "aac_mf"), (plan.VideoEncoder!.Id, plan.AudioEncoder!.Id));
        Assert.Equal((0L, 60L), (plan.FirstFrame, plan.FrameCount));
        Assert.Equal((Time.Zero, Time.FromSeconds(2)), (plan.Start, plan.End));
        Assert.False(plan.ConvertsRate);
        Assert.Equal(new ExportVideoFormat(1920, 1080, Rational.Fps30, ExportPixelFormat.Nv12, ExportColor.Bt709Limited), plan.Video);
        Assert.Equal(new ExportAudioFormat(48_000, 2), plan.Audio);
        Assert.Contains("+faststart", plan.Arguments.Video);
        Assert.Empty(plan.VideoFilters);
        ExportExpectation expected = plan.Expectation()!;
        Assert.Equal((60L, 2.0, true), (expected.FrameCount, expected.AudioDurationSeconds, expected.FastStart));
        Assert.Equal(ExportColor.Bt709Limited, expected.Color);
    }

    [Fact]
    public void A_range_off_the_frame_grid_takes_whole_frames_of_the_output_rate()
    {
        var range = new ExportRange(Time.FromSeconds(0.51), Time.FromSeconds(1.49));
        ExportSettings settings = H264 with { Video = H264.Video! with { FrameRate = Rational.Fps25 } };

        ExportPlan plan = ExportPlan.Create(Request(Project(Time.FromSeconds(2)), settings, range), Encoders);

        Assert.True(plan.ConvertsRate);
        Assert.Equal((13L, 25L), (plan.FirstFrame, plan.FrameCount));
        Assert.Equal(Time.FromFrames(13, Rational.Fps25), plan.Start);
        Assert.Equal(Time.FromFrames(38, Rational.Fps25), plan.End);
    }

    [Fact]
    public void A_range_past_the_end_stops_at_the_end_of_the_sequence()
    {
        ExportPlan plan = ExportPlan.Create(Request(Project(Time.FromSeconds(2)), H264, new ExportRange(Time.FromSeconds(1), Time.FromSeconds(9))), Encoders);

        Assert.Equal((30L, 30L), (plan.FirstFrame, plan.FrameCount));
    }

    [Fact]
    public void Target_size_uses_the_length_of_the_range_not_of_the_sequence()
    {
        ExportSettings settings = H264 with
        {
            Video = H264.Video! with { Codec = ExportCodec.Av1, RateControl = RateControl.TargetSize(10_000_000) },
            Audio = new AudioExportSettings { Codec = ExportCodec.Opus, RateControl = new RateControl { Mode = RateControlMode.Vbr, BitrateKbps = 96 } },
            Container = ExportContainer.WebM,
        };

        ExportPlan plan = ExportPlan.Create(Request(Project(Time.FromSeconds(100)), settings, new ExportRange(Time.FromSeconds(10), Time.FromSeconds(30))), Encoders);

        int expected = RateControl.BitrateForTargetSize(10_000_000, TimeSpan.FromSeconds(20), 96);
        Assert.Equal(3824, expected);
        Assert.Contains($"{expected}k", plan.Arguments.Video);
        Assert.Equal(2, plan.Arguments.PassCount);
    }

    [Fact]
    public void Chapters_come_from_the_markers_inside_the_range()
    {
        Project project = Project(Time.FromSeconds(60));
        project.Sequences[0].Markers.AddRange(
        [
            new Marker { Id = "a", Time = Time.FromSeconds(5), Name = "Antes" },
            new Marker { Id = "b", Time = Time.FromSeconds(20), Name = " Uno " },
            new Marker { Id = "c", Time = Time.FromSeconds(35), Name = "" },
            new Marker { Id = "d", Time = Time.FromSeconds(50), Name = "Fuera" },
        ]);

        ExportPlan plan = ExportPlan.Create(Request(project, H264 with { ChaptersFromMarkers = true }, new ExportRange(Time.FromSeconds(10), Time.FromSeconds(40))), Encoders);

        Assert.Equal(
        [
            new ExportChapter(Time.Zero, Time.FromSeconds(10), "Inicio"),
            new ExportChapter(Time.FromSeconds(10), Time.FromSeconds(25), "Uno"),
            new ExportChapter(Time.FromSeconds(25), Time.FromSeconds(30), "Capítulo 3"),
        ], plan.Metadata.Chapters);
        Assert.Equal(3, plan.Expectation()!.Chapters);
        Assert.Empty(ExportPlan.Create(Request(project, H264), Encoders).Metadata.Chapters);
        Assert.Empty(ExportPlan.Chapters([], Time.Zero, Time.FromSeconds(1)));
        Assert.Single(ExportPlan.Chapters([new Marker { Time = Time.Zero, Name = "a" }, new Marker { Time = Time.Zero, Name = "b" }], Time.Zero, Time.FromSeconds(1)));
    }

    [Fact]
    public void Soft_subtitles_are_cut_to_the_range_and_embedded_alone_hides_the_burned_ones()
    {
        Project project = Project(Time.FromSeconds(60));
        var subtitles = new Track { Id = "subs", Kind = TrackKind.Video, Name = "Inglés", Role = TrackRoles.Subtitles };
        foreach ((string id, double start, double length, string text) in ((string, double, double, string)[])[("s1", 5, 10, "Cruza el inicio"), ("s2", 20, 2, "Dentro\ncon salto"), ("s3", 45, 2, "Fuera")])
        {
            subtitles.Clips.Add(new Clip
            {
                Id = id, Start = Time.FromSeconds(start), SourceIn = Time.Zero, SourceOut = Time.FromSeconds(length),
                Generator = new ClipGenerator { Text = text, Style = new TextStyle() },
            });
        }

        project.Sequences[0].VideoTracks.Add(subtitles);
        var range = new ExportRange(Time.FromSeconds(10), Time.FromSeconds(40));

        ExportPlan both = ExportPlan.Create(Request(project, H264 with { Subtitles = SubtitleExport.Both }, range), Encoders);
        ExportSubtitleTrack track = Assert.Single(both.Metadata.Subtitles);
        Assert.Equal("Inglés", track.Name);
        Assert.Equal(
        [
            new Rodaje.Core.Editing.SubtitleCue(Time.Zero, Time.FromSeconds(5), "Cruza el inicio"),
            new Rodaje.Core.Editing.SubtitleCue(Time.FromSeconds(10), Time.FromSeconds(12), "Dentro\ncon salto"),
        ], track.Cues);
        Assert.Equal(1, both.Expectation()!.SubtitleStreams);
        Assert.False(both.Request.Project.Sequences[0].VideoTracks[^1].Hidden);

        ExportPlan embedded = ExportPlan.Create(Request(project, H264 with { Subtitles = SubtitleExport.Embedded }, range), Encoders);
        Assert.True(embedded.Request.Project.Sequences[0].VideoTracks[^1].Hidden);
        Assert.False(subtitles.Hidden);

        Assert.Empty(ExportPlan.Create(Request(project, H264, range), Encoders).Metadata.Subtitles);
        Assert.Empty(ExportPlan.Create(Request(project, H264 with { Container = ExportContainer.MpegTs, Subtitles = SubtitleExport.Both }, range), Encoders).Metadata.Subtitles);
    }

    [Fact]
    public void The_sink_maps_each_subtitle_file_with_the_codec_of_the_container()
    {
        var format = new ExportVideoFormat(320, 180, Rational.Fps30, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        EncoderSpec h264 = Encoders.First(e => e.Id == "h264_nvenc");
        var metadata = new ExportMetadata
        {
            Subtitles = [new ExportSubtitleTrack("Español", [new(Time.Zero, Time.FromSeconds(1), "Hola")])],
        };
        foreach ((ExportContainer container, string codec) in ((ExportContainer, string)[])[(ExportContainer.Mp4, "mov_text"), (ExportContainer.Mkv, "srt"), (ExportContainer.WebM, "webvtt")])
        {
            var request = new ExportSinkRequest("salida", container, format, h264, [], null, null, null, [], 30) { Metadata = metadata };
            List<string> line = [.. FfmpegPipeArguments.Build(request, "salida.tmp", null, ["a.0.srt"])];
            string joined = string.Join(' ', line);
            Assert.Contains("-f srt -i a.0.srt", joined, StringComparison.Ordinal);
            Assert.Contains("-map 1:s:0", joined, StringComparison.Ordinal);
            Assert.Equal(codec, line[line.IndexOf("-c:s") + 1]);
            Assert.Equal("title=Español", line[line.IndexOf("-metadata:s:s:0") + 1]);
        }
    }

    [Fact]
    public void Containers_without_chapters_and_image_outputs_are_not_checked_for_them()
    {
        Project project = Project(Time.FromSeconds(2));
        project.Sequences[0].Markers.Add(new Marker { Id = "a", Time = Time.FromSeconds(1), Name = "Uno" });
        ExportSettings gif = ExportPresets.Gif.Settings with { ChaptersFromMarkers = true };

        ExportPlan plan = ExportPlan.Create(Request(project, gif), Encoders);

        Assert.Empty(plan.Metadata.Chapters);
        Assert.Null(plan.Expectation());
        Assert.Contains("palettegen", plan.VideoFilters[0], StringComparison.Ordinal);
        Assert.Equal((480, 270, ExportPixelFormat.Rgb24), (plan.Video!.Width, plan.Video.Height, plan.Video.PixelFormat));
    }

    [Fact]
    public void Audio_only_has_no_video_and_checks_only_the_audio()
    {
        ExportPlan plan = ExportPlan.Create(Request(Project(Time.FromSeconds(3)), ExportPresets.AudioOnly.Settings), Encoders);

        Assert.Null(plan.Video);
        ExportExpectation expected = plan.Expectation()!;
        Assert.Equal((null, null, 3.0, true), (expected.FrameCount, expected.Color, expected.AudioDurationSeconds, expected.FastStart));
    }

    [Fact]
    public void Ten_bit_square_and_vertical_shapes()
    {
        Project project = Project(Time.FromSeconds(1));
        Assert.Equal(ExportPixelFormat.P010, ExportPlan.Create(Request(project, ExportPresets.BestQuality.Settings), Encoders).Video!.PixelFormat);
        Assert.Equal((1080, 1080), Size(ExportPlan.Create(Request(project, ExportPresets.Square.Settings), Encoders)));
        Assert.Equal((1080, 1920), Size(ExportPlan.Create(Request(project, ExportPresets.Vertical.Settings), Encoders)));
        ExportSettings oneSide = H264 with { Video = H264.Video! with { Width = 1281 } };
        Assert.Equal((1280, 720), Size(ExportPlan.Create(Request(project, oneSide), Encoders)));
        Assert.Contains(ExportPresets.Square, ExportPresets.All);

        static (int, int) Size(ExportPlan plan) => (plan.Video!.Width, plan.Video.Height);
    }

    [Fact]
    public void A_crop_exports_its_region_at_its_own_size_unless_a_size_is_asked()
    {
        Project project = Project(Time.FromSeconds(1));
        ExportSettings cropped = H264 with { Video = H264.Video! with { Crop = new ExportCrop(480, 270, 960, 540) } };
        ExportVideoFormat own = ExportPlan.Create(Request(project, cropped), Encoders).Video!;
        Assert.Equal((960, 540), (own.Width, own.Height));
        ExportPlan scaled = ExportPlan.Create(Request(project, cropped with { Video = cropped.Video! with { Width = 1920 } }), Encoders);
        Assert.Equal((1920, 1080), (scaled.Video!.Width, scaled.Video.Height));
    }

    [Fact]
    public void What_cannot_be_exported_says_why()
    {
        Project project = Project(Time.FromSeconds(2));
        Code(ExportErrorCode.InvalidSettings, () => ExportPlan.Create(Request(project, new ExportSettings()), Encoders));
        Code(ExportErrorCode.InvalidSettings, () => ExportPlan.Create(Request(project, H264 with { Video = H264.Video! with { Crop = new ExportCrop(1800, 0, 200, 100) } }), Encoders));
        Code(ExportErrorCode.InvalidSettings, () => ExportPlan.Create(Request(project, H264, new ExportRange(Time.FromSeconds(3), Time.FromSeconds(4))), Encoders));
        Code(ExportErrorCode.InvalidSettings, () => ExportPlan.Create(Request(project, H264, new ExportRange(Time.FromTicks(1), Time.FromTicks(2))), Encoders));
        Code(ExportErrorCode.InvalidSettings, () => ExportPlan.Create(new ExportRequest(project, "otra", "x.mp4", H264), Encoders));
        Code(ExportErrorCode.InvalidSettings, () => ExportPlan.Create(Request(project, H264 with { Container = ExportContainer.WebM }), Encoders));
        Code(ExportErrorCode.EncoderMissing, () => ExportPlan.Create(Request(project, H264), [ExportEncoderCandidates.AacMf]));
        ExportException missing = Code(ExportErrorCode.EncoderMissing,
            () => ExportPlan.Create(Request(project, H264 with { Video = H264.Video! with { EncoderId = "h264_amf" } }), Encoders));
        Assert.Equal("h264_amf", missing.Culprit);
    }

    [Fact]
    public void Kvazaar_and_other_encoders_out_of_the_defaults_are_used_only_when_asked()
    {
        EncoderSpec kvazaar = ExportEncoderCandidates.H264Nvenc with { Id = "libkvazaar", Codec = ExportCodec.Hevc, InDefaults = false };
        EncoderSpec[] available = [kvazaar, ExportEncoderCandidates.HevcNvenc];

        Assert.Equal("hevc_nvenc", ExportPlan.Choose(ExportCodec.Hevc, null, available).Id);
        Assert.Equal("libkvazaar", ExportPlan.Choose(ExportCodec.Hevc, "libkvazaar", available).Id);
        Assert.Throws<ExportException>(() => ExportPlan.Choose(ExportCodec.Hevc, null, [kvazaar]));
    }

    [Fact]
#pragma warning disable CA2201 // Las excepciones del runtime se crean a propósito para ver cómo se traducen.
    public void Errors_map_to_stable_codes()
    {
        Assert.Equal(ExportErrorCode.GpuFailed, ExportErrors.Map(new RenderException("x"), 7).Code);
        Assert.Equal(7, ExportErrors.Map(new RenderException("x"), 7).Frame);
        Assert.Equal(ExportErrorCode.GpuFailed, ExportErrors.Map(new COMException("perdida", unchecked((int)0x887A0005))).Code);
        Assert.Equal(ExportErrorCode.Unknown, ExportErrors.Map(new COMException("otra", 5)).Code);
        Assert.Equal(ExportErrorCode.GpuFailed, ExportErrors.Map(new OutOfMemoryException()).Code);
        Assert.Equal(ExportErrorCode.EncoderMissing, ExportErrors.Map(new FfmpegNotFoundException("falta")).Code);
        Assert.Equal(ExportErrorCode.DiskFull, ExportErrors.Map(new IOException("lleno", unchecked((int)0x80070070))).Code);
        Assert.Equal(ExportErrorCode.OutputNotWritable, ExportErrors.Map(new IOException("en uso")).Code);
        Assert.Equal(ExportErrorCode.OutputNotWritable, ExportErrors.Map(new UnauthorizedAccessException()).Code);
        Assert.Equal(ExportErrorCode.Canceled, ExportErrors.Map(new OperationCanceledException()).Code);
        Assert.Equal(ExportErrorCode.GpuFailed, ExportErrors.Map(new AggregateException(new RenderException("x"))).Code);
        Assert.Equal(ExportErrorCode.Unknown, ExportErrors.Map(new InvalidOperationException("x")).Code);

        var original = new ExportException(ExportErrorCode.EffectFailed, "efecto") { Culprit = "lut" };
        ExportException located = ExportErrors.Map(original, 12);
        Assert.Equal((ExportErrorCode.EffectFailed, 12L, "lut", "efecto"), (located.Code, located.Frame!.Value, located.Culprit, located.Message));
        Assert.Same(original, ExportErrors.Map(original));
        Assert.Same(located, ExportErrors.Map(located, 13));
    }
#pragma warning restore CA2201

    [Fact]
    public void Performance_modes_share_the_machine_differently()
    {
        ExportPerformanceProfile fast = ExportPerformanceProfile.For(ExportPerformance.Fastest, 8L << 30);
        ExportPerformanceProfile background = ExportPerformanceProfile.For(ExportPerformance.Background, 8L << 30);

        Assert.Equal((ThreadPriority.Normal, ProcessPriorityClass.Normal, true, false), (fast.ThreadPriority, fast.EncoderPriority, fast.PausePreview, fast.PreviewProxies));
        Assert.Equal((ThreadPriority.BelowNormal, ProcessPriorityClass.BelowNormal, false), (background.ThreadPriority, background.EncoderPriority, background.PausePreview));
        Assert.True(background.LookaheadFrames < fast.LookaheadFrames && background.MaxDecodersPerMedia < fast.MaxDecodersPerMedia);
        Assert.True(ExportPerformanceProfile.For(ExportPerformance.Background, (4L << 30) - 1).PreviewProxies);
        Assert.False(ExportPerformanceProfile.For(ExportPerformance.Background, 4L << 30).PreviewProxies);
        Assert.False(ExportPerformanceProfile.For(ExportPerformance.Fastest, 0).PreviewProxies);
    }

    [Fact]
    public void The_probe_encodes_with_the_first_pixel_format_and_profile_of_each_candidate()
    {
        string prores = string.Join(' ', ProbedEncoderSource.ProbeArguments(ExportEncoderCandidates.ProRes));
        Assert.Contains("-c:v prores_ks -pix_fmt yuv422p10le -profile:v proxy", prores, StringComparison.Ordinal);
        Assert.EndsWith("-f null -", prores, StringComparison.Ordinal);
        string aac = string.Join(' ', ProbedEncoderSource.ProbeArguments(ExportEncoderCandidates.AacMf));
        Assert.Contains("sample_rate=48000", aac, StringComparison.Ordinal);
        Assert.Contains("-c:a aac_mf", aac, StringComparison.Ordinal);
        Assert.Contains("sample_rate=44100", string.Join(' ', ProbedEncoderSource.ProbeArguments(ExportEncoderCandidates.Mp3 with { SampleRates = [44_100] })), StringComparison.Ordinal);
    }

    internal static Project Project(Time duration)
    {
        var sequence = new Sequence { Id = "sec", Name = "Principal", FrameRate = Rational.Fps30, Width = 1920, Height = 1080 };
        sequence.VideoTracks.Add(new Track
        {
            Id = "v1", Kind = TrackKind.Video, Name = "Video 1",
            Clips = { new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = duration } },
        });
        var project = new Project { Id = "p", Name = "Plan", Sequences = [sequence] };
        project.Media.Add(new MediaRef { Id = "m", Path = "falta.mp4" });
        return project;
    }

    internal static ExportRequest Request(Project project, ExportSettings? settings = null, ExportRange? range = null) =>
        new(project, "sec", "salida.mp4", settings ?? H264, range);

    private static ExportException Code(ExportErrorCode code, Action action)
    {
        ExportException error = Assert.Throws<ExportException>(action);
        Assert.Equal(code, error.Code);
        return error;
    }
}
