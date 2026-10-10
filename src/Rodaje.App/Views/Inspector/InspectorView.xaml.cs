using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Inspector;

/// <summary>Anfitrión de las pestañas del panel derecho.</summary>
internal sealed partial class InspectorView : UserControl, IEditorView
{
    public InspectorView()
    {
        InitializeComponent();
    }

    public enum Tab
    {
        Properties,
        Filters,
        History,
        Jobs,
    }

    public void Attach(EditorServices services)
    {
        PropertiesPanel.Attach(services);
        FiltersPanel.Attach(services);
        HistoryPanel.Attach(services);
        JobsPanel.Attach(services);
    }

    /// <summary>Muestra una pestaña (por ejemplo, desde el menú Ver).</summary>
    public void Show(Tab tab)
    {
        RadioButton button = tab switch
        {
            Tab.Filters => FiltersTab,
            Tab.History => HistoryTab,
            Tab.Jobs => JobsTab,
            _ => PropertiesTab,
        };
        button.IsChecked = true;
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (PropertiesPanel is null || FiltersPanel is null || HistoryPanel is null || JobsPanel is null)
        {
            return;
        }

        PropertiesPanel.Visibility = Visible(sender, PropertiesTab);
        FiltersPanel.Visibility = Visible(sender, FiltersTab);
        HistoryPanel.Visibility = Visible(sender, HistoryTab);
        JobsPanel.Visibility = Visible(sender, JobsTab);
    }

    private static Visibility Visible(object sender, RadioButton tab) =>
        ReferenceEquals(sender, tab) ? Visibility.Visible : Visibility.Collapsed;
}
