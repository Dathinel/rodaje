namespace Rodaje.App.Views.Plugins.Logic;

/// <summary>Solo la confirmación explícita autoriza quitar un complemento.</summary>
public static class PluginUninstallDecision
{
    public static bool IsConfirmed(string? result) => result == "Primary";
}
