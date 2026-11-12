using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
        Masks,
        Graph,
        Tracking,
        Dynamics,
        Transition,
        History,
        Jobs,
    }

    public event EventHandler<Tab>? GlobalPanelRequested;

    public void Attach(EditorServices services)
    {
        if (services.Content is EffectContent content)
        {
            AppEffectContent.AttachSession(content, services.Session);
        }

        PropertiesPanel.Attach(services);
        GraphPanel.Attach(services);
        TransitionPanel.ActiveChanged += (_, _) => OnTransitionActiveChanged();
        TransitionPanel.Attach(services);
        TextPanel.Attach(services);
        services.Session.TextPanelRequested += (_, _) => Show(Tab.Text);
        SpeedPanel.Attach(services);
        services.Session.SpeedPanelRequested += (_, _) => Show(Tab.Speed);
        FiltersPanel.Attach(services);
        MasksPanel.Attach(services);
        services.Session.MaskPanelRequested += (_, _) => Show(Tab.Masks);
        TrackingPanel.Attach(services);
        services.Session.TrackPanelRequested += (_, _) => Show(Tab.Tracking);
        DynamicsPanel.Attach(services);
        services.Session.DynamicsPanelRequested += (_, _) => Show(Tab.Dynamics);
        services.Session.PropertiesPanelRequested += (_, _) => Show(Tab.Properties);
        ApplyTabs(services.Settings.Current.HiddenInspectorTabs, null);
    }

    /// <summary>Muestra una pestaña (por ejemplo, desde el menú Ver).</summary>
    /// <summary>La pestaña Propiedades, para la autoprueba.</summary>
    internal Properties.PropertiesPanel Properties => PropertiesPanel;

    public void Show(Tab tab)
    {
        if (tab is Tab.History or Tab.Jobs)
        {
            GlobalPanelRequested?.Invoke(this, tab);
            return;
        }

        if (TransitionPanel.IsActive && tab is not (Tab.Transition or Tab.History or Tab.Jobs))
        {
            tab = Tab.Transition;
        }

        RadioButton button = tab switch
        {
            Tab.Graph => GraphTab,
            Tab.Text => TextTab,
            Tab.Speed => SpeedTab,
            Tab.Filters => FiltersTab,
            Tab.Masks => MasksTab,
            Tab.Tracking => TrackingTab,
            Tab.Dynamics => DynamicsTab,
            Tab.Transition => TransitionTab,
            _ => PropertiesTab,
        };
        button.IsChecked = true;
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        UpdateTabVisibility();
        UpdateVisibility();
        UpdateTabLabels();
        (sender as UIElement)?.StartBringIntoView();
    }

    private (RadioButton Tab, TextBlock Label)[] Tabs =>
        [(PropertiesTab, PropertiesLabel), (TextTab, TextLabel), (SpeedTab, SpeedLabel), (FiltersTab, FiltersLabel), (MasksTab, MasksLabel), (GraphTab, GraphLabel), (TrackingTab, TrackingLabel), (DynamicsTab, DynamicsLabel), (TransitionTab, TransitionLabel)];

    // Se mide con todos los nombres; si no caben, las pestañas pasan a ícono y solo la elegida conserva su nombre.
    private void UpdateTabLabels()
    {
        // Mientras se carga el XAML, el Checked de la pestaña inicial llega antes que el resto de pestañas y nombres.
        if (TabStrip is null || TabBar is null || TabBar.ActualWidth <= 0
            || Tabs.Any(t => t.Tab is null || t.Label?.Parent is not Panel { Children.Count: > 0 }))
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
        if (PropertiesPanel is null || TransitionPanel is null || TextPanel is null || SpeedPanel is null || FiltersPanel is null || MasksPanel is null || TrackingPanel is null || DynamicsPanel is null)
        {
            return;
        }

        bool properties = PropertiesTab.IsChecked == true;
        bool transition = TransitionTab.IsChecked == true && TransitionPanel.IsActive;
        PropertiesPanel.Visibility = Visible(properties && !TransitionPanel.IsActive);
        if (GraphPanel is not null)
        {
            GraphPanel.Visibility = Visible(GraphTab.IsChecked == true);
        }
        TransitionPanel.Visibility = Visible(transition);
        TextPanel.Visibility = Visible(TextTab.IsChecked == true);
        SpeedPanel.Visibility = Visible(SpeedTab.IsChecked == true);
        FiltersPanel.Visibility = Visible(FiltersTab.IsChecked == true);
        MasksPanel.Visibility = Visible(MasksTab.IsChecked == true);
        TrackingPanel.Visibility = Visible(TrackingTab.IsChecked == true);
        DynamicsPanel.Visibility = Visible(DynamicsTab.IsChecked == true);
    }

    private void OnTransitionActiveChanged()
    {
        if (TransitionPanel.IsActive)
        {
            Show(Tab.Transition);
        }
        else if (TransitionTab.IsChecked == true)
        {
            Show(Tab.Properties);
        }

        UpdateTabVisibility();
        UpdateVisibility();
    }

    private void UpdateTabVisibility()
    {
        // Checked de la pestaña inicial salta mientras se carga el XAML, antes de que existan el resto de pestañas y el panel.
        if (TransitionPanel is null || TransitionTab is null)
        {
            return;
        }

        bool transition = TransitionPanel.IsActive;
        foreach ((RadioButton tab, _) in Tabs)
        {
            if (tab is null)
            {
                continue;
            }

            string name = AutomationProperties.GetName(tab);
            tab.Visibility = transition
                ? tab == TransitionTab ? Visibility.Visible : Visibility.Collapsed
                : tab == TransitionTab || (_hiddenTabs.Contains(name) && tab.IsChecked != true) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
