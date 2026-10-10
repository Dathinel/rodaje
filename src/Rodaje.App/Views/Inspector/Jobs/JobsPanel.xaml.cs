using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Inspector.Jobs;

/// <summary>Trabajos de proxies y exportación con su progreso.</summary>
internal sealed partial class JobsPanel : UserControl, IEditorView
{
    public JobsPanel()
    {
        InitializeComponent();
    }

    public void Attach(EditorServices services)
    {
    }
}
