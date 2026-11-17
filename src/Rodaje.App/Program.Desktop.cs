using Uno.UI.Hosting;

namespace Rodaje.App;

internal static partial class Program
{
    /// <summary>Levanta Uno (Skia sobre X11) y crea la app con <paramref name="createApp"/>; vuelve al cerrarse la ventana.</summary>
    private static void RunInterface(Action createApp)
    {
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
