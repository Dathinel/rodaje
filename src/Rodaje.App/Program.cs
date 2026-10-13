using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Rodaje.App.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Settings;
using Rodaje.App.Shell;
using Rodaje.App.Views.Player;
using Rodaje.Engine.Timeline;

namespace Rodaje.App;

/// <summary>Entrada propia (DISABLE_XAML_GENERATED_MAIN) para medir el arranque y poner el motor en marcha antes que WinUI.</summary>
internal static class Program
{
    public static Metrics Metrics { get; } = new();

    public static int ExitCode { get; set; }

    public static App? Instance { get; private set; }

    /// <summary>Las últimas líneas de la sesión, para el informe de un fallo.</summary>
    public static SessionLog Log { get; } = new();

    /// <summary>Informes de fallo en <c>%LOCALAPPDATA%\Rodaje\fallos\</c>.</summary>
    public static CrashReporter Crashes { get; } = new(CrashReporter.DefaultFolder, ReadVersion(), Log, DescribeGpu);

    [STAThread]
    private static int Main(string[] args)
    {
        Metrics.Milestone("main");
        InstallCrashReporting();
        try
        {
            AppOptions options = AppOptions.Parse(args);
            var settings = new SettingsStore(SettingsStore.DefaultPath);
            string? settingsWarning = settings.Load();
            Metrics.Milestone("ajustes");
            StartupSplash.Show(settings.Current);
            // Sin archivos que abrir, el motor (FFmpeg y la GPU) arranca después del primer cuadro de la ventana (MainWindow):
            // cargar sus DLL a la vez que las de WinUI se disputa el cargador de Windows y el disco, y en frío retrasaba la
            // ventana medio segundo. Con archivos, el primer cuadro del video manda: arranca ya (200 ms antes).
            using var engine = new PlayerEngine(options.EnableAudio, options.NoProxies ? ProxyUsage.Never : settings.Current.ProxyUsage, Metrics);
            if (options.Files.Count > 0)
            {
                engine.Start();
                Metrics.Milestone("motor_lanzado");
            }
            // Los archivos de la línea de comandos se leen mientras WinUI levanta la ventana.
            var prober = new PrefetchingProber(new FfmpegMediaProber());
            prober.Prefetch(options.Files.Where(f => !ProjectPaths.IsProjectFile(f)));
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Metrics.Milestone("winui_start");
            Application.Start(_ =>
            {
                // Envuelto: una excepción en una continuación del hilo de la interfaz deja informe y aviso en vez de
                // cerrar la app con un error «stowed» de WinRT.
                SynchronizationContext.SetSynchronizationContext(new GuardedSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()), OnDispatcherException));
                Instance = new App(options, engine, prober, settings, settingsWarning);
            });
        }
#pragma warning disable CA1031 // Último recurso: deja el motivo en un archivo junto al exe.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StartupSplash.Close();
            AppendErrorFile(ex.ToString());
            Crashes.Write(CrashKind.Startup, ex, fatal: true);
            return 3;
        }

        Crashes.EndSession();
        return ExitCode;
    }

    /// <summary>
    /// Deja el error en <c>rodaje-error.txt</c> junto al exe (una app de ventana no tiene consola) y un informe
    /// «recuperado» en <c>fallos\</c>, que se puede escribir aunque la carpeta del exe no (Archivos de programa).
    /// </summary>
    public static void WriteCrash(string? text)
    {
        AppendErrorFile(text);
        Crashes.Write(CrashKind.Caught, text, fatal: false);
    }

    /// <summary>Informa una excepción que llegó por una de las vías sin atrapar (también en <c>rodaje-error.txt</c>).</summary>
    public static string? ReportException(CrashKind kind, Exception exception, bool fatal)
    {
        AppendErrorFile(exception.ToString());
        return Crashes.Write(kind, exception, fatal);
    }

    private static void InstallCrashReporting()
    {
        // Las vías de excepción y el filtro nativo, antes que nada: un fallo al leer los ajustes también deja rastro.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppendErrorFile(e.ExceptionObject?.ToString());
            Crashes.OnAppDomainUnhandled(AppDomain.CurrentDomain, e);
        };
        TaskScheduler.UnobservedTaskException += Crashes.OnUnobservedTask;
        NativeCrashHandler.Install(Crashes);
        System.Diagnostics.Trace.Listeners.Add(new SessionLogTraceListener(Log));
        Log.Append($"Rodaje {Crashes.Version} arranca: {Environment.CommandLine}");
        // Las sesiones anteriores que terminaron sin cerrarse ni dejar informe se vuelven informe; después, la copia en
        // disco del registro de esta sesión.
        _ = Crashes.RecoverUncleanSessions();
        Crashes.StartSession();
        // La GPU se lee ahora, fuera de cualquier fallo: un fallo nativo no debe tocar DXGI.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Crashes.WarmUp();
        });
    }

    private static bool OnDispatcherException(Exception exception)
    {
        if (Instance is { } app)
        {
            return app.HandleDispatcherException(exception);
        }

        // Sin ventana todavía, sigue su camino (y la app se cierra), pero con informe.
        ReportException(CrashKind.DispatcherCallback, exception, fatal: true);
        return false;
    }

    private static void AppendErrorFile(string? text)
    {
        Log.Append("Error: " + FirstLine(text));
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

    private static string FirstLine(string? text)
    {
        string value = text ?? "(sin detalles)";
        int end = value.IndexOfAny(['\r', '\n']);
        return end < 0 ? value : value[..end];
    }

    private static string ReadVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    private static string? DescribeGpu()
    {
        Rodaje.Media.Gpu.GpuProfile profile = Rodaje.Media.Gpu.GpuProfileReader.Current;
        return profile.Primary is { } gpu
            ? $"{gpu.Name} (controlador {gpu.DriverVersion ?? "?"}, {gpu.DedicatedVideoMemory / (1024 * 1024)} MB de VRAM)"
            : "sin GPU de hardware (WARP o CPU)";
    }
}
