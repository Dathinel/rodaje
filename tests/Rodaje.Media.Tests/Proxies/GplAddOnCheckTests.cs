using Rodaje.Media.Proxies;

namespace Rodaje.Media.Tests.Proxies;

/// <summary>El exe del complemento de codificadores GPL se acepta solo fuera del editor: GPL sí, nonfree nunca, misma serie.</summary>
public class GplAddOnCheckTests
{
    private const string Version = "ffmpeg version n9.0.2\nlibavcodec     63.  3.100 / 63.  3.100\n";

    private const string Gpl = "ffmpeg is free software; you can redistribute it and/or modify it under the terms of the GNU General Public License "
        + "as published by the Free Software Foundation; either version 3 of the License, or (at your option) any later version.";

    [Fact]
    public void Gpl_is_accepted_only_for_the_add_on()
    {
        Assert.Null(FfmpegExecutable.GplAddOnBuildProblem("--enable-gpl --enable-version3", Gpl, Version));
        Assert.NotNull(FfmpegExecutable.BuildProblem("--enable-gpl --enable-version3", Gpl, Version));
    }

    [Fact]
    public void Nonfree_unknown_licenses_and_other_series_are_rejected()
    {
        Assert.NotNull(FfmpegExecutable.GplAddOnBuildProblem("--enable-gpl --enable-nonfree", Gpl, Version));
        Assert.NotNull(FfmpegExecutable.GplAddOnBuildProblem("--enable-gpl", "This version of ffmpeg has nonfree parts compiled in.", Version));
        Assert.NotNull(FfmpegExecutable.GplAddOnBuildProblem("--enable-gpl", "Propietario.", Version));
        Assert.NotNull(FfmpegExecutable.GplAddOnBuildProblem("--enable-gpl", Gpl, "libavcodec     61.  3.100 / 61.  3.100\n"));
    }
}
