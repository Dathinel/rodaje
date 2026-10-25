using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.App.Services.Shortcuts;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Packages;

namespace Rodaje.App.Services.Settings;

/// <summary>Lo que viaja en un <c>.rodaje-perfil</c>: la personalización, los espacios propios, los atajos y el ratón, y el tema activo si es propio.</summary>
internal sealed class PersonalProfileFile
{
    public int Version { get; set; } = PersonalProfile.CurrentVersion;

    public string? Theme { get; set; }

    public ThemeDefinition? ActiveTheme { get; set; }

    public string? AccentColor { get; set; }

    public int UiScale { get; set; } = Personalization.DefaultScale;

    public TimelineFontSize TimelineFontSize { get; set; }

    public string? Workspace { get; set; }

    public List<WorkspaceProfile> Workspaces { get; set; } = [];

    public int DefaultVideoTracks { get; set; } = NewProjectTracks.Default.Video;

    public int DefaultAudioTracks { get; set; } = NewProjectTracks.Default.Audio;

    public string? DefaultProjectAspect { get; set; }

    public int DefaultProjectShortSide { get; set; } = 1080;

    public Rational DefaultProjectFrameRate { get; set; } = Rational.Fps30;

    public string? DefaultProjectFolder { get; set; }

    public ShortcutPreset ShortcutPreset { get; set; }

    public ShortcutFile? Shortcuts { get; set; }
}

/// <summary>Exporta e importa el <c>.rodaje-perfil</c> (JSON con versión); lo que viene de fuera se limpia y se acota.</summary>
public static class PersonalProfile
{
    public const int CurrentVersion = 1;
    public const string Extension = ".rodaje-perfil";

    public static string Serialize(AppSettings settings, ShortcutRegistry registry, ThemeDefinition? activeOwnTheme)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(registry);
        var file = new PersonalProfileFile
        {
            Theme = settings.Theme, ActiveTheme = activeOwnTheme is { } own && !Themes.IsBuiltIn(own.Name) ? own : null,
            AccentColor = settings.AccentColor, UiScale = settings.UiScale, TimelineFontSize = settings.TimelineFontSize,
            Workspace = settings.Workspace, Workspaces = [.. settings.Workspaces],
            DefaultVideoTracks = settings.DefaultVideoTracks, DefaultAudioTracks = settings.DefaultAudioTracks,
            DefaultProjectAspect = settings.DefaultProjectAspect, DefaultProjectShortSide = settings.DefaultProjectShortSide,
            DefaultProjectFrameRate = settings.DefaultProjectFrameRate, DefaultProjectFolder = settings.DefaultProjectFolder,
            ShortcutPreset = settings.ShortcutPreset, Shortcuts = ShortcutStore.Build(registry),
        };
        return JsonSerializer.Serialize(file, ProfileJsonContext.Default.PersonalProfileFile);
    }

    public static void Export(AppSettings settings, ShortcutRegistry registry, ThemeDefinition? activeOwnTheme, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        AppDataPaths.WriteAllTextAtomic(path, Serialize(settings, registry, activeOwnTheme));
    }

    /// <summary>
    /// Aplica el perfil del archivo a los ajustes, los atajos y los temas propios (el tema activo del perfil se guarda si
    /// no existe). Devuelve <c>null</c> si fue bien o el motivo en español; en ese caso no cambia nada.
    /// </summary>
    public static string? Import(string path, AppSettings settings, ShortcutRegistry registry, ThemeStore? themes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string json;
        try
        {
            json = SafeText.ReadAllTextStrict(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return $"No se pudo leer {Path.GetFileName(path)}: {ex.Message}";
        }

        return Apply(json, settings, registry, themes) ? null : $"{Path.GetFileName(path)} no es un perfil de Rodaje o es de una versión más nueva.";
    }

    /// <summary><c>false</c> si el JSON no es un perfil válido; entonces no toca nada.</summary>
    public static bool Apply(string json, AppSettings settings, ShortcutRegistry registry, ThemeStore? themes)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(registry);
        PersonalProfileFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, ProfileJsonContext.Default.PersonalProfileFile);
        }
        catch (JsonException)
        {
            return false;
        }

        if (file is null || file.Version < 1 || file.Version > CurrentVersion)
        {
            return false;
        }

        ThemeDefinition? active = Themes.Normalize(file.ActiveTheme);
        if (active is not null && themes is not null && !Themes.IsBuiltIn(active.Name) && themes.Find(active.Name) is null)
        {
            themes.Save(active);
        }

        settings.Theme = file.Theme == Personalization.FollowWindowsTheme ? file.Theme : Themes.CleanName(file.Theme) ?? Themes.GraphiteName;
        settings.AccentColor = file.AccentColor;
        settings.UiScale = file.UiScale;
        settings.TimelineFontSize = file.TimelineFontSize;
        foreach (WorkspaceProfile workspace in file.Workspaces ?? [])
        {
            workspace.Name = Themes.CleanName(workspace.Name) ?? "";
            workspace.Id = SafeText.Clean(workspace.Id ?? "");
            workspace.InspectorTab = workspace.InspectorTab is { } tab ? SafeText.Clean(tab) : null;
            workspace.HiddenInspectorTabs = [.. (workspace.HiddenInspectorTabs ?? []).Where(t => t is not null).Select(t => SafeText.Clean(t))];
        }

        settings.Workspaces = WorkspaceProfiles.Normalize(file.Workspaces);
        settings.Workspace = file.Workspace is { } chosen ? SafeText.Clean(chosen) : null;
        settings.DefaultVideoTracks = file.DefaultVideoTracks;
        settings.DefaultAudioTracks = file.DefaultAudioTracks;
        settings.DefaultProjectAspect = file.DefaultProjectAspect ?? "16:9";
        settings.DefaultProjectShortSide = file.DefaultProjectShortSide;
        settings.DefaultProjectFrameRate = file.DefaultProjectFrameRate;
        settings.DefaultProjectFolder = file.DefaultProjectFolder;
        settings.ShortcutPreset = Enum.IsDefined(file.ShortcutPreset) ? file.ShortcutPreset : ShortcutPreset.Rodaje;
        settings.Normalize();
        if (WorkspaceProfiles.Find(settings, settings.Workspace) is { } profile)
        {
            WorkspaceProfiles.Apply(settings, profile);
        }

        if (file.Shortcuts?.Presets is not null)
        {
            ShortcutStore.Apply(registry, file.Shortcuts, replaceAll: true);
        }

        registry.Preset = settings.ShortcutPreset;
        return true;
    }

    /// <summary>«Restablecer todo»: la personalización a fábrica y fuera los cambios de teclas y ratón de todos los estilos.</summary>
    public static void ResetAll(AppSettings settings, ShortcutRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Personalization.ResetAll(settings);
        registry.Restore(new ShortcutSnapshot(
            new Dictionary<ShortcutPreset, IReadOnlyDictionary<string, IReadOnlyList<KeyChord>>>(),
            new Dictionary<ShortcutPreset, IReadOnlyDictionary<MouseContext, IReadOnlyDictionary<MouseChord, string?>>>()));
        registry.Preset = ShortcutPreset.Rodaje;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    Converters = [typeof(RationalJsonConverter), typeof(TimeJsonConverter)])]
[JsonSerializable(typeof(PersonalProfileFile))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;
