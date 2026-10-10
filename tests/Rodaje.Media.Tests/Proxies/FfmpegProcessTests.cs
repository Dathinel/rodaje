using Rodaje.Media.Proxies;
using Rodaje.Media.Tests.Support;

namespace Rodaje.Media.Tests.Proxies;

public sealed class FfmpegProcessTests
{
    private const string LgplConfiguration = "--prefix=/ffbuild/prefix --enable-version3 --disable-debug --enable-shared --disable-libx264";
    private const string LgplLicense = "ffmpeg is free software; you can redistribute it and/or modify\nit under the terms of the GNU Lesser General Public License as published by";
    private const string GplLicense = "ffmpeg is free software; you can redistribute it and/or modify\nit under the terms of the GNU General Public License as published by";
    private const string Version9 = "ffmpeg version n9.0.2\nlibavutil      61.  1.100 / 61.  1.100\nlibavcodec     63.  3.100 / 63.  3.100\n";

    [Fact]
    public void An_lgpl_build_of_ffmpeg_9_is_accepted() =>
        Assert.Null(FfmpegExecutable.BuildProblem(LgplConfiguration, LgplLicense, Version9));

    [Theory]
    [InlineData("--enable-gpl")]
    [InlineData("--enable-nonfree")]
    public void A_build_configured_as_gpl_or_nonfree_is_rejected(string flag)
    {
        string? problem = FfmpegExecutable.BuildProblem(LgplConfiguration + " " + flag + " --enable-libx264", LgplLicense, Version9);

        Assert.NotNull(problem);
        Assert.Contains(flag, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gpl_license_text_is_rejected_even_without_the_flag()
    {
        string? problem = FfmpegExecutable.BuildProblem(LgplConfiguration, GplLicense, Version9);

        Assert.NotNull(problem);
        Assert.Contains("GPL", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_major_version_of_libavcodec_is_rejected()
    {
        string? problem = FfmpegExecutable.BuildProblem(LgplConfiguration, LgplLicense, "libavcodec     62. 11.100 / 62. 11.100\n");

        Assert.NotNull(problem);
        Assert.Contains("62", problem, StringComparison.Ordinal);
        Assert.NotNull(FfmpegExecutable.BuildProblem(LgplConfiguration, LgplLicense, "sin versiones"));
    }

    [Fact]
    public void Progress_blocks_end_at_each_progress_line()
    {
        var parser = new FfmpegProgressParser();
        string[] lines =
        [
            "frame=12", "fps=0.00", "out_time_us=3900000", "speed=N/A", "progress=continue",
            "frame=480", "out_time_us=19500000", "progress=continue",
            "frame=599", "out_time_us=N/A", "progress=end",
        ];
        var blocks = new List<FfmpegProgressSnapshot>();
        foreach (string line in lines)
        {
            if (parser.Feed(line, out FfmpegProgressSnapshot snapshot))
            {
                blocks.Add(snapshot);
            }
        }

        Assert.Equal(
            [new FfmpegProgressSnapshot(12, 3_900_000, false), new FfmpegProgressSnapshot(480, 19_500_000, false), new FfmpegProgressSnapshot(599, 19_500_000, true)],
            blocks);
        Assert.Equal(599, parser.Last.Frame);
        Assert.False(parser.Feed("", out _));
        Assert.False(parser.Feed("sin igual", out _));
        Assert.False(parser.Feed(null, out _));
    }

    [ProxyFact]
    public void The_bundled_ffmpeg_exe_is_the_lgpl_build_next_to_the_dlls()
    {
        FfmpegExecutable exe = FfmpegExecutable.Locate();

        Assert.Equal(Path.Combine(FfmpegLibrary.Directory!, "ffmpeg.exe"), exe.Path, ignoreCase: true);
        Assert.Contains("--enable-version3", exe.Configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("--enable-gpl", exe.Configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("--enable-nonfree", exe.Configuration, StringComparison.Ordinal);
        Assert.StartsWith("ffmpeg version n9.", exe.Version, StringComparison.Ordinal);
        Assert.Same(exe, FfmpegExecutable.Open(exe.Path));
        Assert.All(FfmpegExecutable.ExtraLibraries, l => Assert.True(File.Exists(Path.Combine(FfmpegLibrary.Directory!, l + ".dll"))));
    }

    [ProxyFact]
    public void Encoder_probe_explains_a_missing_encoder()
    {
        FfmpegExecutable exe = FfmpegExecutable.Locate();

        Assert.True(exe.IsEncoderUsable("libopenh264"));
        Assert.False(exe.IsEncoderUsable("libx264"));
        Assert.Contains("libx264", exe.ProbeEncoder("libx264"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_executable_is_reported_in_spanish()
    {
        using var temp = new TempDirectory();

        var ex = Assert.Throws<FfmpegNotFoundException>(() => FfmpegExecutable.Open(temp.Combine("ffmpeg.exe")));

        Assert.Contains("No existe", ex.Message, StringComparison.Ordinal);
    }
}
