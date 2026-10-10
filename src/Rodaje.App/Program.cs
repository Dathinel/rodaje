using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Rodaje.App.Diagnostics;

namespace Rodaje.App;

/// <summary>Entrada propia (DISABLE_XAML_GENERATED_MAIN) para medir el arranque y pasar los argumentos a la app.</summary>
internal static class Program
{
    public static Metrics Metrics { get; } = new();

    public static int ExitCode { get; set; }

    public static App? Instance { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        Metrics.Milestone("main");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject?.ToString());
        try
        {
            AppOptions options = AppOptions.Parse(args);
            // El motor (FFmpeg, GPU, abrir los medios) arranca en su hilo mientras WinUI levanta la ventana.
            using var engine = new Player.PlayerEngine(options.Files, options.EnableAudio, Metrics);
            engine.Start();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                Instance = new App(options, engine);
                Instance.UnhandledException += (_, e) => WriteCrash(e.Exception?.ToString() ?? e.Message);
            });
        }
#pragma warning disable CA1031 // Último recurso: deja el motivo en un archivo junto al exe.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            WriteCrash(ex.ToString());
            return 3;
        }

        return ExitCode;
    }

    /// <summary>Deja el error en <c>rodaje-error.txt</c> junto al exe: una app de ventana no tiene consola.</summary>
    public static void WriteCrash(string? text)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "rodaje-error.txt"), $"{DateTime.Now:O}\n{text}\n\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
