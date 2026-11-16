using Uno.UI.Hosting;

namespace Fase0.Uno;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseMacOS()
            .UseWin32()
            .Build()
            .Run();
    }
}
