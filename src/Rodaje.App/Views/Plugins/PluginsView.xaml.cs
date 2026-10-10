using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Plugins;

/// <summary>Página del gestor de complementos.</summary>
internal sealed partial class PluginsView : UserControl, IEditorView
{
    public PluginsView()
    {
        InitializeComponent();
    }

    public void Attach(EditorServices services)
    {
    }
}
