using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Rodaje.App.Theme;

/// <summary>Las claves de Palette.xaml y Typography.xaml desde el código, y la paleta en el contraste alto de Windows.</summary>
internal static class ThemeResources
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static Color Color(string key) => (Color)Application.Current.Resources[key];

    /// <summary>El color de un pincel de la paleta: sigue al contraste alto, a diferencia de las claves <c>*Color</c>.</summary>
    public static Color SolidColor(string brushKey) => ((SolidColorBrush)Application.Current.Resources[brushKey]).Color;

    public static double Size(string key) => (double)Application.Current.Resources[key];

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static CornerRadius Corner(string key) => (CornerRadius)Application.Current.Resources[key];

    public static bool IsHighContrast { get; private set; }

    /// <summary>
    /// Con el contraste alto de Windows, los pinceles de la paleta toman los colores del sistema antes de crear la ventana:
    /// los controles de WinUI ya los siguen solos, y así las vistas propias (línea de tiempo, barras, clips) no quedan en
    /// grafito con texto del sistema encima. Los colores de datos (medidores, curvas) y el gris del visor, que pinta el
    /// render, no cambian.
    /// </summary>
    public static void ApplyHighContrast(bool selfTest)
    {
        // Solo en la autoprueba, RODAJE_CONTRASTE_ALTO=1 simula un contraste alto negro sin tocar Windows (capturas).
        bool simulated = selfTest && Environment.GetEnvironmentVariable("RODAJE_CONTRASTE_ALTO") == "1";
        if (!simulated && !new AccessibilitySettings().HighContrast)
        {
            return;
        }

        IsHighContrast = true;
        var ui = simulated ? null : new UISettings();
        Color Get(UIElementType type, uint simulatedRgb) => ui?.UIElementColor(type)
            ?? Windows.UI.Color.FromArgb(0xFF, (byte)(simulatedRgb >> 16), (byte)(simulatedRgb >> 8), (byte)simulatedRgb);

        Set(Get(UIElementType.Window, 0x000000),
            "BackgroundBrush", "BarBrush", "PanelBrush", "ControlBrush", "ControlHoverBrush", "ControlPressedBrush",
            "AccentSubtleBrush", "VideoClipBrush", "TextClipBrush", "AudioClipBrush");
        Set(Get(UIElementType.WindowText, 0xFFFFFF),
            "StrokeBrush", "ControlBorderBrush", "TextBrush", "TextMutedBrush", "VideoClipEdgeBrush", "TextClipEdgeBrush", "AudioClipEdgeBrush");
        Set(Get(UIElementType.GrayText, 0xA6A6A6), "TextDisabledBrush");
        Set(Get(UIElementType.Highlight, 0x1AEBFF), "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "KeyframeBrush");
        Set(Get(UIElementType.HighlightText, 0x000000), "AccentForegroundBrush");
    }

    private static void Set(Color color, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (Application.Current.Resources[key] is SolidColorBrush brush)
            {
                brush.Color = color;
            }
        }
    }
}
