using Microsoft.UI.Xaml;

namespace Fase0.Uno;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = "Rodaje fase 0" };
        _window.Content = new MainPage(_window);
        _window.Activate();
    }
}
