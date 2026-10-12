namespace Rodaje.App.Services.Settings;

/// <summary>Flujo de trabajo: proxies al importar, espacios de trabajo, carpetas vigiladas y el recorrido inicial.</summary>
public sealed partial class AppSettings
{
    /// <summary>Generar en segundo plano el proxy de los videos pesados al importarlos (<see cref="Media.AutoProxyRule"/>).</summary>
    public bool AutoProxies { get; set; } = true;
}
