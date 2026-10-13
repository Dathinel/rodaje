using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Settings;
using Rodaje.App.Shell;
using Rodaje.App.Views.Player;

namespace Rodaje.App;

public partial class App : Application
{
    private readonly AppOptions _options;
    private readonly PlayerEngine _engine;
    private readonly IMediaProber _prober;
    private readonly SettingsStore _settings;
    private readonly string? _settingsWarning;
    private MainWindow? _window;

    internal App(AppOptions options, PlayerEngine engine, IMediaProber prober, SettingsStore settings, string? settingsWarning)
    {
        _options = options;
        _engine = engine;
        _prober = prober;
        _settings = settings;
        _settingsWarning = settingsWarning;
        InitializeComponent();
        Program.Metrics.Milestone("app_xaml");
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Program.Metrics.Milestone("onlaunched");
        // Antes de crear la ventana: en el constructor Application.Current todavía no da sus recursos.
        Theme.ThemeResources.ApplyHighContrast(_options.SelfTest);
        _window = new MainWindow(_options, _engine, _prober, _settings, _settingsWarning);
        _window.Activate();
        // Apagar o cerrar la sesión de Windows no es un fallo: se cierra la sesión del registro antes de que Windows
        // termine el proceso.
        _ = SessionEndWatcher.Install(WinRT.Interop.WindowNative.GetWindowHandle(_window), Program.Crashes.EndSession);
        LogSessionTrail(_window.Session);
        if (!_options.IsAutomated)
        {
            // Después de los avisos del arranque, para que no lo tapen.
            _ = DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, OfferPendingCrashReports);
        }
    }

    /// <summary>Lo llama el contexto del hilo de la interfaz: informe «recuperado» y aviso, y la app sigue abierta.</summary>
    internal bool HandleDispatcherException(Exception exception)
    {
        if (_window is null)
        {
            Program.ReportException(CrashKind.DispatcherCallback, exception, fatal: true);
            return false;
        }

        // Una ráfaga (el mismo error en cada tick) avisa una vez por intervalo, como el informe.
        Program.ReportException(CrashKind.DispatcherCallback, exception, fatal: false, out bool repeated);
        if (!repeated)
        {
            _window.ReportUnexpectedError(exception.Message);
        }

        return true;
    }

    // Rastro para el informe de un fallo: los pasos del historial y los avisos, en el registro de la sesión.
    private static void LogSessionTrail(EditorSession session)
    {
        string? lastStep = null;
        session.HistoryChanged += (_, _) =>
        {
            if (session.UndoDescription is { } step && step != lastStep)
            {
                lastStep = step;
                Program.Log.Append("Paso: " + step);
            }
        };
        session.Notified += (_, n) => Program.Log.Append($"Aviso ({n.Severity}): {n.Title}. {n.Message}");
    }

    // Si la vez anterior la app se cerró por un error, lo dice y ofrece copiar el informe para enviarlo.
    private void OfferPendingCrashReports()
    {
        IReadOnlyList<string> pending = Program.Crashes.PendingReports();
        if (pending.Count == 0 || _window is null)
        {
            return;
        }

        Program.Crashes.AcknowledgePending();
        string latest = pending[^1];
        _window.Session.Notify(NotificationSeverity.Warning, "Rodaje se cerró por un error", CrashReporter.DescribePending(pending),
            new NotificationAction("Copiar informe", () => CopyReport(latest)));
    }

    private void CopyReport(string path)
    {
        try
        {
            var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(File.ReadAllText(path));
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            _window?.Session.Notify(NotificationSeverity.Success, "Informe copiado", "Pégalo en un correo o en un reporte de GitHub.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _window?.Session.Notify(NotificationSeverity.Warning, "No se pudo copiar el informe", $"Está en {path}.");
        }
    }

    // Red de seguridad: un error inesperado en un manejador (sobre todo los async void) se registra y se avisa en la
    // ventana en lugar de cerrar la app con el trabajo sin guardar.
    // Sin ventana no se atiende: WinUI cierra la app (error «stowed»), pero el informe ya quedó escrito.
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        bool fatal = _window is null;
        bool repeated;
        if (e.Exception is { } exception)
        {
            Program.ReportException(CrashKind.XamlUnhandled, exception, fatal, out repeated);
        }
        else
        {
            Program.Crashes.Write(CrashKind.XamlUnhandled, e.Message, fatal, out repeated);
        }

        if (_window is null)
        {
            StartupSplash.Close();
            return;
        }

        e.Handled = true;
        if (!repeated)
        {
            _window.ReportUnexpectedError(e.Exception?.Message ?? e.Message);
        }
    }
}
