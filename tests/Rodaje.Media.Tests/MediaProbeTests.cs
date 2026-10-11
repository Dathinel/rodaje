using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Tests.Support;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests;

public sealed class MediaProbeTests
{
    [FfmpegLibraryFact]
    public void H264_with_audio_reports_exact_values()
    {
        MediaInfo info = MediaProbe.Probe(TestClips.PathOf(TestClips.H264));
        VideoStreamInfo video = Assert.IsType<VideoStreamInfo>(info.Video);
        AudioStreamInfo audio = Assert.IsType<AudioStreamInfo>(info.Audio);

        Assert.Equal("h264", video.Codec);
        Assert.Equal(new Rational(30000, 1001), video.FrameRate);
        Assert.False(video.IsVariableFrameRate);
        Assert.Equal(300, video.FrameCount);
        Assert.Equal(Time.FromFrames(300, video.FrameRate), video.Duration);
        Assert.Equal((640, 360), (video.Width, video.Height));
        Assert.Equal((640, 360), (video.DisplayWidth, video.DisplayHeight));
        Assert.Equal(0, video.Rotation);
        Assert.Equal(new Rational(1, 1), video.SampleAspectRatio);
        Assert.Equal(8, video.BitDepth);
        Assert.Equal("yuv420p", video.PixelFormat);
        Assert.Equal(Time.Zero, video.StartTime);

        Assert.Equal("aac", audio.Codec);
        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.True(info.Duration >= video.Duration);
        Assert.True(info.Duration - video.Duration < Time.FromSeconds(0.05), $"Duración {info.Duration} frente a {video.Duration}");
    }

    [FfmpegLibraryFact]
    public void Probe_fills_the_media_reference()
    {
        MediaInfo info = MediaProbe.Probe(TestClips.PathOf(TestClips.H264));
        var media = new MediaRef { Id = "m1", Path = "clip.mp4" };
        info.ApplyTo(media);

        Assert.Equal(info.Duration, media.Duration);
        Assert.True(media.HasVideo);
        Assert.True(media.HasAudio);
        Assert.Equal(new Rational(30000, 1001), media.FrameRate);
        Assert.Equal((640, 360), (media.Width, media.Height));
        Assert.Equal((48000, 2), (media.SampleRate, media.AudioChannels));
        Assert.Equal(TestClips.H264.Name, media.Name);
    }

    [FfmpegLibraryFact(Encoder = TestClips.TenBitEncoder)]
    public void Hevc_main10_reports_ten_bits()
    {
        VideoStreamInfo video = MediaProbe.Probe(TestClips.PathOf(TestClips.Hevc10)).Video!;
        Assert.Equal("hevc", video.Codec);
        Assert.Equal(10, video.BitDepth);
        Assert.Equal(new Rational(25, 1), video.FrameRate);
        Assert.Equal(120, video.FrameCount);
        Assert.False(video.IsVariableFrameRate);
    }

    [FfmpegLibraryFact]
    public void Matroska_without_frame_count_estimates_it_from_the_duration()
    {
        MediaInfo info = MediaProbe.Probe(TestClips.PathOf(TestClips.H264Mkv));
        Assert.StartsWith("matroska", info.ContainerFormat, StringComparison.Ordinal);
        Assert.Equal(new Rational(25, 1), info.Video!.FrameRate);
        Assert.Equal(200, info.Video.FrameCount);
        Assert.False(info.HasAudio);
    }

    [FfmpegLibraryFact]
    public void Rotation_and_sample_aspect_ratio_change_the_display_size()
    {
        VideoStreamInfo video = MediaProbe.Probe(TestClips.RotatedAnamorphic()).Video!;
        Assert.Equal(90, video.Rotation);
        Assert.Equal(new Rational(4, 3), video.SampleAspectRatio);
        Assert.Equal((640, 360), (video.Width, video.Height));
        Assert.Equal((360, 853), (video.DisplayWidth, video.DisplayHeight));
    }

    [FfmpegLibraryFact]
    public void Variable_frame_rate_is_flagged()
    {
        VideoStreamInfo video = MediaProbe.Probe(TestClips.VariableFrameRate()).Video!;
        Assert.True(video.IsVariableFrameRate);
        Assert.Equal(90, video.FrameCount);
    }

    [FfmpegLibraryFact]
    public void Audio_only_file_has_no_video()
    {
        MediaInfo info = MediaProbe.Probe(TestClips.MonoAudio44k());
        Assert.False(info.HasVideo);
        Assert.Equal((44100, 1), (info.Audio!.SampleRate, info.Audio.Channels));
        Assert.InRange(info.Duration.ToSeconds(), 3.95, 4.1);
        var media = new MediaRef();
        info.ApplyTo(media);
        Assert.Null(media.FrameRate);
        Assert.Equal(0, media.Width);
    }

    [FfmpegLibraryFact]
    public void Missing_or_invalid_files_fail_with_a_clear_error()
    {
        Assert.Throws<FileNotFoundException>(() => MediaProbe.Probe(Path.Combine(Path.GetTempPath(), "no-existe-rodaje.mp4")));
        string junk = Path.Combine(MediaTestEnvironment.CacheDirectory, "basura.mp4");
        File.WriteAllBytes(junk, new byte[4096]);
        MediaException error = Assert.Throws<MediaException>(() => MediaProbe.Probe(junk));
        Assert.Contains("basura.mp4", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2997, 100, 30000, 1001)]
    [InlineData(23976, 1000, 24000, 1001)]
    [InlineData(25, 1, 25, 1)]
    [InlineData(12800, 512, 25, 1)]
    [InlineData(1001, 37, 1001, 37)]
    public void Measured_rates_snap_to_standard_ones(long num, long den, long expectedNum, long expectedDen) =>
        Assert.Equal(new Rational(expectedNum, expectedDen), MediaProbe.SnapFrameRate(new Rational(num, den)));

    [Fact]
    public void Frame_rate_sanity_rules()
    {
        Assert.Equal(new Rational(30000, 1001), MediaProbe.ChooseFrameRate(new Rational(30000, 1001), new Rational(30000, 1001)));
        Assert.Equal(new Rational(29, 1), MediaProbe.ChooseFrameRate(new Rational(29, 1), new Rational(30, 1)));
        Assert.Equal(new Rational(25, 1), MediaProbe.ChooseFrameRate(null, new Rational(25, 1)));
        Assert.Equal(new Rational(25, 1), MediaProbe.ChooseFrameRate(new Rational(90000, 1), new Rational(25, 1)));
        Assert.Null(MediaProbe.ChooseFrameRate(new Rational(1, 2), null));
    }

    [Fact]
    public void Measured_rate_of_a_variable_stream_passes_the_same_sanity_rules()
    {
        Rational avg = new(2987, 100);
        Rational real = new(30, 1);
        Assert.Equal(new Rational(30000, 1001), MediaProbe.ResolveFrameRate(avg, real, new Rational(2997, 100), variable: true));
        Assert.Equal(new Rational(30, 1), MediaProbe.ResolveFrameRate(new Rational(30, 1), real, new Rational(25, 1), variable: false));
        Assert.Equal(new Rational(25, 1), MediaProbe.ResolveFrameRate(null, null, new Rational(25, 1), variable: false));

        // Marcas casi pegadas (intervalo de 1 en base 1/90000) o muy separadas: la medida no es una tasa creíble.
        Assert.Equal(avg, MediaProbe.ResolveFrameRate(avg, real, new Rational(90000, 1), variable: true));
        Assert.Equal(real, MediaProbe.ResolveFrameRate(null, real, new Rational(1, 3), variable: true));
        Assert.Equal(new Rational(2987, 100), MediaProbe.ResolveFrameRate(new Rational(2987, 100), new Rational(90000, 1), new Rational(48000, 1), variable: true));
        Assert.Null(MediaProbe.ResolveFrameRate(null, null, new Rational(5000, 1), variable: true));
    }
}
