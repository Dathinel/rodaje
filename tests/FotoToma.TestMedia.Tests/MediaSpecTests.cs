using FotoToma.Core.Timing;

namespace FotoToma.TestMedia.Tests;

public class MediaSpecTests
{
    private static MediaSpec Small => new("pequeño", 320, 240, Rational.Ntsc30, TimeSpan.FromSeconds(2));

    [Fact]
    public void FrameAndSampleCounts_AreExact()
    {
        var spec = new MediaSpec("x", 640, 360, Rational.Ntsc30, TimeSpan.FromSeconds(20));

        Assert.Equal(599, spec.FrameCount);
        Assert.Equal(959_358, spec.AudioSampleCount);
        Assert.Equal(17_982, TestMediaPresets.Drift720p10Min.FrameCount);
        Assert.Equal(500, TestMediaPresets.Hevc25Mkv.FrameCount);
    }

    [Fact]
    public void CacheKey_IsStableAndSensitiveToEveryParameter()
    {
        MediaSpec baseline = Small;
        string key = baseline.CacheKey("h264_nvenc");

        Assert.Equal(key, Small.CacheKey("h264_nvenc"));
        Assert.Equal(16, key.Length);
        MediaSpec[] variants =
        [
            baseline with { GopLength = 30 },
            baseline with { BFrames = 1 },
            baseline with { Audio = AudioCodec.Aac },
            baseline with { Container = ContainerFormat.Mkv },
            baseline with { StartOffset = TimeSpan.FromSeconds(1) },
            baseline with { VideoBitrateKbps = 999 },
            baseline with { AudioBitrateKbps = 96 },
            baseline with { Codec = VideoCodec.Hevc },
            new("pequeño", 320, 240, Rational.Fps30, TimeSpan.FromSeconds(2)),
            new("pequeño", 320, 240, Rational.Ntsc30, TimeSpan.FromSeconds(3)),
            new("pequeño", 640, 240, Rational.Ntsc30, TimeSpan.FromSeconds(2)),
        ];
        foreach (MediaSpec variant in variants)
        {
            Assert.NotEqual(key, variant.CacheKey("h264_nvenc"));
        }

        Assert.NotEqual(key, baseline.CacheKey("h264_mf"));
    }

    [Fact]
    public void Validate_RejectsInvalidCombinations()
    {
        Assert.Throws<ArgumentException>(() => (Small with { Audio = AudioCodec.Opus, Container = ContainerFormat.Mov }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Small with { GopLength = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Small with { BFrames = -1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Small with { StartOffset = TimeSpan.FromSeconds(-1) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaSpec("x", 64, 48, Rational.Fps25, TimeSpan.FromSeconds(1)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaSpec("x", 320, 240, Rational.Fps60, TimeSpan.FromHours(5)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaSpec("x", 321, 240, Rational.Fps25, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaSpec("x", 320, 240, Rational.Fps25, TimeSpan.Zero));
    }

    [Fact]
    public void BuildArguments_PassesPathsAsSingleArguments()
    {
        MediaSpec spec = Small with { Audio = AudioCodec.Aac, StartOffset = TimeSpan.FromSeconds(1.5) };
        const string wav = @"C:\Carpeta con espacios\canción.wav";
        const string output = @"D:\Programas de uso libre\FotoToma\TestResults\media-cache\año 1.mp4";

        List<string> args = TestMediaGenerator.BuildArguments(spec, "h264_nvenc", wav, output);

        Assert.Contains(wav, args);
        Assert.Equal(output, args[^1]);
        Assert.Equal("30000/1001", args[args.IndexOf("-framerate") + 1]);
        Assert.Equal("1.5", args[args.IndexOf("-output_ts_offset") + 1]);
        Assert.Equal("aac", args[args.IndexOf("-c:a") + 1]);
        Assert.Equal("0", args[args.IndexOf("-bf") + 1]);
    }

    [Theory]
    [InlineData("h264_nvenc", "-no-scenecut")]
    [InlineData("h264_mf", "-rate_control")]
    [InlineData("libx264", "-sc_threshold")]
    [InlineData("libx265", "-x265-params")]
    [InlineData("libopenh264", "-g")]
    public void BuildArguments_UsesEncoderSpecificOptions(string encoder, string expectedOption)
    {
        List<string> args = TestMediaGenerator.BuildArguments(Small with { BFrames = 2 }, encoder, null, "salida.mp4");

        Assert.Contains(expectedOption, args);
        Assert.DoesNotContain("-c:a", args);
        Assert.Equal(encoder, args[args.IndexOf("-c:v") + 1]);
    }

    [Fact]
    public void BuildArguments_TagsHevcInMp4()
    {
        List<string> mp4 = TestMediaGenerator.BuildArguments(Small with { Codec = VideoCodec.Hevc }, "hevc_nvenc", null, "a.mp4");
        List<string> mkv = TestMediaGenerator.BuildArguments(Small with { Codec = VideoCodec.Hevc, Container = ContainerFormat.Mkv }, "hevc_nvenc", null, "a.mkv");

        Assert.Equal("hvc1", mp4[mp4.IndexOf("-tag:v") + 1]);
        Assert.DoesNotContain("-tag:v", mkv);
        Assert.Equal("matroska", mkv[mkv.IndexOf("-f", mkv.IndexOf("-c:v")) + 1]);
    }

    [Fact]
    public void WriteWav_HasHeaderAndMarks()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fototoma prueba ñ {Guid.NewGuid():N}.wav");
        try
        {
            TestMediaGenerator.WriteWav(path, 2 * MediaSpec.AudioSampleRate);
            byte[] bytes = File.ReadAllBytes(path);

            Assert.Equal(44 + 2 * MediaSpec.AudioSampleRate * 2 * 2, bytes.Length);
            Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
            Assert.Equal("data"u8.ToArray(), bytes[36..40]);
            var samples = new float[(bytes.Length - 44) / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = BitConverter.ToInt16(bytes, 44 + 2 * i) / 32768f;
            }

            IReadOnlyList<AudioMark> marks = AudioCode.Detect(samples, MediaSpec.AudioSampleRate, 2);
            Assert.Equal([0L, MediaSpec.AudioSampleRate], marks.Select(m => m.Sample));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
