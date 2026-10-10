using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Inspector.Properties;

/// <summary>Propiedades del clip seleccionado con keyframes.</summary>
internal sealed partial class PropertiesPanel : UserControl, IEditorView
{
    public PropertiesPanel()
    {
        InitializeComponent();
    }

    public void Attach(EditorServices services)
    {
    }
}
