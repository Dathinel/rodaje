using Rodaje.App.Services.Format;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Settings;

/// <summary>Tamaño del texto de la línea de tiempo (nombres de clips y de pistas, regla).</summary>
public enum TimelineFontSize
{
    Small,
    Normal,
    Large,
}

/// <summary>Medidas y cadencia de la secuencia de un proyecto nuevo.</summary>
public readonly record struct NewProjectFormat(int Width, int Height, Rational FrameRate);

/// <summary>
/// La parte sin WinUI de la personalización: colores como ARGB (<c>0xAARRGGBB</c>), contraste WCAG, escala de la
/// interfaz, acentos de la paleta y el formato de los proyectos nuevos. La interfaz solo traduce a pinceles.
/// </summary>
public static class Personalization
{
    /// <summary>Valor de <see cref="AppSettings.Theme"/> que elige Grafito o Claro según el modo de Windows.</summary>
    public const string FollowWindowsTheme = "windows";

    public const int DefaultScale = 100;

    /// <summary>Escalas de la interfaz, en por ciento.</summary>
    public static IReadOnlyList<int> Scales { get; } = [90, 100, 110, 125];

    /// <summary>Contraste mínimo AA del texto normal.</summary>
    public const double TextContrast = 4.5;

    /// <summary>Contraste mínimo AA de controles, bordes y texto grande.</summary>
    public const double UiContrast = 3.0;

    public const uint White = 0xFFFFFFFF;

    /// <summary>El negro del texto sobre el acento en la paleta grafito (#141414).</summary>
    public const uint Ink = 0xFF141414;

    /// <summary>Los ocho acentos de la paleta; el primero es el naranja de fábrica.</summary>
    public static IReadOnlyList<(string Name, uint Argb)> AccentPalette { get; } =
    [
        ("Naranja", 0xFFE8833A), ("Ámbar", 0xFFF2B134), ("Verde", 0xFF7FB069), ("Turquesa", 0xFF2FB7A6),
        ("Azul", 0xFF4F9DDE), ("Violeta", 0xFF9B7FE0), ("Rosa", 0xFFE06A9B), ("Rojo", 0xFFE5534B),
    ];

    public static int NormalizeScale(int percent) => Scales.Contains(percent) ? percent : DefaultScale;

    public static double FontFactor(TimelineFontSize size) => size switch
    {
        TimelineFontSize.Small => 0.85,
        TimelineFontSize.Large => 1.2,
        _ => 1,
    };

    /// <summary>El formato de un proyecto nuevo; lo que no esté en las listas de <see cref="ProjectFormats"/> vuelve a 16:9, 1080p, 30.</summary>
    public static NewProjectFormat Format(string? aspect, int shortSide, Rational frameRate)
    {
        AspectChoice choice = ProjectFormats.Aspects.FirstOrDefault(a => !a.Custom && a.Label == aspect) ?? ProjectFormats.Aspects[0];
        int side = ProjectFormats.Resolutions.Any(r => r.ShortSide == shortSide) ? shortSide : 1080;
        Rational rate = ProjectFormats.FrameRates.Any(r => r.Rate == frameRate) ? frameRate : Rational.Fps30;
        (int width, int height) = choice.Size(side);
        return new NewProjectFormat(width, height, rate);
    }

    public static string NormalizeAspect(string? aspect) =>
        ProjectFormats.Aspects.FirstOrDefault(a => !a.Custom && a.Label == aspect)?.Label ?? ProjectFormats.Aspects[0].Label;

    /// <summary>Una carpeta solo vale si es una ruta completa (un archivo externo no puede apuntar a una relativa).</summary>
    public static string? NormalizeFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && folder.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(folder) ? folder.Trim() : null;

    /// <summary><c>#RRGGBB</c> o <c>#AARRGGBB</c>, con o sin almohadilla.</summary>
    public static bool TryParseColor(string? text, out uint argb)
    {
        argb = 0;
        string hex = (text ?? "").Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint value))
        {
            return false;
        }

        argb = hex.Length == 6 ? 0xFF000000 | value : value;
        return true;
    }

    public static string ToHex(uint argb) => (argb >> 24) == 0xFF ? $"#{argb & 0xFFFFFF:X6}" : $"#{argb:X8}";

    public static byte Alpha(uint argb) => (byte)(argb >> 24);

    public static byte Red(uint argb) => (byte)(argb >> 16);

    public static byte Green(uint argb) => (byte)(argb >> 8);

    public static byte Blue(uint argb) => (byte)argb;

    public static uint FromArgb(byte a, byte r, byte g, byte b) => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;

    public static uint WithAlpha(uint argb, byte alpha) => (argb & 0x00FFFFFF) | ((uint)alpha << 24);

    /// <summary>Luminancia relativa WCAG (0 negro, 1 blanco); el alfa no cuenta.</summary>
    public static double Luminance(uint argb)
    {
        static double Channel(byte c)
        {
            double v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(Red(argb))) + (0.7152 * Channel(Green(argb))) + (0.0722 * Channel(Blue(argb)));
    }

    /// <summary>Relación de contraste WCAG, de 1 a 21.</summary>
    public static double Contrast(uint a, uint b)
    {
        double la = Luminance(a) + 0.05;
        double lb = Luminance(b) + 0.05;
        return la > lb ? la / lb : lb / la;
    }

    /// <summary>Un gris: los tres canales casi iguales.</summary>
    public static bool IsAchromatic(uint argb)
    {
        int max = Math.Max(Red(argb), Math.Max(Green(argb), Blue(argb)));
        int min = Math.Min(Red(argb), Math.Min(Green(argb), Blue(argb)));
        return max - min <= 8;
    }

    /// <summary>El gris opuesto (claro ↔ oscuro) con el mismo alfa.</summary>
    public static uint Invert(uint argb) => FromArgb(Alpha(argb), (byte)(255 - Red(argb)), (byte)(255 - Green(argb)), (byte)(255 - Blue(argb)));

    /// <summary>Mezcla hacia <paramref name="target"/>; <paramref name="amount"/> 0 deja el color, 1 da el destino. Conserva el alfa.</summary>
    public static uint Mix(uint argb, uint target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Lerp(byte from, byte to) => (byte)Math.Round(from + ((to - from) * amount));
        return FromArgb(Alpha(argb), Lerp(Red(argb), Red(target)), Lerp(Green(argb), Green(target)), Lerp(Blue(argb), Blue(target)));
    }

    public static uint Lighten(uint argb, double amount) => Mix(argb, White, amount);

    public static uint Darken(uint argb, double amount) => Mix(argb, 0xFF000000, amount);

    /// <summary>Negro o blanco, el que más contraste dé sobre el fondo.</summary>
    public static uint ForegroundOn(uint background) => Contrast(White, background) >= Contrast(Ink, background) ? White : Ink;

    /// <summary>
    /// Si <paramref name="color"/> no llega a <paramref name="minimum"/> sobre <paramref name="background"/>, lo aleja del
    /// fondo (lo oscurece sobre fondo claro, lo aclara sobre oscuro) en pasos de 2 % hasta llegar.
    /// </summary>
    public static (uint Color, bool Adjusted) EnsureContrast(uint color, uint background, double minimum)
    {
        if (Contrast(color, background) >= minimum)
        {
            return (color, false);
        }

        uint target = Luminance(background) > 0.18 ? 0xFF000000 : White;
        for (int step = 1; step <= 50; step++)
        {
            uint candidate = Mix(color, target, step * 0.02);
            if (Contrast(candidate, background) >= minimum)
            {
                return (candidate, true);
            }
        }

        return (WithAlpha(target, Alpha(color)), true);
    }

    /// <summary>Lo del tema, el acento, la escala, la fuente y los proyectos nuevos vuelve a fábrica; lo demás no se toca.</summary>
    public static void ResetAll(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Theme = Themes.GraphiteName;
        settings.AccentColor = null;
        settings.UiScale = DefaultScale;
        settings.TimelineFontSize = TimelineFontSize.Normal;
        settings.DefaultProjectAspect = ProjectFormats.Aspects[0].Label;
        settings.DefaultProjectShortSide = 1080;
        settings.DefaultProjectFrameRate = Rational.Fps30;
        settings.DefaultProjectFolder = null;
        settings.DefaultVideoTracks = NewProjectTracks.Default.Video;
        settings.DefaultAudioTracks = NewProjectTracks.Default.Audio;
        settings.Workspaces.Clear();
        settings.Workspace = null;
        settings.HiddenInspectorTabs.Clear();
        settings.LibraryWidth = AppSettings.DefaultLibraryWidth;
        settings.InspectorWidth = AppSettings.DefaultInspectorWidth;
        settings.TimelineShare = TimelineProportion.Balanced;
        settings.ShortcutPreset = Shortcuts.ShortcutPreset.Rodaje;
        settings.Normalize();
    }
}
