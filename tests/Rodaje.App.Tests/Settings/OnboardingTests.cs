using Rodaje.App.Services.Onboarding;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Tests.Settings;

public class OnboardingTests
{
    [Fact]
    public void ElRecorridoTieneSeisPasosQueTerminanEnExportar()
    {
        Assert.Equal(6, OnboardingGuide.Tour.Count);
        Assert.Equal(TourTarget.Library, OnboardingGuide.Tour[0].Target);
        Assert.Equal(TourTarget.Export, OnboardingGuide.Tour[^1].Target);
        Assert.Equal(Enum.GetValues<TourTarget>().Length, OnboardingGuide.Tour.Select(s => s.Target).Distinct().Count());
    }

    [Fact]
    public void CadaPistaUnaSolaVezHastaReactivarlas()
    {
        var settings = new AppSettings();
        Assert.NotNull(OnboardingGuide.TakeHint(settings, OnboardingGuide.FirstImport));
        Assert.Null(OnboardingGuide.TakeHint(settings, OnboardingGuide.FirstImport));
        Assert.Null(OnboardingGuide.TakeHint(settings, "no-existe"));

        settings.HintsEnabled = false;
        Assert.Null(OnboardingGuide.TakeHint(settings, OnboardingGuide.FirstExport));

        OnboardingGuide.ResetHints(settings);
        Assert.NotNull(OnboardingGuide.TakeHint(settings, OnboardingGuide.FirstImport));
        Assert.Equal(OnboardingGuide.Hints.Count, OnboardingGuide.Hints.Select(h => h.Id).Distinct().Count());
    }
}
