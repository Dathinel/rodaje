using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Services;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Views.Preferences;

/// <summary>Lectura y escritura de las preferencias persistentes editables desde la ventana de Preferencias.</summary>
public sealed class PreferencesViewModel(AppSettings settings)
{
    public AppSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    public int VideoTracks { get => Settings.DefaultVideoTracks; set => Settings.DefaultVideoTracks = Math.Clamp(value, NewProjectTracks.Min, NewProjectTracks.Max); }
    public int AudioTracks { get => Settings.DefaultAudioTracks; set => Settings.DefaultAudioTracks = Math.Clamp(value, NewProjectTracks.Min, NewProjectTracks.Max); }
    public bool GenerateProxies { get => Settings.AutoProxies; set => Settings.AutoProxies = value; }
    public string GeneratedMediaFolder { get => Settings.GeneratedMediaFolder ?? ""; set => Settings.GeneratedMediaFolder = string.IsNullOrWhiteSpace(value) ? null : value; }
    public ProxyUsage ProxyUsage { get => Settings.ProxyUsage; set => Settings.ProxyUsage = value; }
    public PreviewQuality PreviewQuality { get => Settings.PreviewQuality; set => Settings.PreviewQuality = value; }
    public ShortcutPreset ShortcutPreset { get => Settings.ShortcutPreset; set => Settings.ShortcutPreset = value; }
}
