using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services.Effects;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Inspector;

/// <summary>Anfitrión de las pestañas del panel derecho.</summary>
internal sealed partial class InspectorView : UserControl, IEditorView
{
    private bool _compactTabs;

    public InspectorView()
    {
        InitializeComponent();
        TabBar.SizeChanged += (_, _) => UpdateTabLabels();
    }

    public enum Tab
    {
        Properties,
        Text,
        Speed,
        Filters,
        History,
        Jobs,
    }

    public void Attach(EditorServices services)
    {
        if (services.Content is EffectContent content)
        {
            AppEffectContent.AttachSession(content, services.Session);
        }

        PropertiesPanel.Attach(services);
        TransitionPanel.ActiveChanged += (_, _) => UpdateVisibility();
        TransitionPanel.Attach(services);
        TextPanel.Attach(services);
        services.Session.TextPanelRequested += (_, _) => Show(Tab.Text);
        SpeedPanel.Attach(services);
        services.Session.SpeedPanelRequested += (_, _) => Show(Tab.Speed);
        FiltersPanel.Attach(services);
        HistoryPanel.Attach(services);
        JobsPanel.ActiveCountChanged += (_, active) =>
        {
            JobsLabel.Text = active > 0 ? $"Trabajos ({active})" : "Trabajos";
            ToolTipService.SetToolTip(JobsTab, JobsLabel.Text);
            UpdateTabLabels();
        };
        JobsPanel.Attach(services);
    }

    /// <summary>Muestra una pestaña (por ejemplo, desde el menú Ver).</summary>
    public void Show(Tab tab)
    {
        RadioButton button = tab switch
        {
            Tab.Text => TextTab,
            Tab.Speed => SpeedTab,
            Tab.Filters => FiltersTab,
            Tab.History => HistoryTab,
            Tab.Jobs => JobsTab,
            _ => PropertiesTab,
        };
        button.IsChecked = true;
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        UpdateVisibility();
        UpdateTabLabels();
        (sender as UIElement)?.StartBringIntoView();
    }

    private (RadioButton Tab, TextBlock Label)[] Tabs =>
        [(PropertiesTab, PropertiesLabel), (TextTab, TextLabel), (SpeedTab, SpeedLabel), (FiltersTab, FiltersLabel), (HistoryTab, HistoryLabel), (JobsTab, JobsLabel)];

    // Se mide con todos los nombres; si no caben, las pestañas pasan a ícono y solo la elegida conserva su nombre.
    private void UpdateTabLabels()
    {
        if (TabStrip is null || JobsLabel is null || TabBar.ActualWidth <= 0)
        {
            return;
        }

        foreach ((_, TextBlock label) in Tabs)
        {
            label.Visibility = Visibility.Visible;
            Icon(label).Visibility = Visibility.Collapsed;
        }

        TabStrip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _compactTabs = TabStrip.DesiredSize.Width > TabBar.ActualWidth - 1;
        foreach ((RadioButton tab, TextBlock label) in Tabs)
        {
            label.Visibility = !_compactTabs || tab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Icon(label).Visibility = _compactTabs ? Visibility.Visible : Visibility.Collapsed;
        }

        static UIElement Icon(TextBlock label) => ((Panel)label.Parent).Children[0];
    }

    // Con una transición seleccionada, Propiedades muestra la transición.
    private void UpdateVisibility()
    {
        if (PropertiesPanel is null || TransitionPanel is null || TextPanel is null || SpeedPanel is null || FiltersPanel is null || HistoryPanel is null || JobsPanel is null)
        {
            return;
        }

        bool properties = PropertiesTab.IsChecked == true;
        PropertiesPanel.Visibility = Visible(properties && !TransitionPanel.IsActive);
        TransitionPanel.Visibility = Visible(properties && TransitionPanel.IsActive);
        TextPanel.Visibility = Visible(TextTab.IsChecked == true);
        SpeedPanel.Visibility = Visible(SpeedTab.IsChecked == true);
        FiltersPanel.Visibility = Visible(FiltersTab.IsChecked == true);
        HistoryPanel.Visibility = Visible(HistoryTab.IsChecked == true);
        JobsPanel.Visibility = Visible(JobsTab.IsChecked == true);
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
