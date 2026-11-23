using Rodaje.App.Services.Settings;
using Rodaje.App.Views.Preferences;
using Rodaje.Media.Gpu;

namespace Rodaje.App.Tests.Settings;

/// <summary>
/// GPU híbrida (Intel integrada + NVIDIA): en automática se pide la de alto rendimiento, salvo «Ahorro de energía» en
/// Windows › Gráficos; un nombre elegido en Preferencias (o en <c>RODAJE_ADAPTADOR</c>) manda.
/// </summary>
public class GpuAdapterPreferenceTests
{
    [Theory]
    [InlineData(null, null, GpuAdapterChoice.Mode.HighPerformance)]
    [InlineData("", "GpuPreference=0;", GpuAdapterChoice.Mode.HighPerformance)]
    [InlineData(null, "GpuPreference=2;", GpuAdapterChoice.Mode.HighPerformance)]
    [InlineData(null, "SwapEffectUpgradeEnable=1;GpuPreference=1;", GpuAdapterChoice.Mode.MinimumPower)]
    [InlineData("  ", "GpuPreference=1;", GpuAdapterChoice.Mode.MinimumPower)]
    [InlineData("RTX", "GpuPreference=1;", GpuAdapterChoice.Mode.Named)]
    [InlineData("Intel", null, GpuAdapterChoice.Mode.Named)]
    [InlineData(null, "GpuPreference=12;", GpuAdapterChoice.Mode.HighPerformance)]
    public void Decide(string? name, string? windows, GpuAdapterChoice.Mode expected) =>
        Assert.Equal(expected, GpuAdapterChoice.Decide(name, windows));

    [Fact]
    public void Las_opciones_llevan_la_automatica_cada_GPU_y_la_guardada_aunque_ya_no_este()
    {
        string[] hardware = ["Intel(R) UHD Graphics", "NVIDIA GeForce RTX 3050 Laptop GPU", "Intel(R) UHD Graphics"];

        Assert.Equal(new[] { PreferencesViewModel.AutomaticGpu, hardware[0], hardware[1] }, PreferencesViewModel.GpuAdapterOptions(hardware, null));
        Assert.Equal(3, PreferencesViewModel.GpuAdapterOptions(hardware, "nvidia geforce rtx 3050 laptop gpu").Count);
        Assert.Equal("AMD Radeon RX 7600", PreferencesViewModel.GpuAdapterOptions(hardware, "AMD Radeon RX 7600")[^1]);
    }

    [Fact]
    public void La_eleccion_se_guarda_y_vacia_es_automatica()
    {
        var settings = new AppSettings();
        var model = new PreferencesViewModel(settings) { GpuAdapter = " NVIDIA GeForce RTX 3050 Laptop GPU " };
        Assert.Equal("NVIDIA GeForce RTX 3050 Laptop GPU", settings.GpuAdapter);

        model.GpuAdapter = "";
        Assert.Null(settings.GpuAdapter);
    }
}
