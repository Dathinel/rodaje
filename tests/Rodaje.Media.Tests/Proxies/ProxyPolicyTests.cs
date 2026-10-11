using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;

namespace Rodaje.Media.Tests.Proxies;

public sealed class ProxyPolicyTests
{
    private static readonly ProxyPolicy Policy = new();

    private static ProxyCandidate Hd(string codec = "h264", int bits = 8) => new(1920, 1080, codec, bits, Rational.Ntsc30);

    [Fact]
    public void Plain_1080p_h264_needs_no_proxy()
    {
        ProxyRecommendation result = Policy.Evaluate(Hd() with { Bitrate = 20_000_000, KeyframeInterval = 60, DecodeSpeed = 8, Layers = 3 });

        Assert.False(result.IsRecommended);
        Assert.Equal(ProxyReasons.None, result.Reasons);
        Assert.Empty(result.Explanations);
    }

    [Theory]
    [InlineData(1920, 1080, false)]
    [InlineData(1920, 1088, false)]
    [InlineData(1080, 1920, false)]
    [InlineData(2048, 1080, true)]
    [InlineData(2560, 1440, true)]
    [InlineData(3840, 2160, true)]
    [InlineData(2160, 3840, true)]
    [InlineData(1440, 1440, true)]
    public void Resolution_above_1080p_recommends_a_proxy(int width, int height, bool expected)
    {
        ProxyRecommendation result = Policy.Evaluate(Hd() with { Width = width, Height = height });

        Assert.Equal(expected, result.Reasons.HasFlag(ProxyReasons.Resolution));
    }

    [Theory]
    [InlineData("hevc", 10, 1, false)]
    [InlineData("hevc", 10, 2, true)]
    [InlineData("hevc", 10, 4, true)]
    [InlineData("hevc", 8, 4, false)]
    [InlineData("h264", 10, 4, false)]
    [InlineData("HEVC", 12, 2, true)]
    public void High_bit_depth_hevc_needs_a_proxy_only_with_several_layers(string codec, int bits, int layers, bool expected)
    {
        ProxyRecommendation result = Policy.Evaluate(Hd(codec, bits) with { Layers = layers });

        Assert.Equal(expected, result.Reasons.HasFlag(ProxyReasons.HighBitDepthHevc));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(15, false)]
    [InlineData(60, false)]
    [InlineData(61, true)]
    [InlineData(250, true)]
    public void Keyframe_interval_above_60_recommends_a_proxy(int? gop, bool expected) =>
        Assert.Equal(expected, Policy.Evaluate(Hd() with { KeyframeInterval = gop }).Reasons.HasFlag(ProxyReasons.LongGop));

    [Theory]
    [InlineData(null, false)]
    [InlineData(80_000_000L, false)]
    [InlineData(80_000_001L, true)]
    [InlineData(400_000_000L, true)]
    public void Bitrate_above_the_threshold_recommends_a_proxy(long? bitrate, bool expected) =>
        Assert.Equal(expected, Policy.Evaluate(Hd() with { Bitrate = bitrate }).Reasons.HasFlag(ProxyReasons.HighBitrate));

    [Theory]
    [InlineData(null, 1, false)]
    [InlineData(1.5, 1, false)]
    [InlineData(1.49, 1, true)]
    [InlineData(4.5, 3, false)]
    [InlineData(4.4, 3, true)]
    [InlineData(10.0, 6, false)]
    [InlineData(8.9, 6, true)]
    public void Measured_decode_speed_must_cover_every_layer(double? speed, int layers, bool expected) =>
        Assert.Equal(expected, Policy.Evaluate(Hd() with { DecodeSpeed = speed, Layers = layers }).Reasons.HasFlag(ProxyReasons.SlowDecode));

    [Fact]
    public void Thresholds_are_configurable()
    {
        var strict = new ProxyPolicy(new ProxyThresholds
        {
            MaxLongSide = 1280,
            MaxShortSide = 720,
            MaxKeyframeInterval = 30,
            MaxBitrate = 10_000_000,
            MinDecodeSpeedPerLayer = 3,
            HighBitDepthHevcLayers = 1,
        });

        ProxyRecommendation result = strict.Evaluate(Hd("hevc", 10) with
        {
            Bitrate = 20_000_000,
            KeyframeInterval = 48,
            DecodeSpeed = 2.5,
        });

        Assert.Equal(
            ProxyReasons.Resolution | ProxyReasons.HighBitDepthHevc | ProxyReasons.LongGop | ProxyReasons.HighBitrate | ProxyReasons.SlowDecode,
            result.Reasons);
        Assert.Equal(5, result.Explanations.Count);
        Assert.All(result.Explanations, e => Assert.False(string.IsNullOrWhiteSpace(e)));
    }

    [Fact]
    public void A_4k_hevc_10_bit_clip_on_three_layers_gives_every_reason_in_spanish()
    {
        ProxyRecommendation result = Policy.Evaluate(new ProxyCandidate(
            3840, 2160, "hevc", 10, Rational.Fps30, Bitrate: 150_000_000, KeyframeInterval: 120, DecodeSpeed: 2.0, Layers: 3));

        Assert.True(result.IsRecommended);
        Assert.Equal(5, result.Explanations.Count);
        Assert.Contains(result.Explanations, e => e.Contains("3840×2160", StringComparison.Ordinal));
        Assert.Contains(result.Explanations, e => e.Contains("HEVC de 10 bits en 3 capas", StringComparison.Ordinal));
        Assert.Contains(result.Explanations, e => e.Contains("cada 120 cuadros", StringComparison.Ordinal));
        Assert.Contains(result.Explanations, e => e.Contains("150 Mb/s", StringComparison.Ordinal));
        Assert.Contains(result.Explanations, e => e.Contains("2.0×", StringComparison.Ordinal) && e.Contains("4.5×", StringComparison.Ordinal));
    }

    [Fact]
    public void Layers_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy.Evaluate(Hd() with { Layers = 0 }));

    [Fact]
    public void Candidate_from_media_info_uses_display_size_and_measurements()
    {
        MediaInfo info = Info(width: 2160, height: 3840, rotation: 90, codec: "hevc", bits: 10);

        ProxyCandidate candidate = ProxyCandidate.From(info, new StreamStructure(90_000_000, 250, 2, 400, false), decodeSpeed: 1.2, layers: 2);

        Assert.Equal(3840, candidate.Width);
        Assert.Equal(2160, candidate.Height);
        Assert.Equal("hevc", candidate.Codec);
        Assert.Equal(10, candidate.BitDepth);
        Assert.Equal(90_000_000, candidate.Bitrate);
        Assert.Equal(250, candidate.KeyframeInterval);
        Assert.Equal(1.2, candidate.DecodeSpeed);
        Assert.Equal(2, candidate.Layers);
        Assert.Equal(
            ProxyReasons.Resolution | ProxyReasons.HighBitDepthHevc | ProxyReasons.LongGop | ProxyReasons.HighBitrate | ProxyReasons.SlowDecode,
            Policy.Evaluate(candidate).Reasons);
    }

    [Theory]
    [InlineData(1.0, 8.0, 1, false)] // a 1× no cuenta (eso es SlowDecode)
    [InlineData(2.0, 2.6, 1, false)] // pide 2 × 1,25 = 2,5
    [InlineData(2.0, 2.4, 1, true)]
    [InlineData(4.0, 4.9, 1, true)]
    [InlineData(4.0, 5.1, 1, false)]
    [InlineData(2.0, 4.9, 2, true)] // dos capas a 2× piden 5
    [InlineData(15.0, 10.0, 1, true)]
    [InlineData(16.0, 1.0, 1, false)] // desde 16× solo keyframes
    [InlineData(100.0, 1.0, 1, false)]
    [InlineData(0.5, 0.7, 1, false)]
    public void High_speed_compares_rate_times_layers_with_the_measured_decode(double rate, double measured, int layers, bool expected)
    {
        ProxyRecommendation result = Policy.Evaluate(Hd() with { PlaybackRate = rate, DecodeSpeed = measured, Layers = layers });

        Assert.Equal(expected, result.Reasons.HasFlag(ProxyReasons.HighSpeed));
        Assert.False(result.IsRequired);
    }

    [Fact]
    public void High_speed_needs_a_measurement_and_explains_in_frames_per_second()
    {
        Assert.False(Policy.Evaluate(Hd() with { PlaybackRate = 4 }).Reasons.HasFlag(ProxyReasons.HighSpeed));

        ProxyRecommendation result = Policy.Evaluate(Hd() with { PlaybackRate = 4, DecodeSpeed = 3, KeyframeInterval = 30 });
        Assert.Equal(ProxyReasons.HighSpeed, result.Reasons);
        Assert.Equal("A 4× hacen falta 150 cuadros por segundo y este equipo decodifica 90.", Assert.Single(result.Explanations));
    }

    [Theory]
    [InlineData(1920, 1080, false)]
    [InlineData(3840, 2160, true)]
    [InlineData(2160, 3840, true)]
    public void Reverse_above_1080p_requires_a_proxy(int width, int height, bool required)
    {
        ProxyRecommendation reverse = Policy.Evaluate(Hd() with { Width = width, Height = height, Reverse = true });
        ProxyRecommendation forward = Policy.Evaluate(Hd() with { Width = width, Height = height });

        Assert.Equal(required, reverse.Reasons.HasFlag(ProxyReasons.Reverse));
        Assert.Equal(required, reverse.IsRequired);
        Assert.False(forward.IsRequired);
        Assert.False(forward.Reasons.HasFlag(ProxyReasons.Reverse));
    }

    [Fact]
    public void Candidate_carries_rate_and_direction()
    {
        ProxyCandidate candidate = ProxyCandidate.From(Info(3840, 2160), decodeSpeed: 1.8, playbackRate: 2, reverse: true);

        Assert.Equal(2, candidate.PlaybackRate);
        Assert.True(candidate.Reverse);
        Assert.Equal(ProxyReasons.Resolution | ProxyReasons.HighSpeed | ProxyReasons.Reverse, Policy.Evaluate(candidate).Reasons);
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy.Evaluate(candidate with { PlaybackRate = 0 }));
    }

    [Fact]
    public void Candidate_needs_video() =>
        Assert.Throws<ArgumentException>(() => ProxyCandidate.From(Info(withVideo: false)));

    internal static MediaInfo Info(int width = 1920, int height = 1080, int rotation = 0, string codec = "h264", int bits = 8, bool withVideo = true)
    {
        bool swap = rotation is 90 or 270;
        return new MediaInfo
        {
            Path = @"C:\medios\clip.mp4",
            ContainerFormat = "mov,mp4,m4a,3gp,3g2,mj2",
            Duration = Time.FromSeconds(10),
            ContainerStartTime = Time.Zero,
            Video = !withVideo ? null : new VideoStreamInfo
            {
                StreamIndex = 0,
                Codec = codec,
                PixelFormat = bits > 8 ? "yuv420p10le" : "yuv420p",
                BitDepth = bits,
                Width = width,
                Height = height,
                SampleAspectRatio = new Rational(1, 1),
                Rotation = rotation,
                DisplayWidth = swap ? height : width,
                DisplayHeight = swap ? width : height,
                FrameRate = Rational.Fps30,
                IsVariableFrameRate = false,
                FrameCount = 300,
                Duration = Time.FromSeconds(10),
                StartTime = Time.Zero,
            },
        };
    }
}
