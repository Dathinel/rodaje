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
    public void LosTextosNombranAtajosQueExistenEnLosTresEstilos()
    {
        foreach (TourStep step in OnboardingGuide.Tour)
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Title));
            Assert.False(string.IsNullOrWhiteSpace(step.Text));
        }

        foreach (Hint hint in OnboardingGuide.Hints)
        {
            Assert.False(string.IsNullOrWhiteSpace(hint.Title));
            Assert.False(string.IsNullOrWhiteSpace(hint.Text));
            // Ctrl+Y solo rehace en Rodaje y Clásico; Ctrl+Mayús+Z, en los tres estilos.
            Assert.DoesNotContain("Ctrl+Y", hint.Text);
        }

        Assert.Contains("Ctrl+Mayús+Z", OnboardingGuide.Hints.Single(h => h.Id == OnboardingGuide.FirstEdit).Text);
        Assert.Contains("Ctrl+I", OnboardingGuide.Tour[0].Text);
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
