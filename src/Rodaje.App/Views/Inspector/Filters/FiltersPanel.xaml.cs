using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Inspector.Filters;

/// <summary>Pila de filtros del clip seleccionado.</summary>
internal sealed partial class FiltersPanel : UserControl, IEditorView
{
    public FiltersPanel()
    {
        InitializeComponent();
    }

    public void Attach(EditorServices services)
    {
    }
}
