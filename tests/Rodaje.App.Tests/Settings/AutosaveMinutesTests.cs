using Rodaje.App.Services.Settings;
using Rodaje.App.Views.Preferences;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Settings;

/// <summary>Preferencias › General › Autoguardado: el intervalo se guarda en ajustes.json acotado de 1 a 30 minutos.</summary>
[Collection("StillImageDefaults")]
public sealed class AutosaveMinutesTests
{
    [Fact]
    public void DeFabricaSonDosMinutos()
    {
        Assert.Equal(AutosaveSchedule.DefaultIntervalMinutes, new AppSettings().AutosaveMinutes);
        Assert.Equal(2, AutosaveSchedule.DefaultIntervalMinutes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(15, 15)]
    [InlineData(120, 30)]
    public void ElModeloYNormalizeAcotanElIntervalo(int minutes, int expected)
    {
        var settings = new AppSettings();
        var model = new PreferencesViewModel(settings);

        model.AutosaveMinutes = minutes;
        Assert.Equal(expected, settings.AutosaveMinutes);
        Assert.Equal(expected, model.AutosaveMinutes);

        settings.AutosaveMinutes = minutes;
        settings.Normalize();
        Assert.Equal(expected, settings.AutosaveMinutes);
    }
}
