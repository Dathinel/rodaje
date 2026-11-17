using Uno.UI.Hosting;

namespace Rodaje.App;

internal static partial class Program
{
    /// <summary>Levanta Uno (Skia sobre X11) y crea la app con <paramref name="createApp"/>; vuelve al cerrarse la ventana.</summary>
    private static void RunInterface(Action createApp)
    {
        // Los FFmpeg y proxies que dejó vivos una sesión anterior que se cayó (en Windows los cierra el Job Object).
        _ = Task.Run(Rodaje.Media.Proxies.ChildProcessGuard.SweepOrphans);
        Metrics.Milestone("winui_start");
        UnoPlatformHostBuilder.Create()
            .App(() =>
            {
                createApp();
                return Instance!;
            })
            .UseX11()
            .Build()
            .Run();
    }
}
