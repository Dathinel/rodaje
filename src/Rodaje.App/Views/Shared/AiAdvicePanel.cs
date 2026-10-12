using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.Media.Ai;

namespace Rodaje.App.Views.Shared;

/// <summary>
/// La línea que va antes de lanzar una función de IA: el tiempo estimado en este equipo y dónde corre y, si la memoria no
/// alcanza, el aviso con la alternativa (un botón si la alternativa es un cambio en el mismo diálogo).
/// </summary>
internal sealed class AiAdvicePanel
{
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button _alternative = new() { Visibility = Visibility.Collapsed };
    private Action? _onAlternative;

    public AiAdvicePanel()
    {
        Panel = new StackPanel { Spacing = 4 };
        Panel.Children.Add(_summary);
        Panel.Children.Add(_warning);
        Panel.Children.Add(_alternative);
        _alternative.Click += (_, _) => _onAlternative?.Invoke();
        if (Application.Current?.Resources.TryGetValue("SystemFillColorCautionBrush", out object? brush) == true && brush is Microsoft.UI.Xaml.Media.Brush caution)
        {
            _warning.Foreground = caution;
        }
    }

    public StackPanel Panel { get; }

    /// <summary>Muestra <paramref name="estimate"/>; con <paramref name="onAlternative"/>, su alternativa es un botón.</summary>
    public void Show(AiEstimate? estimate, Action? onAlternative = null)
    {
        Panel.Visibility = estimate is null ? Visibility.Collapsed : Visibility.Visible;
        if (estimate is null)
        {
            return;
        }

        _summary.Text = estimate.Summary + (estimate.Measured ? "" : " Es una estimación: se afina al usarla.");
        _warning.Text = estimate.Warning is null ? "" : estimate.Warning + (onAlternative is null && estimate.Alternative is { } text ? " " + text : "");
        _warning.Visibility = estimate.Warning is null ? Visibility.Collapsed : Visibility.Visible;
        _onAlternative = onAlternative;
        _alternative.Content = estimate.Alternative ?? "";
        _alternative.Visibility = estimate.Warning is not null && estimate.Alternative is not null && onAlternative is not null ? Visibility.Visible : Visibility.Collapsed;
    }
}
