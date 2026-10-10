using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Meters;

/// <summary>Medidores de pico de audio L/R.</summary>
internal sealed partial class MetersView : UserControl, IEditorView
{
    public MetersView()
    {
        InitializeComponent();
    }

    public void Attach(EditorServices services)
    {
    }

    /// <summary>Picos de cada canal entre 0 y 1 (lineales). Desde el hilo de la interfaz.</summary>
    public void SetLevels(double left, double right)
    {
        double height = Math.Max(0, ActualHeight - 28);
        LeftLevel.Height = Math.Clamp(left, 0, 1) * height;
        RightLevel.Height = Math.Clamp(right, 0, 1) * height;
    }
}
