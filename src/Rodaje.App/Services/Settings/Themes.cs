using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.Core.Model;
using Rodaje.Packages;

namespace Rodaje.App.Services.Settings;

/// <summary>Un tema con nombre: un color por clave de la paleta (<see cref="Themes.Slots"/>), en <c>#RRGGBB</c> o <c>#AARRGGBB</c>.</summary>
public sealed class ThemeDefinition
{
    public int Version { get; set; } = Themes.CurrentVersion;

    public string Name { get; set; } = "";

    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.Ordinal);

    public ThemeDefinition Copy(string name) => new() { Name = name, Colors = new Dictionary<string, string>(Colors, StringComparer.Ordinal) };

    public uint Color(string key) => Personalization.TryParseColor(Colors.GetValueOrDefault(key), out uint argb) ? argb : Themes.Graphite.Color(key);

    public void Set(string key, uint argb) => Colors[key] = Personalization.ToHex(argb);
}

/// <summary>Los temas de serie (Grafito, Claro, Alto contraste), la lectura y escritura del <c>.rodaje-tema</c> y el contraste.</summary>
public static class Themes
{
    /// <summary>Versión del <c>.rodaje-tema</c>: la 2 añade el fondo de las pistas de video y de audio (<see cref="TrackKeys"/>).</summary>
    public const int CurrentVersion = 2;
    public const string Extension = ".rodaje-tema";
    public const int MaxNameLength = 40;
    public const string GraphiteName = "Grafito";
    public const string LightName = "Claro";
    public const string HighContrastName = "Alto contraste";

    /// <summary>Las claves editables, por grupo y con su nombre visible; la línea de tiempo y el cursor usan el fondo y el acento.</summary>
    public static IReadOnlyList<(string Key, string Group, string Label)> Slots { get; } =
    [
        ("BackgroundBrush", "Ventana", "Fondo de la ventana y de la línea de tiempo"),
        ("PanelBrush", "Paneles", "Paneles"),
        ("BarBrush", "Paneles", "Barras y cabeceras"),
        ("ControlBrush", "Paneles", "Controles"),
        ("ControlHoverBrush", "Paneles", "Controles al pasar el ratón"),
        ("ControlPressedBrush", "Paneles", "Controles pulsados"),
        ("StrokeBrush", "Bordes", "Separadores"),
        ("ControlBorderBrush", "Bordes", "Bordes de controles"),
        ("TextBrush", "Texto", "Texto principal"),
        ("TextMutedBrush", "Texto", "Texto secundario"),
        ("TextDisabledBrush", "Texto", "Texto desactivado"),
        ("AccentBrush", "Acento", "Acento (botón principal y cursor)"),
        ("AccentForegroundBrush", "Acento", "Texto sobre el acento"),
        ("AccentSubtleBrush", "Acento", "Selección"),
        ("VideoTrackBrush", "Línea de tiempo", "Fondo de las pistas de video"),
        ("AudioTrackBrush", "Línea de tiempo", "Fondo de las pistas de audio"),
        ("VideoClipBrush", "Línea de tiempo", "Clip de video"),
        ("VideoClipEdgeBrush", "Línea de tiempo", "Borde del clip de video"),
        ("AudioClipBrush", "Línea de tiempo", "Clip de audio"),
        ("AudioClipEdgeBrush", "Línea de tiempo", "Borde del clip de audio"),
        ("TextClipBrush", "Línea de tiempo", "Clip de título"),
        ("TextClipEdgeBrush", "Línea de tiempo", "Borde del clip de título"),
        ("KeyframeBrush", "Línea de tiempo", "Marcadores y fotogramas clave"),
        ("ViewerSurroundBrush", "Visor", "Fondo del visor"),
    ];

    /// <summary>Las claves que no existían en la versión 1 del <c>.rodaje-tema</c>: al migrar toman el color de los paneles.</summary>
    public static IReadOnlyList<string> TrackKeys { get; } = ["VideoTrackBrush", "AudioTrackBrush"];

    /// <summary>Parejas que deben cumplir AA: texto sobre fondo (4,5:1) y bordes o acento sobre su fondo (3:1).</summary>
    public static IReadOnlyList<(string Foreground, string Background, double Minimum)> ContrastPairs { get; } =
    [
        ("TextBrush", "PanelBrush", Personalization.TextContrast),
        ("TextBrush", "BackgroundBrush", Personalization.TextContrast),
        ("TextBrush", "ControlBrush", Personalization.TextContrast),
        ("TextBrush", "VideoTrackBrush", Personalization.TextContrast),
        ("TextBrush", "AudioTrackBrush", Personalization.TextContrast),
        ("TextMutedBrush", "PanelBrush", Personalization.UiContrast),
        ("AccentForegroundBrush", "AccentBrush", Personalization.TextContrast),
        ("AccentBrush", "PanelBrush", Personalization.UiContrast),
        ("ControlBorderBrush", "PanelBrush", Personalization.UiContrast),
        ("VideoClipEdgeBrush", "VideoClipBrush", Personalization.UiContrast),
        ("AudioClipEdgeBrush", "AudioClipBrush", Personalization.UiContrast),
        ("TextClipEdgeBrush", "TextClipBrush", Personalization.UiContrast),
    ];

    /// <summary>La paleta grafito de <c>Palette.xaml</c> (D-027).</summary>
    public static ThemeDefinition Graphite { get; } = new()
    {
        Name = GraphiteName,
        Colors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BackgroundBrush"] = "#101010", ["PanelBrush"] = "#1D1D1D", ["BarBrush"] = "#262626", ["ControlBrush"] = "#2C2C2C",
            ["ControlHoverBrush"] = "#3A3A3A", ["ControlPressedBrush"] = "#4A4A4A", ["StrokeBrush"] = "#3A3A3A", ["ControlBorderBrush"] = "#737373",
            ["TextBrush"] = "#ECECEC", ["TextMutedBrush"] = "#B0B0B0", ["TextDisabledBrush"] = "#7A7A7A",
            ["AccentBrush"] = "#E8833A", ["AccentForegroundBrush"] = "#141414", ["AccentSubtleBrush"] = "#4DE8833A",
            ["VideoClipBrush"] = "#33587C", ["VideoClipEdgeBrush"] = "#7FB0DE", ["AudioClipBrush"] = "#365A31", ["AudioClipEdgeBrush"] = "#9BCB83",
            ["TextClipBrush"] = "#574573", ["TextClipEdgeBrush"] = "#B7A2E3", ["KeyframeBrush"] = "#F2D16B", ["ViewerSurroundBrush"] = "#242424",
            ["VideoTrackBrush"] = "#1D1D1D", ["AudioTrackBrush"] = "#1D1D1D",
        },
    };

    /// <summary>Los grises del grafito invertidos y el naranja oscurecido hasta 3:1 sobre el panel claro; los clips no cambian.</summary>
    public static ThemeDefinition Light { get; } = BuildLight();

    public static ThemeDefinition HighContrast { get; } = new()
    {
        Name = HighContrastName,
        Colors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BackgroundBrush"] = "#000000", ["PanelBrush"] = "#000000", ["BarBrush"] = "#000000", ["ControlBrush"] = "#000000",
            ["ControlHoverBrush"] = "#1A1A1A", ["ControlPressedBrush"] = "#333333", ["StrokeBrush"] = "#FFFFFF", ["ControlBorderBrush"] = "#FFFFFF",
            ["TextBrush"] = "#FFFFFF", ["TextMutedBrush"] = "#FFFFFF", ["TextDisabledBrush"] = "#A6A6A6",
            ["AccentBrush"] = "#1AEBFF", ["AccentForegroundBrush"] = "#000000", ["AccentSubtleBrush"] = "#4D1AEBFF",
            ["VideoClipBrush"] = "#003A6B", ["VideoClipEdgeBrush"] = "#7FD4FF", ["AudioClipBrush"] = "#0A4F0A", ["AudioClipEdgeBrush"] = "#9BFF83",
            ["TextClipBrush"] = "#4B2E7A", ["TextClipEdgeBrush"] = "#D6B8FF", ["KeyframeBrush"] = "#FFFF00", ["ViewerSurroundBrush"] = "#000000",
            ["VideoTrackBrush"] = "#000000", ["AudioTrackBrush"] = "#000000",
        },
    };

    public static IReadOnlyList<ThemeDefinition> BuiltIn { get; } = [Graphite, Light, HighContrast];

    public static bool IsBuiltIn(string? name) => BuiltIn.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Un tema es claro si su fondo lo es: entonces los controles de WinUI se visten con su tema claro al aplicarlo.</summary>
    public static bool IsLight(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return Personalization.Luminance(theme.Color("BackgroundBrush")) > 0.5;
    }

    /// <summary>El tema de los ajustes: el de serie o propio con ese nombre; con <see cref="Personalization.FollowWindowsTheme"/>, Claro u Grafito según Windows.</summary>
    public static ThemeDefinition Resolve(string? setting, IEnumerable<ThemeDefinition> own, bool windowsIsLight, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(own);
        warning = null;
        if (setting == Personalization.FollowWindowsTheme)
        {
            return windowsIsLight ? Light : Graphite;
        }

        ThemeDefinition? found = BuiltIn.Concat(own).FirstOrDefault(t => string.Equals(t.Name, setting, StringComparison.OrdinalIgnoreCase));
        if (found is null && !string.IsNullOrWhiteSpace(setting) && setting != GraphiteName)
        {
            warning = $"No se encontró el tema «{setting}»; se usa Grafito.";
        }

        return found ?? Graphite;
    }

    /// <summary>El tema con otro acento: el color, su texto (negro o blanco) y la selección translúcida.</summary>
    public static ThemeDefinition WithAccent(ThemeDefinition theme, uint accent)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ThemeDefinition result = theme.Copy(theme.Name);
        result.Set("AccentBrush", Personalization.WithAlpha(accent, 0xFF));
        result.Set("AccentForegroundBrush", Personalization.ForegroundOn(accent));
        result.Set("AccentSubtleBrush", Personalization.WithAlpha(accent, 0x4D));
        return result;
    }

    /// <summary>
    /// El acento que llega a 3:1 sobre los paneles del tema (si el elegido no llega, se aleja del fondo) y si hubo que
    /// tocarlo, para avisar.
    /// </summary>
    public static (uint Accent, bool Adjusted) FitAccent(ThemeDefinition theme, uint accent)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return Personalization.EnsureContrast(Personalization.WithAlpha(accent, 0xFF), theme.Color("PanelBrush"), Personalization.UiContrast);
    }

    /// <summary>Las parejas que no cumplen AA, en español: «Texto principal» sobre «Paneles»: 2,1:1 (mínimo 4,5:1).</summary>
    public static IReadOnlyList<string> ContrastWarnings(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var warnings = new List<string>();
        foreach ((string fg, string bg, double min) in ContrastPairs)
        {
            double ratio = Personalization.Contrast(theme.Color(fg), theme.Color(bg));
            if (ratio < min)
            {
                warnings.Add($"«{Label(fg)}» sobre «{Label(bg)}»: {Decimal(ratio)}:1 (mínimo {Decimal(min)}:1)");
            }
        }

        return warnings;
    }

    public static string Label(string key) => Slots.FirstOrDefault(s => s.Key == key).Label ?? key;

    /// <summary>El nombre limpio (sin controles ni bidi, recortado), o <c>null</c> si queda vacío.</summary>
    public static string? CleanName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        string clean = SafeText.Truncate(SafeText.Clean(name).Trim(), MaxNameLength).Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>El archivo del tema en la carpeta de temas: el nombre sin caracteres prohibidos ni nombres de dispositivo.</summary>
    public static string FileName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        safe = safe.TrimEnd('.', ' ');
        return WindowsFileNames.AvoidReserved(safe.Length == 0 ? "tema" : safe) + Extension;
    }

    /// <summary>
    /// Corrige un tema leído de un archivo: nombre limpio, solo claves conocidas y colores válidos (lo que falte lo toma de
    /// Grafito) y lo sube a <see cref="CurrentVersion"/>: en un tema de la versión 1 las pistas (<see cref="TrackKeys"/>)
    /// toman el color de los paneles, que es lo que se veía. Devuelve <c>null</c> si no tiene nombre o la versión es de un
    /// Rodaje más nuevo.
    /// </summary>
    public static ThemeDefinition? Normalize(ThemeDefinition? theme)
    {
        if (theme is null || theme.Version < 1 || theme.Version > CurrentVersion || CleanName(theme.Name) is not { } name)
        {
            return null;
        }

        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, _, _) in Slots)
        {
            colors[key] = Personalization.TryParseColor(theme.Colors?.GetValueOrDefault(key), out uint argb)
                ? Personalization.ToHex(argb)
                : Missing(key, theme.Version, colors);
        }

        return new ThemeDefinition { Version = CurrentVersion, Name = name, Colors = colors };
    }

    // Las claves van en el orden de Slots: los paneles ya están resueltos cuando llegan las pistas.
    private static string Missing(string key, int version, Dictionary<string, string> resolved) =>
        version < 2 && TrackKeys.Contains(key) && resolved.TryGetValue("PanelBrush", out string? panel) ? panel : Graphite.Colors[key];

    public static string Serialize(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return JsonSerializer.Serialize(theme, ThemeJsonContext.Default.ThemeDefinition);
    }

    /// <summary>El tema del JSON, ya corregido, o <c>null</c> si está roto.</summary>
    public static ThemeDefinition? Parse(string json)
    {
        try
        {
            return Normalize(JsonSerializer.Deserialize(json, ThemeJsonContext.Default.ThemeDefinition));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // La app corre con globalización invariante: la coma decimal se pone a mano.
    private static string Decimal(double value) => value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');

    private static ThemeDefinition BuildLight()
    {
        ThemeDefinition light = Graphite.Copy(LightName);
        foreach ((string key, _, _) in Slots)
        {
            uint color = Graphite.Color(key);
            if (Personalization.IsAchromatic(color))
            {
                light.Set(key, Personalization.Invert(color));
            }
        }

        (uint accent, _) = FitAccent(light, Graphite.Color("AccentBrush"));
        return WithAccent(light, accent);
    }
}

/// <summary>Los temas propios en <c>%LOCALAPPDATA%\Rodaje\temas\&lt;nombre&gt;.rodaje-tema</c>.</summary>
public sealed class ThemeStore
{
    public ThemeStore(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        Folder = Path.GetFullPath(folder);
    }

    public static string DefaultFolder => Path.Combine(AppDataPaths.Root, "temas");

    public string Folder { get; }

    public List<ThemeDefinition> Own { get; } = [];

    /// <summary>Lee la carpeta; los archivos rotos o con nombre de serie se saltan y se avisan en español.</summary>
    public IReadOnlyList<string> Load()
    {
        Own.Clear();
        var warnings = new List<string>();
        if (!Directory.Exists(Folder))
        {
            return warnings;
        }

        foreach (string file in Directory.EnumerateFiles(Folder, "*" + Themes.Extension).Order(StringComparer.OrdinalIgnoreCase))
        {
            ThemeDefinition? theme;
            try
            {
                theme = Themes.Parse(SafeText.ReadAllTextStrict(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                warnings.Add($"No se pudo leer el tema {Path.GetFileName(file)}: {ex.Message}");
                continue;
            }

            if (theme is null || Themes.IsBuiltIn(theme.Name) || Own.Any(t => string.Equals(t.Name, theme.Name, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"El tema {Path.GetFileName(file)} está dañado o repite un nombre; se ignora.");
                continue;
            }

            Own.Add(theme);
        }

        return warnings;
    }

    public ThemeDefinition? Find(string? name) => Own.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Guarda (o reemplaza) un tema propio; los de serie no se guardan.</summary>
    public void Save(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (Themes.IsBuiltIn(theme.Name))
        {
            throw new InvalidOperationException("Los temas de serie no se editan: duplícalo.");
        }

        Directory.CreateDirectory(Folder);
        AppDataPaths.WriteAllTextAtomic(Path.Combine(Folder, Themes.FileName(theme.Name)), Themes.Serialize(theme));
        Own.RemoveAll(t => string.Equals(t.Name, theme.Name, StringComparison.OrdinalIgnoreCase));
        Own.Add(theme);
    }

    public bool Delete(string name)
    {
        string path = Path.Combine(Folder, Themes.FileName(name));
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Own.RemoveAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>Un nombre libre a partir del pedido: «Grafito (copia)», «Grafito (copia 2)»…</summary>
    public string FreeName(string baseName)
    {
        string wanted = Themes.CleanName(baseName) ?? "Tema";
        string candidate = wanted;
        for (int n = 2; Themes.IsBuiltIn(candidate) || Find(candidate) is not null; n++)
        {
            string suffix = n == 2 ? " (copia)" : $" (copia {n})";
            candidate = SafeText.Truncate(wanted, Themes.MaxNameLength - suffix.Length) + suffix;
        }

        return candidate;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ThemeDefinition))]
internal sealed partial class ThemeJsonContext : JsonSerializerContext;
