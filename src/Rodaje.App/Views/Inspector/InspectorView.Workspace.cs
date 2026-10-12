using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Rodaje.App.Views.Inspector;

/// <summary>Pestañas que esconde el espacio de trabajo, por su nombre visible; una pestaña que se pide se vuelve a mostrar.</summary>
internal sealed partial class InspectorView
{
    private bool _tabsHooked;

    /// <summary>Nombre visible de la pestaña elegida («Propiedades», «Filtros»…).</summary>
    public string? CurrentTabName => TabButtons().FirstOrDefault(t => t.IsChecked == true) is { } tab ? AutomationProperties.GetName(tab) : null;

    /// <summary>Esconde las pestañas de <paramref name="hidden"/> (las demás se ven) y elige <paramref name="active"/> si está.</summary>
    public void ApplyTabs(IReadOnlyCollection<string> hidden, string? active)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        RadioButton[] tabs = TabButtons();
        if (!_tabsHooked)
        {
            _tabsHooked = true;
            foreach (RadioButton tab in tabs)
            {
                tab.Checked += (_, _) => tab.Visibility = Visibility.Visible;
            }
        }

        foreach (RadioButton tab in tabs)
        {
            tab.Visibility = hidden.Contains(AutomationProperties.GetName(tab)) && tab.IsChecked != true ? Visibility.Collapsed : Visibility.Visible;
        }

        RadioButton? wanted = active is null ? null : tabs.FirstOrDefault(t => AutomationProperties.GetName(t) == active);
        if (wanted is null && tabs.FirstOrDefault(t => t.IsChecked == true) is { } current && hidden.Contains(AutomationProperties.GetName(current)))
        {
            wanted = tabs.FirstOrDefault(t => !hidden.Contains(AutomationProperties.GetName(t)));
        }

        if (wanted is not null)
        {
            wanted.IsChecked = true;
        }

        foreach (RadioButton tab in tabs.Where(t => t.IsChecked != true && hidden.Contains(AutomationProperties.GetName(t))))
        {
            tab.Visibility = Visibility.Collapsed;
        }
    }

    private RadioButton[] TabButtons() => [.. TabStrip.Children.OfType<RadioButton>()];
}
