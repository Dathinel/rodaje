namespace Rodaje.Media.Tests;

public sealed class FfmpegLocatorNamingTests
{
    [Theory]
    [InlineData("avcodec-63", "Windows", "avcodec-63.dll")]
    [InlineData("avcodec-63", "Linux", "libavcodec.so.63")]
    [InlineData("avcodec-63", "MacOs", "libavcodec.63.dylib")]
    [InlineData("swresample-7", "Linux", "libswresample.so.7")]
    [InlineData("avfilter-12", "MacOs", "libavfilter.12.dylib")]
    [InlineData("avcodec", "Linux", "avcodec")]
    public void Library_file_name_follows_each_system(string versioned, string os, string expected) =>
        Assert.Equal(expected, FfmpegLocator.LibraryFileName(versioned, Enum.Parse<NativeOs>(os)));

    [Theory]
    [InlineData("avfilter-12.dll", "avfilter-12")]
    [InlineData("AVFILTER-12.DLL", "AVFILTER-12")]
    [InlineData("libavfilter.so.12", "avfilter-12")]
    [InlineData("libavfilter.12.dylib", "avfilter-12")]
    [InlineData("libavfilter.so", null)]
    [InlineData("libavfilter.dylib", null)]
    [InlineData("avfilter.dll", null)]
    [InlineData("avfilter-x.dll", null)]
    [InlineData("ffmpeg", null)]
    public void Versioned_name_reads_any_system(string fileName, string? expected) =>
        Assert.Equal(expected, FfmpegLocator.VersionedName(fileName));

    [Fact]
    public void Every_required_library_round_trips_on_every_system()
    {
        foreach (NativeOs os in Enum.GetValues<NativeOs>())
        {
            foreach (string library in FfmpegLocator.RequiredLibraries)
            {
                Assert.Equal(library, FfmpegLocator.VersionedName(FfmpegLocator.LibraryFileName(library, os)));
            }
        }
    }

    [Fact]
    public void Host_names_match_the_host()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg", FfmpegLocator.ExecutableName("ffmpeg"));
        Assert.Equal(OperatingSystem.IsWindows() ? "bin" : "lib", FfmpegLocator.LibrarySubdirectory);
    }
}
