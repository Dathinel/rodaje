using Microsoft.UI.Xaml;
using Rodaje.App.Services;
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
    }

    // Red de seguridad: un error inesperado en un manejador (sobre todo los async void) se registra y se avisa en la
    // ventana en lugar de cerrar la app con el trabajo sin guardar.
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Program.WriteCrash(e.Exception?.ToString() ?? e.Message);
        if (_window is null)
        {
            StartupSplash.Close();
            return;
        }

        e.Handled = true;
        _window.ReportUnexpectedError(e.Exception?.Message ?? e.Message);
    }
}
