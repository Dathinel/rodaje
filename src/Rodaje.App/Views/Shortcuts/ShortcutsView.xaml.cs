using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Shortcuts;

/// <summary>Página de atajos de teclado.</summary>
internal sealed partial class ShortcutsView : UserControl, IEditorView
{
    public ShortcutsView()
    {
        InitializeComponent();
    }

    public void Attach(EditorServices services)
    {
    }
}
