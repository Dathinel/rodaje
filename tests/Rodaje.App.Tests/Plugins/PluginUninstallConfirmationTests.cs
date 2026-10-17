using Rodaje.App.Views.Plugins.Logic;

namespace Rodaje.App.Tests.Plugins;

public sealed class PluginUninstallConfirmationTests
{
    [Theory]
    [InlineData("None")]
    [InlineData("Secondary")]
    [InlineData(null)]
    public void Cancelar_o_cerrar_no_autoriza_la_desinstalacion(string? result) =>
        Assert.False(PluginUninstallDecision.IsConfirmed(result));

    [Fact]
    public void Confirmar_autoriza_la_desinstalacion() =>
        Assert.True(PluginUninstallDecision.IsConfirmed("Primary"));
}
