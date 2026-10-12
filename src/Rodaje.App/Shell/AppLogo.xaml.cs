using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Rodaje.App.Shell;

internal sealed partial class AppLogo : UserControl
{
    public AppLogo() => InitializeComponent();

    public bool Detailed
    {
        get => Large.Visibility == Visibility.Visible;
        set
        {
            Large.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Small.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
