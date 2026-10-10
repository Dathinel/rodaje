using Microsoft.UI.Xaml;
using Rodaje.App.Player;

namespace Rodaje.App;

public partial class App : Application
{
    private readonly AppOptions _options;
    private readonly PlayerEngine _engine;
    private MainWindow? _window;

    internal App(AppOptions options, PlayerEngine engine)
    {
        _options = options;
        _engine = engine;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(_options, _engine);
        _window.Activate();
    }
}
