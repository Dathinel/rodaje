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
        TransitionPanel.ActiveChanged += (_, _) => UpdateVisibility();
        TransitionPanel.Attach(services);
        FiltersPanel.Attach(services);
        HistoryPanel.Attach(services);
        JobsPanel.ActiveCountChanged += (_, active) => JobsTab.Content = active > 0 ? $"Trabajos ({active})" : "Trabajos";
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

    private void OnTabChecked(object sender, RoutedEventArgs e) => UpdateVisibility();

    // Con una transición seleccionada, Propiedades muestra la transición.
    private void UpdateVisibility()
    {
        if (PropertiesPanel is null || TransitionPanel is null || FiltersPanel is null || HistoryPanel is null || JobsPanel is null)
        {
            return;
        }

        bool properties = PropertiesTab.IsChecked == true;
        PropertiesPanel.Visibility = Visible(properties && !TransitionPanel.IsActive);
        TransitionPanel.Visibility = Visible(properties && TransitionPanel.IsActive);
        FiltersPanel.Visibility = Visible(FiltersTab.IsChecked == true);
        HistoryPanel.Visibility = Visible(HistoryTab.IsChecked == true);
        JobsPanel.Visibility = Visible(JobsTab.IsChecked == true);
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
