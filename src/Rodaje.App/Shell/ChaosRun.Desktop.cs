using Rodaje.App.Views.Player;

namespace Rodaje.App.Shell;

/// <summary><c>--caos</c> solo existe en Windows (automatización de WinUI); en Linux sale con un error.</summary>
internal static class ChaosRun
{
    public static Task RunAsync(MainWindow window, PlayerEngine engine, int seed, double minutes, IReadOnlyList<string> corpus)
    {
        Program.WriteCrash("caos: el modo caos solo está en Windows.");
        Program.ExitCode = 1;
        window.Close();
        return Task.CompletedTask;
    }
}
