using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Settings;
using Rodaje.App.Shell;
using Rodaje.App.Views.Inspector;
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
            MainWindow window = _window;
            _ = DispatcherQueue.GetForCurrentThread().RunLater(DispatcherQueuePriority.Low, () => UiTask.Run(OfferAfterUncleanExitAsync(window)));
        }
    }

    /// <summary>Lo llama el contexto del hilo de la interfaz: informe «recuperado» y aviso, y la app sigue abierta.</summary>
    // Una ráfaga (el mismo error en cada tick) avisa una vez por intervalo, como el informe.
    internal bool HandleDispatcherException(Exception exception) =>
        CrashRouting.OnDispatcherException(exception, ShowUnexpectedError(), Program.ReportException);

    private Action<string>? ShowUnexpectedError() => _window is { } window ? window.ReportUnexpectedError : null;

    /// <summary>
    /// Avisa en la ventana de un error ya informado en <paramref name="reportPath"/>, con el texto fijo de
    /// <see cref="UnexpectedErrorNotice"/>; desde cualquier hilo (pasa por la cola de la interfaz).
    /// </summary>
    internal void NotifyUnexpectedError(string? reportPath)
    {
        if (_window is not { } window)
        {
            return;
        }

        // Los errores esperables al avisar solo van al registro. Otro error pasa por la red de RunLater (informe y un aviso
        // directo); si ese aviso también falla, sigue su camino: no se vuelve a encolar otro aviso.
        _ = window.DispatcherQueue.RunLater(() =>
        {
            try
            {
                window.ReportUnexpectedError(UnexpectedErrorNotice.Describe(reportPath));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Program.Log.Append("No se pudo avisar de un error: " + ex.Message);
            }
        });
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

    // Si la vez anterior la app no se cerró bien, un solo aviso (la barra muestra uno): «Recuperar» si quedaron cambios
    // sin guardar, con «Copiar informe» al lado si hay informe; si no, el del informe solo.
    private async Task OfferAfterUncleanExitAsync(MainWindow window)
    {
        IReadOnlyList<string> pending = Program.Crashes.PendingReports();
        NotificationAction? copy = pending.Count > 0 ? new NotificationAction("Copiar informe", () => CopyReport(pending[^1])) : null;
        if (await window.OfferRecoveryAsync(copy) is null && copy is not null)
        {
            window.Session.Notify(NotificationSeverity.Warning, "Rodaje se cerró por un error", CrashReporter.DescribePending(pending), copy);
        }

        if (copy is not null)
        {
            Program.Crashes.AcknowledgePending();
        }
    }

    private void CopyReport(string path)
    {
        bool copied = CrashRouting.TryCopyReport(path, text =>
        {
            var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }, Program.Log);
        if (copied)
        {
            _window?.Session.Notify(NotificationSeverity.Success, "Informe copiado", "Pégalo en un correo o en un reporte de GitHub.");
        }
        else
        {
            _window?.Session.Notify(NotificationSeverity.Warning, "No se pudo copiar el informe", $"Está en {path}.");
        }
    }

    // Red de seguridad: un error inesperado en un manejador (sobre todo los async void) se registra y se avisa en la
    // ventana en lugar de cerrar la app con el trabajo sin guardar.
    // Sin ventana no se atiende: WinUI cierra la app (error «stowed»), pero el informe ya quedó escrito.
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (CrashRouting.OnXamlUnhandled(e.Exception, e.Message, ShowUnexpectedError(), Program.ReportException, Program.Crashes.Write))
        {
            e.Handled = true;
        }
        else
        {
            StartupSplash.Close();
        }
    }
}
