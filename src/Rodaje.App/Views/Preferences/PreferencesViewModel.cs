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
    public bool CheckForUpdates { get => Settings.CheckForUpdates; set => Settings.CheckForUpdates = value; }
    public string GeneratedMediaFolder { get => Settings.GeneratedMediaFolder ?? ""; set => Settings.GeneratedMediaFolder = string.IsNullOrWhiteSpace(value) ? null : value; }
    public string PathMappings { get => PathMappingText.Format(Settings.PathMappings); set => Settings.PathMappings = PathMappingText.Parse(value); }
    public string StillCaptureFolder { get => Settings.StillCaptureFolder ?? ""; set => Settings.StillCaptureFolder = string.IsNullOrWhiteSpace(value) ? null : value; }
    public IReadOnlyList<int> LinkedAudioVideoTracks => Settings.LinkedAudioTracks.IsAll
        ? Enumerable.Range(1, NewProjectTracks.Max).ToArray()
        : Settings.LinkedAudioTracks.ToSetting() ?? [];
    public ProxyUsage ProxyUsage { get => Settings.ProxyUsage; set => Settings.ProxyUsage = value; }
    public PreviewQuality PreviewQuality { get => Settings.PreviewQuality; set => Settings.PreviewQuality = value; }

    /// <summary>La primera opción de «Tarjeta gráfica».</summary>
    public const string AutomaticGpu = "Automática (la más potente)";

    /// <summary>El adaptador elegido; vacío, automática.</summary>
    public string GpuAdapter { get => Settings.GpuAdapter ?? ""; set => Settings.GpuAdapter = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }

    /// <summary>
    /// Las opciones de «Tarjeta gráfica»: la automática y cada adaptador de hardware, sin repetir; el guardado va también
    /// aunque ya no esté (otra GPU, el portátil sin la externa), para no perderlo al guardar otra cosa.
    /// </summary>
    public static IReadOnlyList<string> GpuAdapterOptions(IEnumerable<string> hardware, string? saved)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        List<string> options = [AutomaticGpu, .. hardware.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (!string.IsNullOrWhiteSpace(saved) && !options.Skip(1).Contains(saved, StringComparer.OrdinalIgnoreCase))
        {
            options.Add(saved);
        }

        return options;
    }
    public TenBitViewer TenBitViewer { get => Settings.TenBitViewer; set => Settings.TenBitViewer = value; }
    public HdrViewer HdrViewer { get => Settings.HdrViewer; set => Settings.HdrViewer = value; }
    public ShortcutPreset ShortcutPreset { get => Settings.ShortcutPreset; set => Settings.ShortcutPreset = value; }
    public Services.RenderCache.RenderCacheMode RenderCacheMode { get => Settings.RenderCacheMode; set => Settings.RenderCacheMode = Enum.IsDefined(value) ? value : Services.RenderCache.RenderCacheMode.Smart; }
    public string RenderCacheFolder { get => Settings.RenderCacheFolder ?? ""; set => Settings.RenderCacheFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }

    /// <summary>Tope en GB; NaN o menos de 1, el automático.</summary>
    public double RenderCacheCapGb { get => Settings.RenderCacheCapGb ?? double.NaN; set => Settings.RenderCacheCapGb = double.IsFinite(value) && value >= 1 ? Math.Min(value, 100_000) : null; }

    /// <summary>Tope de los originales de la sala en GB; NaN, sin tope.</summary>
    public double CollabOriginalCapGb { get => Settings.CollabOriginalCapGb ?? double.NaN; set => Settings.CollabOriginalCapGb = double.IsFinite(value) && value >= 0.1 ? Math.Min(value, 100_000) : null; }

    /// <summary>Dirección del relé propio; vacía, los relés públicos de n0.</summary>
    public string CollabRelayUrl { get => Settings.CollabRelayUrl ?? ""; set => Settings.CollabRelayUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }

    /// <summary>«Solo por relé»: la otra persona no ve la IP de este equipo.</summary>
    public bool CollabRelayOnly { get => Settings.CollabRelayOnly; set => Settings.CollabRelayOnly = value; }

    /// <summary>Nombre con el que entrar en las salas; vacío, el usuario de Windows.</summary>
    public string CollabAlias { get => Settings.CollabAlias ?? ""; set => Settings.CollabAlias = Services.Collab.CollabNames.NormalizeAlias(value); }

    /// <summary>Carpeta por defecto de las copias de sala; vacía, «Documentos\Rodaje\Salas».</summary>
    public string CollabRoomsFolder { get => Settings.CollabRoomsFolder ?? ""; set => Settings.CollabRoomsFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }

    public void SetLinkedAudioVideoTracks(IEnumerable<int> videoTracks)
    {
        ArgumentNullException.ThrowIfNull(videoTracks);
        int[] selected = [.. videoTracks.Where(track => track is >= 1 and <= NewProjectTracks.Max).Distinct().Order()];
        Settings.LinkedAudioVideoTracks = selected.Length == NewProjectTracks.Max ? null : selected.ToList();
        Settings.Normalize();
    }
}
