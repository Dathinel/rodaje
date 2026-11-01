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
    public double StillImageDurationSeconds { get => Settings.StillImageDurationSeconds; set { Settings.StillImageDurationSeconds = Math.Clamp(value, Rodaje.Core.Model.MediaRef.MinStillImageDurationSeconds, Rodaje.Core.Model.MediaRef.MaxStillImageDurationSeconds); Rodaje.Core.Model.MediaRef.DefaultStillImageDuration = Rodaje.Core.Timing.Time.FromSeconds(Settings.StillImageDurationSeconds); } }
    public bool GenerateProxies { get => Settings.AutoProxies; set => Settings.AutoProxies = value; }
    public int AutosaveMinutes { get => Settings.AutosaveMinutes; set => Settings.AutosaveMinutes = Rodaje.Core.Serialization.AutosaveSchedule.ClampMinutes(value); }
    public string GeneratedMediaFolder { get => Settings.GeneratedMediaFolder ?? ""; set => Settings.GeneratedMediaFolder = string.IsNullOrWhiteSpace(value) ? null : value; }
    public string StillCaptureFolder { get => Settings.StillCaptureFolder ?? ""; set => Settings.StillCaptureFolder = string.IsNullOrWhiteSpace(value) ? null : value; }
    public IReadOnlyList<int> LinkedAudioVideoTracks => Settings.LinkedAudioTracks.IsAll
        ? Enumerable.Range(1, NewProjectTracks.Max).ToArray()
        : Settings.LinkedAudioTracks.ToSetting() ?? [];
    public ProxyUsage ProxyUsage { get => Settings.ProxyUsage; set => Settings.ProxyUsage = value; }
    public PreviewQuality PreviewQuality { get => Settings.PreviewQuality; set => Settings.PreviewQuality = value; }
    public TenBitViewer TenBitViewer { get => Settings.TenBitViewer; set => Settings.TenBitViewer = value; }
    public HdrViewer HdrViewer { get => Settings.HdrViewer; set => Settings.HdrViewer = value; }
    public ShortcutPreset ShortcutPreset { get => Settings.ShortcutPreset; set => Settings.ShortcutPreset = value; }

    public void SetLinkedAudioVideoTracks(IEnumerable<int> videoTracks)
    {
        ArgumentNullException.ThrowIfNull(videoTracks);
        int[] selected = [.. videoTracks.Where(track => track is >= 1 and <= NewProjectTracks.Max).Distinct().Order()];
        Settings.LinkedAudioVideoTracks = selected.Length == NewProjectTracks.Max ? null : selected.ToList();
        Settings.Normalize();
    }
}
