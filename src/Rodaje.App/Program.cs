using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Rodaje.App.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Views.Player;

namespace Rodaje.App;

/// <summary>Entrada propia (DISABLE_XAML_GENERATED_MAIN) para medir el arranque y poner el motor en marcha antes que WinUI.</summary>
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
            var settings = new SettingsStore(SettingsStore.DefaultPath);
            string? settingsWarning = settings.Load();
            // El motor (FFmpeg y la GPU) arranca en su hilo mientras WinUI levanta la ventana.
            using var engine = new PlayerEngine(options.EnableAudio, settings.Current.ProxyUsage, Metrics);
            engine.Start();
            // Los archivos de la línea de comandos se leen mientras WinUI levanta la ventana.
            var prober = new PrefetchingProber(new FfmpegMediaProber());
            prober.Prefetch(options.Files.Where(f => !ProjectPaths.IsProjectFile(f)));
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                Instance = new App(options, engine, prober, settings, settingsWarning);
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
