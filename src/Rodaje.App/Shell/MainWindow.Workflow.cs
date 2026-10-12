using Rodaje.App.Services;
using Rodaje.App.Services.Media;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Shell;

/// <summary>Flujo de trabajo: proxies al importar y lo que se revisa con el temporizador de los ajustes.</summary>
#pragma warning disable CA1001 // Lo descartable se libera en OnClosed (MainWindow.xaml.cs).
internal sealed partial class MainWindow
#pragma warning restore CA1001
{
    private AutoProxyService CreateAutoProxies(EditorSession session)
    {
        var service = new AutoProxyService(
            () => _settings.Current.ProxyUsage == ProxyUsage.Never ? null : _engine.ProxyResolver,
            () => _engine.DedicatedVideoMemory,
            () => _settings.Current.AutoProxies && _settings.Current.ProxyUsage != ProxyUsage.Never,
            SynchronizationContext.Current);
        session.MediaImported += (_, media) =>
        {
            int count = service.OnImported(media);
            if (count > 0)
            {
                session.Notify(NotificationSeverity.Informational, "Proxies en segundo plano",
                    (count == 1 ? "Un video pesado tendrá" : $"{count} videos pesados tendrán")
                    + " una copia liviana para editar con fluidez. El avance se ve en la portada y en Trabajos; la exportación usa los originales.");
            }
        };
        return service;
    }

    private void OnWorkflowTick() => _services.AutoProxies?.Tick();
}
