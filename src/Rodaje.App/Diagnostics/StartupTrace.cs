using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Views.Inspector;

namespace Rodaje.App.Diagnostics;

/// <summary>
/// Marca el primer cuadro de XAML (<c>primer_render</c>) y cuándo la interfaz ya atiende (<c>interfaz_usable</c>: la cola
/// del hilo de la interfaz quedó vacía después de ese cuadro), y avisa entonces para lo que se dejó para después.
/// </summary>
internal static class StartupTrace
{
    public static void Watch(Metrics metrics, Action whenUsable)
    {
        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            metrics.Milestone("primer_render");
            _ = dispatcher.RunLater(DispatcherQueuePriority.Low, () =>
            {
                metrics.Milestone("interfaz_usable");
                whenUsable();
            });
        };
        CompositionTarget.Rendering += handler;
    }

    /// <summary>
    /// <c>--arranque</c>: con la interfaz lista (y, si se abrieron archivos, con el primer cuadro del video) escribe las
    /// marcas en el registro y cierra.
    /// </summary>
    public static async Task FinishProbeAsync(Metrics metrics, string? logPath, bool waitForFrame, Func<string?> failure, Action close)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while ((!metrics.HasMilestone("interfaz_usable") || (waitForFrame && !metrics.HasMilestone("primer_cuadro")))
            && failure() is null && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(10);
        }

        // Lo que se difirió al arrancar también cuenta: se espera un momento para que quede su marca.
        await Task.Delay(1500);
        string path = logPath ?? Path.Combine(AppContext.BaseDirectory, "arranque.txt");
        File.WriteAllText(path, metrics.Report(failure() is { } f ? ["error: " + f] : []), new System.Text.UTF8Encoding(false));
        if (failure() is not null || !metrics.HasMilestone("interfaz_usable"))
        {
            Program.ExitCode = 1;
        }

        close();
    }
}
