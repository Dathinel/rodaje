using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Rodaje.App.Services.Crashes;

namespace Rodaje.App;

internal static partial class Program
{
    /// <summary>Levanta WinUI y crea la app con <paramref name="createApp"/>; vuelve al cerrarse la última ventana.</summary>
    private static void RunInterface(Action createApp)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Metrics.Milestone("winui_start");
        Application.Start(_ =>
        {
            // Envuelto: una excepción en una continuación del hilo de la interfaz deja informe y aviso en vez de
            // cerrar la app con un error «stowed» de WinRT.
            SynchronizationContext.SetSynchronizationContext(new GuardedSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()), OnDispatcherException));
            createApp();
        });
    }
}
