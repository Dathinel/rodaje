using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.App.Services.Format;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Settings;

/// <summary>Personalización: tema, acento, escala, fuente de la línea de tiempo y el formato de los proyectos nuevos (D-056).</summary>
public sealed partial class AppSettings
{
    /// <summary>Tema por su nombre (de serie o propio, <see cref="Themes"/>), o <see cref="Personalization.FollowWindowsTheme"/>.</summary>
    public string Theme { get; set; } = Themes.GraphiteName;

    /// <summary>Acento propio en <c>#RRGGBB</c>; nulo, el del tema.</summary>
    public string? AccentColor { get; set; }

    /// <summary>Escala de la interfaz en por ciento (<see cref="Personalization.Scales"/>).</summary>
    public int UiScale { get; set; } = Personalization.DefaultScale;

    public TimelineFontSize TimelineFontSize { get; set; } = TimelineFontSize.Normal;

    /// <summary>Proporción de un proyecto nuevo, por su etiqueta en <see cref="ProjectFormats.Aspects"/>.</summary>
    public string DefaultProjectAspect { get; set; } = "16:9";

    /// <summary>Lado corto de un proyecto nuevo (<see cref="ProjectFormats.Resolutions"/>).</summary>
    public int DefaultProjectShortSide { get; set; } = 1080;

    public Rational DefaultProjectFrameRate { get; set; } = Rational.Fps30;

    /// <summary>Carpeta que proponen los diálogos de guardar proyecto; nula, la última usada.</summary>
    public string? DefaultProjectFolder { get; set; }

    [JsonIgnore]
    public NewProjectFormat NewProjectFormat => Personalization.Format(DefaultProjectAspect, DefaultProjectShortSide, DefaultProjectFrameRate);

    /// <summary>El acento de los ajustes como ARGB, o nulo si se usa el del tema.</summary>
    [JsonIgnore]
    public uint? Accent => Personalization.TryParseColor(AccentColor, out uint argb) ? Personalization.WithAlpha(argb, 0xFF) : null;

    /// <summary>Una copia independiente (por el mismo JSON con que se guarda): Preferencias trabaja sobre ella hasta Guardar.</summary>
    public AppSettings Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings), SettingsJsonContext.Default.AppSettings)
        ?? throw new InvalidOperationException("No se pudieron copiar los ajustes.");

    private void NormalizePersonalization()
    {
        Theme = Themes.CleanName(Theme) is { } name ? name : Themes.GraphiteName;
        AccentColor = Accent is { } accent ? Personalization.ToHex(accent) : null;
        UiScale = Personalization.NormalizeScale(UiScale);
        if (!Enum.IsDefined(TimelineFontSize))
        {
            TimelineFontSize = TimelineFontSize.Normal;
        }

        NewProjectFormat format = NewProjectFormat;
        DefaultProjectAspect = Personalization.NormalizeAspect(DefaultProjectAspect);
        DefaultProjectShortSide = Math.Min(format.Width, format.Height);
        DefaultProjectFrameRate = format.FrameRate;
        DefaultProjectFolder = Personalization.NormalizeFolder(DefaultProjectFolder);
    }
}
