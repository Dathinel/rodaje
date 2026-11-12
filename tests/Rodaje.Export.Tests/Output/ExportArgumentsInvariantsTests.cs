using System.Globalization;
using Rodaje.Core.Timing;
using Rodaje.Export.Running;
using Rodaje.Export.Tests.Support;
using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Output;

public sealed class ExportArgumentsInvariantsTests
{
    public static IEnumerable<object[]> ContainerCases =>
        from container in Enum.GetValues<ExportContainer>()
        from fastStart in new[] { false, true }
        from culture in new[] { "", "es-CO", "de-DE", "tr-TR" }
        select new object[] { container, fastStart, culture };

    [Theory]
    [MemberData(nameof(ContainerCases))]
    public void Every_container_has_exact_flags_in_every_culture(ExportContainer container, bool fastStart, string culture)
    {
        InCulture(culture, () =>
        {
            string[] expected = fastStart && container is ExportContainer.Mp4 or ExportContainer.Mov or ExportContainer.M4a
                ? ["-movflags", "+faststart"] : [];
            Assert.Equal(expected, FfmpegArguments.Container(new ExportSettings { Container = container, FastStart = fastStart }));
        });
    }

    public static IEnumerable<object[]> VideoCases =>
        from culture in new[] { "", "es-CO", "de-DE", "tr-TR" }
        from mode in new[] { RateControlMode.ConstantQuality, RateControlMode.Vbr, RateControlMode.Cbr, RateControlMode.Abr, RateControlMode.Lossless }
        select new object[] { mode, culture };

    [Theory]
    [MemberData(nameof(VideoCases))]
    public void Fractional_quality_and_gop_have_exact_arguments_for_every_rate_mode(RateControlMode mode, string culture)
    {
        InCulture(culture, () =>
        {
            var video = new VideoExportSettings
            {
                Codec = ExportCodec.H264,
                RateControl = new RateControl { Mode = mode, Quality = 21.375, BitrateKbps = 8123,
                    MaxBitrateKbps = mode == RateControlMode.Vbr ? 12345 : null },
                Profile = "high", Level = "4.1", SpeedPreset = "p7", Tune = "hq",
                Gop = new GopSettings { KeyframeSeconds = 1.5, BFrames = 2, References = 3 },
            };
            string[] head = mode switch
            {
                RateControlMode.ConstantQuality => ["-rc", "vbr", "-cq", "21.375", "-b:v", "0"],
                RateControlMode.Vbr => ["-rc", "vbr", "-b:v", "8123k", "-maxrate", "12345k", "-bufsize", "24690k"],
                RateControlMode.Cbr => ["-rc", "cbr", "-b:v", "8123k", "-maxrate", "8123k", "-bufsize", "16246k"],
                RateControlMode.Abr => ["-rc", "vbr", "-b:v", "8123k"],
                _ => ["-tune", "lossless"],
            };
            Assert.Equal([.. head, "-profile:v", "high", "-level:v", "4.1", "-preset", "p7", "-tune", "hq",
                "-g", "45", "-flags", "+cgop", "-bf", "2", "-refs", "3"],
                FfmpegArguments.Video(video, TestEncoders.H264Nvenc, ExportPixelFormat.Nv12, Rational.Ntsc30, TimeSpan.FromSeconds(10.125), 192));
        });
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void Fractional_audio_quality_has_exact_arguments(string culture)
    {
        InCulture(culture, () =>
        {
            EncoderSpec encoder = TestEncoders.Pcm24 with { Id = "libmp3lame", Codec = ExportCodec.Mp3 };
            var settings = new AudioExportSettings { Codec = ExportCodec.Mp3, SampleRate = 44100, Channels = 1,
                RateControl = RateControl.ConstantQuality(2.375) };
            Assert.Equal(["-q:a", "2.375", "-ar", "44100", "-ac", "1"], FfmpegArguments.Audio(settings, encoder));
        });
    }

    [Theory]
    [InlineData("salida con espacios.mp4", "\"salida con espacios.mp4\"")]
    [InlineData("edición ñ.mp4", "\"edición ñ.mp4\"")]
    [InlineData("dice\"hola.mp4", "\"dice\\\"hola.mp4\"")]
    [InlineData("100%.mp4", "100%.mp4")]
    [InlineData("%TEMP%.mp4", "%TEMP%.mp4")]
    public void Special_output_paths_keep_the_exact_argument_and_console_quoting(string path, string quoted)
    {
        Assert.Equal(quoted, ExportCommandLine.Quote(path));
        Assert.Equal("ffmpeg -y " + quoted, ExportCommandLine.Format("ffmpeg", ["-y", path]));
    }

    private static void InCulture(string name, Action test)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            test();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
