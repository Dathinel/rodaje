namespace Rodaje.App.Services.Settings;

/// <summary>
/// «Seguir a Windows» en caliente: Windows avisa de cada cambio de colores (varias veces seguidas y desde otro hilo) y
/// esto decide, ya en el hilo de la interfaz, si hay que volver a aplicar el tema: solo si los ajustes siguen a Windows
/// y el modo (claro u oscuro) es distinto del que se aplicó la última vez.
/// </summary>
public sealed class WindowsThemeFollower
{
    private bool? _appliedLight;

    /// <summary>Se aplicó el tema de los ajustes; con «Seguir a Windows», con este modo de Windows.</summary>
    public void Applied(string? themeSetting, bool windowsIsLight) =>
        _appliedLight = themeSetting == Personalization.FollowWindowsTheme ? windowsIsLight : null;

    /// <summary>Tras un aviso de Windows: si hay que volver a aplicar el tema con <paramref name="windowsIsLight"/>.</summary>
    public bool ShouldReapply(string? themeSetting, bool windowsIsLight) =>
        themeSetting == Personalization.FollowWindowsTheme && _appliedLight != windowsIsLight;
}
