using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Settings;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Rodaje.App.Theme;

/// <summary>El tema y el acento elegidos llevados a los pinceles sin reiniciar (D-056).</summary>
internal static partial class ThemeResources
{
    private static readonly Dictionary<SolidColorBrush, Color> Originals = [];

    private static readonly HashSet<string> Untouched = new(StringComparer.Ordinal)
    {
        "DangerBrush", "TransparentBrush", "ScrimBrush", "ShadeBrush", "SafeZoneFillBrush", "SafeZoneStrokeBrush", "SafeZoneTextBrush", "SafeZoneNoteBrush",
    };

    /// <summary>Se dispara tras aplicar un tema o acento; las vistas que derivan colores (línea de tiempo) los recalculan.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>El tema aplicado, ya con el acento propio si lo hay.</summary>
    public static ThemeDefinition Current { get; private set; } = Themes.Graphite;

    /// <summary>Factor del tamaño del texto de la línea de tiempo (<see cref="Personalization.FontFactor"/>).</summary>
    public static double TimelineFontScale { get; set; } = 1;

    /// <summary>El tema de WinUI del aplicado: con fondo claro, los pinceles de fábrica de los controles son los del tema claro.</summary>
    public static ElementTheme RequestedTheme => Themes.IsLight(Current) ? ElementTheme.Light : ElementTheme.Dark;

    /// <summary>Lleva <see cref="RequestedTheme"/> a la raíz de la ventana y a lo abierto encima (diálogos, menús).</summary>
    public static void ApplyRequestedTheme(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.RequestedTheme = RequestedTheme;
        if (root.XamlRoot is { } xamlRoot)
        {
            foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            {
                if (popup.Child is FrameworkElement child)
                {
                    child.RequestedTheme = RequestedTheme;
                }
            }
        }
    }

    public static double TimelineSize(string key) => Math.Round(Size(key) * TimelineFontScale, 1);

    /// <summary>Los temas propios de <c>%LOCALAPPDATA%\Rodaje\temas</c>.</summary>
    public static ThemeStore Store { get; } = new(ThemeStore.DefaultFolder);

    /// <summary>Al arrancar: lee los temas propios y aplica el tema, el acento y la fuente de los ajustes. Devuelve los avisos.</summary>
    public static IReadOnlyList<string> ApplyFromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var warnings = new List<string>(Store.Load());
        if (ApplyCurrent(settings) is { } warning)
        {
            warnings.Add(warning);
        }

        return warnings;
    }

    /// <summary>Aplica lo que dicen los ajustes con los temas ya leídos; avisa si el tema elegido no existe.</summary>
    public static string? ApplyCurrent(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TimelineFontScale = Personalization.FontFactor(settings.TimelineFontSize);
        bool windowsIsLight = settings.Theme == Personalization.FollowWindowsTheme && WindowsIsLight();
        ThemeDefinition theme = Themes.Resolve(settings.Theme, Store.Own, windowsIsLight, out string? warning);
        ApplyTheme(theme, settings.Accent);
        return warning;
    }

    /// <summary>Windows está en modo claro (fondo de las apps claro).</summary>
    public static bool WindowsIsLight()
    {
        Color background = new UISettings().GetColorValue(UIColorType.Background);
        return Personalization.Luminance(ToArgb(background)) > 0.5;
    }

    /// <summary>
    /// Lleva el tema a los pinceles de la paleta y a los de los controles de WinUI (ControlColors.xaml) en caliente: los
    /// grises opacos de los controles se invierten en un tema claro y la familia del naranja de fábrica toma el acento. Con
    /// el contraste alto de Windows no se toca nada: mandan los colores del sistema.
    /// </summary>
    public static void ApplyTheme(ThemeDefinition theme, uint? accent)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (IsHighContrast)
        {
            return;
        }

        if (accent is { } wanted)
        {
            theme = Themes.WithAccent(theme, Themes.FitAccent(theme, wanted).Accent);
        }

        Current = theme;
        bool light = Themes.IsLight(theme);
        uint accentColor = theme.Color("AccentBrush");
        uint hover = light ? Personalization.Darken(accentColor, 0.12) : Personalization.Lighten(accentColor, 0.18);
        uint pressed = light ? Personalization.Darken(accentColor, 0.25) : Personalization.Darken(accentColor, 0.13);
        var handled = new HashSet<string>(Themes.Slots.Select(s => s.Key), StringComparer.Ordinal);
        foreach (string key in handled)
        {
            SetBrush(key, theme.Color(key));
        }

        SetBrush("AccentHoverBrush", hover);
        SetBrush("AccentPressedBrush", pressed);
        handled.UnionWith(["AccentHoverBrush", "AccentPressedBrush"]);
        handled.UnionWith(Untouched);
        var all = AllBrushes(Application.Current.Resources, stock: false, stockLight: false).Where(b => !handled.Contains(b.Key)).ToList();
        // Por pincel y no por clave: en WinUI AccentButtonBackground es el mismo pincel que AccentFillColorDefaultBrush.
        var fixedColors = new Dictionary<SolidColorBrush, uint>();
        foreach ((string key, SolidColorBrush brush, _) in all)
        {
            if (FixedColor(key, theme, light, hover, pressed) is { } color)
            {
                fixedColors[brush] = color;
            }
        }

        foreach ((_, SolidColorBrush brush, bool stockLight) in all)
        {
            if (!Originals.TryGetValue(brush, out Color original))
            {
                original = brush.Color;
                Originals[brush] = original;
            }

            uint o = ToArgb(original);
            // Los grises del tema claro de fábrica de WinUI ya son claros: solo se invierten los pensados para oscuro.
            uint mapped = fixedColors.TryGetValue(brush, out uint fixedColor) ? fixedColor : AccentFamily(o, accentColor, hover, pressed)
                ?? (light && !stockLight && Personalization.Alpha(o) == 0xFF && Personalization.IsAchromatic(o) ? Personalization.Invert(o) : o);
            Color color = ToColor(mapped);
            if (brush.Color != color)
            {
                brush.Color = color;
            }
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static uint ToArgb(Color color) => Personalization.FromArgb(color.A, color.R, color.G, color.B);

    public static Color ToColor(uint argb) =>
        Windows.UI.Color.FromArgb(Personalization.Alpha(argb), Personalization.Red(argb), Personalization.Green(argb), Personalization.Blue(argb));

    /// <summary>El naranja de fábrica y sus variantes (al pasar, pulsado), con cualquier alfa, pasan al acento elegido.</summary>
    private static uint? AccentFamily(uint original, uint accent, uint hover, uint pressed)
    {
        uint rgb = original & 0x00FFFFFF;
        uint? target = rgb switch
        {
            0xE8833A => accent,
            0xEE9A5C => hover,
            0xC96D29 => pressed,
            _ => null,
        };
        return target is { } t ? Personalization.WithAlpha(t, Personalization.Alpha(original)) : null;
    }

    /// <summary>El fondo de página siempre; en un tema claro, el botón de acento de WinUI con el acento y su texto (AA, <see cref="Themes.WithAccent"/>).</summary>
    private static uint? FixedColor(string key, ThemeDefinition theme, bool light, uint hover, uint pressed) => key switch
    {
        "ApplicationPageBackgroundThemeBrush" => theme.Color("BackgroundBrush"),
        "AccentButtonBackground" when light => theme.Color("AccentBrush"),
        "AccentButtonBackgroundPointerOver" when light => hover,
        "AccentButtonBackgroundPressed" when light => pressed,
        "AccentButtonForeground" or "AccentButtonForegroundPointerOver" or "AccentButtonForegroundPressed" when light => theme.Color("AccentForegroundBrush"),
        _ => null,
    };

    private static void SetBrush(string key, uint argb)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush)
        {
            Color color = ToColor(argb);
            if (brush.Color != color)
            {
                brush.Color = color;
            }
        }
    }

    /// <summary>Los pinceles con su clave; <c>StockLight</c> marca los del tema claro de los recursos de fábrica de WinUI.</summary>
    private static IEnumerable<(string Key, SolidColorBrush Brush, bool StockLight)> AllBrushes(ResourceDictionary dictionary, bool stock, bool stockLight)
    {
        stock |= dictionary is XamlControlsResources;
        foreach (KeyValuePair<object, object> entry in dictionary)
        {
            if (entry.Key is string key && entry.Value is SolidColorBrush brush)
            {
                yield return (key, brush, stockLight);
            }
        }

        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
        {
            foreach (var pair in AllBrushes(merged, stock, stockLight))
            {
                yield return pair;
            }
        }

        foreach (KeyValuePair<object, object> entry in dictionary.ThemeDictionaries)
        {
            if (entry.Value is ResourceDictionary themed)
            {
                foreach (var pair in AllBrushes(themed, stock, stockLight || (stock && "Light".Equals(entry.Key))))
                {
                    yield return pair;
                }
            }
        }
    }
}
