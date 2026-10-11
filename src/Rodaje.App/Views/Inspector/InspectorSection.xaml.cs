using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Rodaje.App.Services.Settings;

namespace Rodaje.App.Views.Inspector;

/// <summary>Sección plegable; si tiene <see cref="Key"/>, recuerda en los ajustes si quedó abierta.</summary>
[ContentProperty(Name = nameof(Body))]
internal sealed partial class InspectorSection : UserControl
{
    private bool _expanded = true;
    private SettingsStore? _settings;

    public InspectorSection()
    {
        InitializeComponent();
        UpdateExpanded();
    }

    public string Key { get; set; } = "";

    /// <summary>Si nunca se tocó, empieza plegada.</summary>
    public bool CollapsedByDefault { get; set; }

    public string Title
    {
        get => TitleText.Text;
        set
        {
            TitleText.Text = value;
            ToolTipService.SetToolTip(TitleText, value);
            AutomationProperties.SetName(ToggleButton, value);
            UpdateExpanded();
        }
    }

    /// <summary>Lo que se ve junto al título con la sección plegada («X −120 · Y 0»).</summary>
    public string Summary
    {
        get => SummaryText.Text;
        set => SummaryText.Text = value;
    }

    /// <summary>«3 keyframes»; vacío para ocultarlo.</summary>
    public string KeyframeSummary
    {
        get => KeyframeText.Text;
        set => KeyframeText.Text = value;
    }

    public UIElement? Body
    {
        get => BodyHost.Content as UIElement;
        set => BodyHost.Content = value;
    }

    /// <summary>Botones de la cabecera (restablecer la sección, menú «⋯»).</summary>
    public object? Actions
    {
        get => ActionsHost.Content;
        set => ActionsHost.Content = value;
    }

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value)
            {
                return;
            }

            _expanded = value;
            UpdateExpanded();
            if (_settings is not null && Key.Length > 0)
            {
                _settings.Current.InspectorSections[Key] = value;
            }
        }
    }

    /// <summary>Lee de los ajustes si estaba abierta y desde entonces guarda cada cambio.</summary>
    public void Remember(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = null;
        IsExpanded = settings.Current.InspectorSections.TryGetValue(Key, out bool open) ? open : !CollapsedByDefault;
        _settings = settings;
    }

    private void OnToggleClick(object sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;

    private void UpdateExpanded()
    {
        BodyHost.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Visibility = _expanded ? Visibility.Collapsed : Visibility.Visible;
        Chevron.Text = _expanded ? "" : "";
        string action = _expanded ? "Plegar" : "Desplegar";
        ToolTipService.SetToolTip(ToggleButton, action + " «" + TitleText.Text + "»");
        AutomationProperties.SetHelpText(ToggleButton, _expanded ? "Abierta" : "Plegada");
    }
}
