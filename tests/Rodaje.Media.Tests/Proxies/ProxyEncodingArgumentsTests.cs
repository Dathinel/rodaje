using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;

namespace Rodaje.Media.Tests.Proxies;

public sealed class ProxyEncodingArgumentsTests
{
    private static readonly ProxyEncodeInput Input = new(@"D:\Videos de prueba\toma ñandú 4K.mov", 2, 3840, 2160, Rational.Ntsc30);

    [Theory]
    [InlineData(540, 1920, 1080, 960, 540)]
    [InlineData(540, 3840, 2160, 960, 540)]
    [InlineData(720, 3840, 2160, 1280, 720)]
    [InlineData(720, 1920, 1080, 1280, 720)]
    [InlineData(540, 1080, 1920, 540, 960)]
    [InlineData(540, 2160, 3840, 540, 960)]
    [InlineData(540, 640, 360, 640, 360)]
    [InlineData(720, 1280, 720, 1280, 720)]
    [InlineData(540, 4096, 2160, 1024, 540)]
    [InlineData(540, 1440, 1080, 720, 540)]
    [InlineData(540, 2048, 858, 1288, 540)]
    [InlineData(540, 1000, 1000, 540, 540)]
    [InlineData(540, 999, 541, 998, 540)]
    public void Output_size_scales_the_short_side_keeps_aspect_and_is_even(int target, int width, int height, int expectedWidth, int expectedHeight)
    {
        (int w, int h) = new ProxySettings { TargetHeight = target }.OutputSize(width, height);

        Assert.Equal(expectedWidth, w);
        Assert.Equal(expectedHeight, h);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
    }

    [Theory]
    [InlineData(480, 15)]
    [InlineData(1080, 15)]
    [InlineData(540, 0)]
    [InlineData(540, 121)]
    public void Invalid_settings_are_rejected(int height, int gop) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxySettings { TargetHeight = height, GopLength = gop }.Validate());

    [Fact]
    public void Settings_tag_names_every_choice_and_the_format_version()
    {
        Assert.Equal("h540-g15-v1", ProxySettings.Default.Tag);
        Assert.Equal("h720-g10-v1", new ProxySettings { TargetHeight = 720, GopLength = 10 }.Tag);
    }

    [Fact]
    public void Arguments_keep_every_frame_and_timestamp_of_the_original()
    {
        IReadOnlyList<string> args = ProxyEncoders.BuildArguments(Input, ProxySettings.Default, "h264_nvenc", @"C:\p r\o.tmp.mp4", hardwareDecoding: false);

        AssertSequence(args, "-copyts", "-i", Input.SourcePath);
        AssertSequence(args, "-map", "0:2");
        AssertSequence(args, "-fps_mode", "passthrough");
        AssertSequence(args, "-enc_time_base:v", "demux");
        Assert.DoesNotContain("-r", args);
        Assert.DoesNotContain("-vsync", args);
        Assert.True(args.ToList().IndexOf("-copyts") < args.ToList().IndexOf("-i"), "-copyts es opción de entrada.");
    }

    [Fact]
    public void Arguments_drop_audio_and_extra_streams_and_use_a_short_gop_without_b_frames()
    {
        IReadOnlyList<string> args = ProxyEncoders.BuildArguments(
            Input, new ProxySettings { GopLength = 12 }, "libopenh264", "o.mp4", hardwareDecoding: false);

        Assert.Contains("-an", args);
        Assert.Contains("-sn", args);
        Assert.Contains("-dn", args);
        AssertSequence(args, "-g", "12");
        AssertSequence(args, "-bf", "0");
        AssertSequence(args, "-allow_skip_frames", "0");
        AssertSequence(args, "-vf", "scale=960:540:flags=bicubic,setsar=1");
        AssertSequence(args, "-progress", "pipe:1");
        Assert.Equal("o.mp4", args[^1]);
        AssertSequence(args, "-f", "mp4", "o.mp4");
    }

    [Fact]
    public void Each_encoder_gets_its_pixel_format_and_rate_control()
    {
        IReadOnlyList<string> nvenc = ProxyEncoders.BuildArguments(Input, ProxySettings.Default, "h264_nvenc", "o.mp4", false);
        IReadOnlyList<string> mf = ProxyEncoders.BuildArguments(Input, ProxySettings.Default, "h264_mf", "o.mp4", false);
        IReadOnlyList<string> openh264 = ProxyEncoders.BuildArguments(Input, ProxySettings.Default, "libopenh264", "o.mp4", false);

        AssertSequence(nvenc, "-c:v", "h264_nvenc");
        AssertSequence(nvenc, "-pix_fmt", "nv12");
        AssertSequence(nvenc, "-no-scenecut", "1");
        AssertSequence(mf, "-pix_fmt", "nv12");
        AssertSequence(mf, "-c:v", "h264_mf");
        AssertSequence(openh264, "-pix_fmt", "yuv420p");
        AssertSequence(openh264, "-rc_mode", "bitrate");
    }

    [Fact]
    public void Hardware_decoding_adds_d3d11va_before_the_input()
    {
        List<string> args = [.. ProxyEncoders.BuildArguments(Input, ProxySettings.Default, "h264_nvenc", "o.mp4", hardwareDecoding: true)];
        List<string> software = [.. ProxyEncoders.BuildArguments(Input, ProxySettings.Default, "h264_nvenc", "o.mp4", hardwareDecoding: false)];

        AssertSequence(args, "-hwaccel", "d3d11va");
        Assert.True(args.IndexOf("-hwaccel") < args.IndexOf("-i"));
        Assert.DoesNotContain("-hwaccel", software);
    }

    [Theory]
    [InlineData(540, 30, 1, 3000)]
    [InlineData(540, 60, 1, 6000)]
    [InlineData(540, 24000, 1001, 2398)]
    [InlineData(540, 10, 1, 1500)]
    [InlineData(540, 240, 1, 6000)]
    [InlineData(720, 30000, 1001, 4995)]
    public void Bitrate_follows_height_and_frame_rate(int height, long num, long den, int expectedKbps) =>
        Assert.Equal(expectedKbps, ProxyEncoders.Bitrate(new ProxySettings { TargetHeight = height }, new Rational(num, den)));

    [Fact]
    public void Preference_is_nvenc_then_media_foundation_then_openh264() =>
        Assert.Equal(["h264_nvenc", "h264_mf", "libopenh264"], ProxyEncoders.Preference);

    private static void AssertSequence(IReadOnlyList<string> args, params string[] sequence)
    {
        for (int i = 0; i + sequence.Length <= args.Count; i++)
        {
            if (sequence.Select((s, k) => args[i + k] == s).All(x => x))
            {
                return;
            }
        }

        Assert.Fail($"No aparece «{string.Join(' ', sequence)}» en: {string.Join(' ', args)}");
    }
}
