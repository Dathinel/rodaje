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
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(_options, _engine, _prober, _settings, _settingsWarning);
        _window.Activate();
    }
}
