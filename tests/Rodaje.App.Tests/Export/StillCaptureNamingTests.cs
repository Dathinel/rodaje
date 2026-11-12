using Rodaje.App.Services.Settings;
using Rodaje.App.Views.Export.Logic;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.App.Tests.Export;

public sealed class StillCaptureNamingTests
{
    [Fact]
    public void FormatFileName_ProducesExpectedPattern()
    {
        // 1 hour, 23 minutes, 45 seconds, 12 frames at 30 fps
        Rational rate = Rational.Fps30;
        long totalSeconds = (1 * 3600) + (23 * 60) + 45;
        Time time = Time.FromSeconds(totalSeconds) + Time.FromFrames(12, rate);

        string fileName = StillCaptureNaming.FormatFileName("Mi Proyecto", time, rate);
        Assert.Equal("Mi Proyecto_01-23-45-12.png", fileName);
    }

    [Fact]
    public void FormatFileName_SanitizesInvalidChars()
    {
        Rational rate = Rational.Fps24;
        Time time = Time.Zero;

        string fileName = StillCaptureNaming.FormatFileName("Proyecto: 1/2? *nuevo*", time, rate);
        Assert.DoesNotContain(":", fileName);
        Assert.DoesNotContain("/", fileName);
        Assert.DoesNotContain("?", fileName);
        Assert.DoesNotContain("*", fileName);
        Assert.EndsWith("_00-00-00-00.png", fileName);
    }

    [Fact]
    public void FormatFileName_HandlesZeroAndNegativeTime()
    {
        Rational rate = Rational.Fps30;
        string fileNameZero = StillCaptureNaming.FormatFileName("Demo", Time.Zero, rate);
        Assert.Equal("Demo_00-00-00-00.png", fileNameZero);

        string fileNameNegative = StillCaptureNaming.FormatFileName("Demo", Time.FromSeconds(-5), rate);
        Assert.Equal("Demo_00-00-00-00.png", fileNameNegative);
    }

    [Fact]
    public void Available_DoesNotReplaceAnEarlierCapture()
    {
        using var dir = new Rodaje.App.Tests.Support.TempDirectory();
        string first = dir.File("Demo_00-00-03-00.png");
        Assert.Equal(first, StillCaptureNaming.Available(first));

        File.WriteAllText(first, "");
        File.WriteAllText(dir.File("Demo_00-00-03-00 (2).png"), "");
        Assert.Equal(dir.File("Demo_00-00-03-00 (3).png"), StillCaptureNaming.Available(first));
    }

    [Fact]
    public void ResolveCaptureFolder_UsesPreferenceWhenConfigured()
    {
        var settings = new AppSettings { StillCaptureFolder = @"C:\MisCapturas" };
        string folder = StillCaptureNaming.ResolveCaptureFolder(settings, @"D:\Proyectos\Test.rodaje");
        Assert.Equal(@"C:\MisCapturas", folder);
    }

    [Fact]
    public void ResolveCaptureFolder_DefaultsToCapturasNextToProject()
    {
        var settings = new AppSettings { StillCaptureFolder = null };
        string folder = StillCaptureNaming.ResolveCaptureFolder(settings, @"D:\Proyectos\Test.rodaje");
        Assert.Equal(@"D:\Proyectos\Capturas", folder);
    }
}
