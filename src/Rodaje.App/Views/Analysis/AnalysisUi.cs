using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.Analysis;
using Rodaje.App.Services.Format;
using Rodaje.App.Theme;

namespace Rodaje.App.Views.Analysis;

/// <summary>Piezas comunes de los diálogos de análisis: diálogo con progreso, deslizadores con su valor y la barra de vista previa.</summary>
internal static class AnalysisUi
{
    public const double PreviewWidth = 460;

    public static ContentDialog Dialog(XamlRoot root, string title, string primary, UIElement content, AnalysisApplyPhase initialPhase = AnalysisApplyPhase.Blocked) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = "Cancelar",
        DefaultButton = ContentDialogButton.Primary,
        IsPrimaryButtonEnabled = AnalysisApplyState.IsEnabled(initialPhase),
    };

    /// <summary>Deslizador con etiqueta y valor; <paramref name="changed"/> se llama al moverlo.</summary>
    public static Slider Slider(Panel host, string label, double min, double max, double value, double step, Func<double, string> format, Action changed)
    {
        var text = new TextBlock { Text = $"{label}: {format(value)}" };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, StepFrequency = step, IsThumbToolTipEnabled = false, Width = PreviewWidth };
        slider.ValueChanged += (_, e) =>
        {
            text.Text = $"{label}: {format(e.NewValue)}";
            changed();
        };
        host.Children.Add(text);
        host.Children.Add(slider);
        return slider;
    }

    public static string Db(double v) => string.Create(SpanishNumbers.Format, $"{v:0} dB");

    public static string Ms(double seconds) => string.Create(SpanishNumbers.Format, $"{seconds * 1000:0} ms");

    public static string Sec(double seconds) => string.Create(SpanishNumbers.Format, $"{seconds:0.0#} s");

    /// <summary>Barra del clip con los tramos marcados encima (los que se cortan, la voz, los pulsos).</summary>
    public static void DrawRanges(Canvas canvas, double duration, IEnumerable<TimeRange> ranges, Windows.UI.Color color)
    {
        canvas.Children.Clear();
        canvas.Children.Add(new Rectangle { Width = PreviewWidth, Height = canvas.Height, Fill = ThemeResources.Brush("ControlBrush") });
        if (duration <= 0)
        {
            return;
        }

        var brush = new SolidColorBrush(color);
        foreach (TimeRange r in ranges)
        {
            var mark = new Rectangle
            {
                Width = Math.Max(1, r.Duration / duration * PreviewWidth),
                Height = canvas.Height,
                Fill = brush,
            };
            Canvas.SetLeft(mark, r.Start / duration * PreviewWidth);
            canvas.Children.Add(mark);
        }
    }

    public static Canvas Preview() => new() { Width = PreviewWidth, Height = 28, Margin = new Thickness(0, 4, 0, 8) };
}
