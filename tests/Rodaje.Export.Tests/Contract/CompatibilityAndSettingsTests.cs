using Rodaje.Export.Tests.Support;
using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Contract;

public sealed class CompatibilityAndSettingsTests
{
    [Fact]
    public void Every_container_has_a_muxer_an_extension_and_at_least_one_codec()
    {
        foreach (ExportContainer container in Enum.GetValues<ExportContainer>())
        {
            Assert.False(string.IsNullOrEmpty(container.Muxer()));
            Assert.False(string.IsNullOrEmpty(container.Extension()));
            Assert.NotEmpty(container.VideoCodecs().Concat(container.AudioCodecs()));
            Assert.All(container.VideoCodecs(), c => Assert.False(c.IsAudio()));
            Assert.All(container.AudioCodecs(), c => Assert.True(c.IsAudio()));
            Assert.False(string.IsNullOrEmpty(ExportCompatibility.Name(container)));
        }

        Assert.True(ExportContainer.Wav.IsAudioOnly());
        Assert.False(ExportContainer.Gif.AllowsAudio());
        Assert.True(ExportContainer.WebM.Allows(ExportCodec.Opus) && !ExportContainer.WebM.Allows(ExportCodec.H264));
        Assert.Throws<ArgumentOutOfRangeException>(() => ((ExportContainer)99).Muxer());
    }

    [Fact]
    public void The_matrix_explains_why_a_combination_is_not_valid()
    {
        Assert.Empty(ExportCompatibility.Check(ExportContainer.Mp4, TestEncoders.H264Nvenc, ExportPixelFormat.Nv12, TestEncoders.AacMf, "high"));

        Codes(ExportCompatibility.Check(ExportContainer.WebM, TestEncoders.H264Nvenc, ExportPixelFormat.Nv12, TestEncoders.AacMf))
            .ShouldBe(ExportProblemCode.VideoCodecNotInContainer, ExportProblemCode.AudioCodecNotInContainer);
        Codes(ExportCompatibility.Check(ExportContainer.Wav, TestEncoders.H264Nvenc, null, TestEncoders.Pcm24))
            .ShouldBe(ExportProblemCode.AudioOnlyContainer);
        Codes(ExportCompatibility.Check(ExportContainer.Gif, TestEncoders.Gif, ExportPixelFormat.Rgb24, TestEncoders.AacMf))
            .ShouldBe(ExportProblemCode.NoAudioInContainer);
        Codes(ExportCompatibility.Check(ExportContainer.Mp4, null, null, null)).ShouldBe(ExportProblemCode.NoStreams);
        Codes(ExportCompatibility.Check(ExportContainer.Mp4, TestEncoders.H264Nvenc, ExportPixelFormat.Yuva420P, null))
            .ShouldBe(ExportProblemCode.AlphaNotSupported);
        Codes(ExportCompatibility.Check(ExportContainer.Mp4, TestEncoders.H264Nvenc, ExportPixelFormat.P010, null))
            .ShouldBe(ExportProblemCode.PixelFormatNotInEncoder);
        Codes(ExportCompatibility.Check(ExportContainer.Mp4, TestEncoders.H264Nvenc, ExportPixelFormat.Nv12, null, "high444p"))
            .ShouldBe(ExportProblemCode.PixelFormatNotInProfile);
        Codes(ExportCompatibility.Check(ExportContainer.Mp4, TestEncoders.H264Nvenc, ExportPixelFormat.Nv12, null, "baseline"))
            .ShouldBe(ExportProblemCode.ProfileUnknown);
        Assert.Throws<ArgumentException>(() => ExportCompatibility.Check(ExportContainer.Mp4, TestEncoders.AacMf, null, null));
    }

    [Fact]
    public void Every_factory_preset_is_valid_with_the_encoders_it_names()
    {
        Assert.Equal(ExportPresets.All.Count, ExportPresets.All.Select(p => p.Id).Distinct().Count());
        foreach (ExportPreset preset in ExportPresets.All)
        {
            ExportSettings settings = preset.Settings;
            IReadOnlyList<ExportProblem> problems = ExportSettingsValidator.Validate(settings,
                settings.Video is { } v ? TestEncoders.For(v.Codec) : null, settings.Audio is { } a ? TestEncoders.For(a.Codec) : null);
            Assert.True(problems.Count == 0, $"{preset.Name}: {string.Join(" | ", problems.Select(p => p.Message))}");
            Assert.Equal(ExportPresetOrigin.Factory, preset.Origin);
            Assert.StartsWith("rodaje.", preset.Id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_validator_checks_rate_control_profile_level_gop_and_audio_against_the_encoder()
    {
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.Av1, BitDepth = 12, Level = "9.9",
                RateControl = new RateControl { Mode = RateControlMode.Abr, Passes = 2, Quality = 99 },
                Gop = new GopSettings { BFrames = 3, KeyframeSeconds = 0 },
                Width = -1,
            },
            Audio = new AudioExportSettings { Codec = ExportCodec.Opus, SampleRate = 44_100, Channels = 9, Loudness = new LoudnessTarget(-90) },
        };

        HashSet<ExportProblemCode> codes = ExportSettingsValidator.Validate(settings, TestEncoders.H264Nvenc, TestEncoders.Opus)
            .Select(p => p.Code).ToHashSet();

        Assert.Superset(new HashSet<ExportProblemCode>
        {
            ExportProblemCode.InvalidValue, ExportProblemCode.PixelFormatNotInEncoder, ExportProblemCode.RateControlNotSupported,
            ExportProblemCode.QualityOutOfRange, ExportProblemCode.LevelUnknown, ExportProblemCode.SampleRateNotSupported,
            ExportProblemCode.TooManyChannels,
        }, codes);

        HashSet<ExportProblemCode> av1 = ExportSettingsValidator.Validate(settings with { Audio = null }, TestEncoders.SvtAv1, null)
            .Select(p => p.Code).ToHashSet();
        Assert.Superset(new HashSet<ExportProblemCode> { ExportProblemCode.TwoPassNotSupported, ExportProblemCode.TooManyBFrames }, av1);

        Assert.Contains(ExportSettingsValidator.Validate(
                new ExportSettings { Video = new VideoExportSettings { Codec = ExportCodec.H264, Alpha = true } }, TestEncoders.H264Nvenc, null),
            p => p.Code == ExportProblemCode.AlphaNotSupported);
        Assert.Contains(ExportSettingsValidator.Validate(
                new ExportSettings { Video = new VideoExportSettings { Codec = ExportCodec.H264, RateControl = new RateControl { Passes = 3 } } },
                TestEncoders.H264Nvenc, null),
            p => p.Code == ExportProblemCode.InvalidValue);
        Assert.Throws<ArgumentException>(() => ExportSettingsValidator.Validate(new ExportSettings(), TestEncoders.H264Nvenc, null));
    }

    [Fact]
    public void Rate_helpers_compute_target_size_bitrates_and_gop_lengths()
    {
        Assert.Equal(RateControlMode.Abr, RateControl.TargetSize(100_000_000).Mode);
        Assert.Equal(2, RateControl.TargetSize(100_000_000).Passes);
        Assert.Equal(9_000, RateControl.Cbr(9_000).MaxBitrateKbps);
        Assert.Equal(RateControlMode.Lossless, RateControl.Lossless.Mode);

        // 100 MB en 100 s: 8000 kbps menos el 2 % y 192 de audio.
        Assert.Equal(7648, RateControl.BitrateForTargetSize(100_000_000, TimeSpan.FromSeconds(100), 192));
        Assert.Equal(1, RateControl.BitrateForTargetSize(1, TimeSpan.FromHours(1), 192));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateControl.BitrateForTargetSize(1, TimeSpan.Zero, 0));

        Assert.Equal(60, new GopSettings().LengthAt(Rodaje.Core.Timing.Rational.Fps30));
        Assert.Equal(48, new GopSettings { KeyframeSeconds = null, Length = 48 }.LengthAt(Rodaje.Core.Timing.Rational.Fps30));
    }

    [Fact]
    public void Progress_counts_passes_and_states_know_when_they_are_final()
    {
        Assert.Equal(0.75, new ExportProgress(ExportJobState.Encoding, 50, 100, 120, TimeSpan.Zero, null, 0, Pass: 2, PassCount: 2).Fraction);
        Assert.Equal(1, new ExportProgress(ExportJobState.Completed, 0, 0, 0, TimeSpan.Zero, null, 0).Fraction);
        Assert.Equal(0, new ExportProgress(ExportJobState.Preparing, 0, 0, 0, TimeSpan.Zero, null, 0).Fraction);
        Assert.True(ExportJobState.Canceled.IsFinished());
        Assert.False(ExportJobState.Finalizing.IsFinished());
        Assert.Equal(ExportPerformance.Background, new ExportSettings().Performance);
        Assert.Equal(1.0, new ExportRange(Rodaje.Core.Timing.Time.Zero, Rodaje.Core.Timing.Time.FromSeconds(1)).Duration.ToSeconds());
    }

    private static ExportProblemCode[] Codes(IReadOnlyList<ExportProblem> problems)
    {
        Assert.All(problems, p => Assert.False(string.IsNullOrWhiteSpace(p.Message)));
        return problems.Select(p => p.Code).ToArray();
    }
}

internal static class ProblemAssertions
{
    public static void ShouldBe(this ExportProblemCode[] actual, params ExportProblemCode[] expected) => Assert.Equal(expected, actual);
}
