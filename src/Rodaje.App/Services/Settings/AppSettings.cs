using System.Text.Json.Serialization;
using Rodaje.App.Services.Shortcuts;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Services.Settings;

/// <summary>Preferencias de la persona que sobreviven entre sesiones (<c>ajustes.json</c>).</summary>
public sealed partial class AppSettings
{
    /// <summary>
    /// 2: el panel derecho pasa a 380 px por defecto para que sus pestañas quepan con nombre. 3: los márgenes seguros se
    /// apagan una vez (Daniel los confundió con guías de recorte de la secuencia vertical).
    /// </summary>
    public const int CurrentVersion = 3;

    public const double DefaultLibraryWidth = 280;

    public const double DefaultInspectorWidth = 380;
    public const int MaxRecentProjects = 10;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Última carpeta de la que se importaron medios.</summary>
    public string? LastMediaFolder { get; set; }

    /// <summary>Última carpeta donde se abrió o guardó un proyecto.</summary>
    public string? LastProjectFolder { get; set; }

    public List<string> RecentProjects { get; set; } = [];

    /// <summary>Ancho de la biblioteca (con el riel de íconos), en píxeles independientes del DPI.</summary>
    public double LibraryWidth { get; set; } = DefaultLibraryWidth;

    public double InspectorWidth { get; set; } = DefaultInspectorWidth;

    public double TimelineHeight { get; set; } = 300;

    /// <summary>Tamaño de la ventana en píxeles independientes del DPI; 0 para el de fábrica.</summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }

    public ShortcutPreset ShortcutPreset { get; set; } = ShortcutPreset.Rodaje;

    /// <summary>La página de atajos abre en la vista avanzada (lista completa) en lugar de la esencial.</summary>
    public bool ShortcutsAdvanced { get; set; }

    public ProxyUsage ProxyUsage { get; set; } = ProxyUsage.Auto;

    /// <summary>Resolución de la vista previa; Auto baja sola al reproducir si la GPU no llega.</summary>
    public PreviewQuality PreviewQuality { get; set; } = PreviewQuality.Auto;

    /// <summary>Velocidad de caída de los medidores de audio, en dB por segundo.</summary>
    public double MeterDecayDbPerSecond { get; set; } = 20;

    /// <summary>El reproductor muestra las zonas seguras elegidas en <see cref="SafeZones"/>.</summary>
    public bool ShowSafeAreas { get; set; }

    /// <summary>Las zonas del visor: <c>fabrica/clasicos</c>, una red de fábrica o la referencia de un paquete o de las propias.</summary>
    public string? SafeZones { get; set; }

    /// <summary>Cómo quedan los clips al cambiar el formato del proyecto desde el reproductor.</summary>
    public Format.ClipFit FormatClipFit { get; set; } = Format.ClipFit.Fit;

    /// <summary>Secciones del inspector abiertas (true) o plegadas (false); las que faltan siguen su valor de fábrica.</summary>
    public Dictionary<string, bool> InspectorSections { get; set; } = [];

    /// <summary>Pistas de video que trae un proyecto nuevo (1–8).</summary>
    public int DefaultVideoTracks { get; set; } = NewProjectTracks.Default.Video;

    /// <summary>Pistas de audio que trae un proyecto nuevo (1–8).</summary>
    public int DefaultAudioTracks { get; set; } = NewProjectTracks.Default.Audio;

    /// <summary>
    /// Números de las pistas de video (1 = «Video 1») de las que sale el audio de un medio a su propia pista al
    /// importarlo o soltarlo; <c>null</c> para todas (lo de siempre).
    /// </summary>
    public List<int>? LinkedAudioVideoTracks { get; set; }

    /// <summary>Interruptores y zoom de la línea de tiempo.</summary>
    public TimelinePreferences Timeline { get; set; } = new();

    /// <summary>Las pistas de un proyecto nuevo según los ajustes.</summary>
    [JsonIgnore]
    public NewProjectTracks NewProjectTracks => new NewProjectTracks(DefaultVideoTracks, DefaultAudioTracks).Clamped();

    /// <summary>De qué pistas de video sale el audio según los ajustes.</summary>
    [JsonIgnore]
    public LinkedAudioTracks LinkedAudioTracks => LinkedAudioTracks.FromSetting(LinkedAudioVideoTracks);

    /// <summary>Pone el proyecto el primero de los recientes, sin repetirlo y con un máximo.</summary>
    public void AddRecentProject(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > MaxRecentProjects)
        {
            RecentProjects.RemoveRange(MaxRecentProjects, RecentProjects.Count - MaxRecentProjects);
        }
    }

    /// <summary>Corrige lo que no tenga sentido (un archivo editado a mano, o de otra pantalla).</summary>
    public void Normalize()
    {
        if (Version < 2)
        {
            InspectorWidth = Math.Max(InspectorWidth, DefaultInspectorWidth);
        }

        if (Version < 3)
        {
            ShowSafeAreas = false;
            Version = CurrentVersion;
        }

        LibraryWidth = Clamp(LibraryWidth, 220, 900, DefaultLibraryWidth);
        InspectorWidth = Clamp(InspectorWidth, 240, 900, DefaultInspectorWidth);
        TimelineHeight = Clamp(TimelineHeight, 160, 1400, 300);
        WindowWidth = WindowWidth == 0 ? 0 : Clamp(WindowWidth, 800, 10000, 0);
        WindowHeight = WindowHeight == 0 ? 0 : Clamp(WindowHeight, 560, 10000, 0);
        DefaultVideoTracks = Math.Clamp(DefaultVideoTracks, NewProjectTracks.Min, NewProjectTracks.Max);
        DefaultAudioTracks = Math.Clamp(DefaultAudioTracks, NewProjectTracks.Min, NewProjectTracks.Max);
        LinkedAudioVideoTracks = LinkedAudioTracks.ToSetting();
        MeterDecayDbPerSecond = Clamp(MeterDecayDbPerSecond, 3, 120, 20);
        RecentProjects = [.. (RecentProjects ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxRecentProjects)];
        if (!Enum.IsDefined(ShortcutPreset))
        {
            ShortcutPreset = ShortcutPreset.Rodaje;
        }

        InspectorSections ??= [];
        Export ??= new ExportPreferences();
        Export.Normalize();
        Timeline ??= new TimelinePreferences();
        Timeline.Normalize();
        if (!Enum.IsDefined(ProxyUsage))
        {
            ProxyUsage = ProxyUsage.Auto;
        }

        if (!Enum.IsDefined(PreviewQuality))
        {
            PreviewQuality = PreviewQuality.Auto;
        }
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>Lo que se recuerda de la barra de la línea de tiempo (<see cref="TimelineState"/>), salvo la herramienta.</summary>
public sealed class TimelinePreferences
{
    public bool MagneticMainTrack { get; set; } = true;

    public bool Snapping { get; set; } = true;

    public bool AutoRipple { get; set; }

    public RippleMode RippleMode { get; set; } = RippleMode.AffectedTracks;

    public bool AutoCrossfade { get; set; }

    public bool LockEnvelopes { get; set; }

    public bool IgnoreGroups { get; set; }

    public double PixelsPerSecond { get; set; } = TimelineState.DefaultPixelsPerSecond;

    public void ApplyTo(TimelineState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.MagneticMainTrack = MagneticMainTrack;
        state.Snapping = Snapping;
        state.AutoRipple = AutoRipple;
        state.RippleMode = RippleMode;
        state.AutoCrossfade = AutoCrossfade;
        state.LockEnvelopes = LockEnvelopes;
        state.IgnoreGroups = IgnoreGroups;
        state.PixelsPerSecond = PixelsPerSecond;
    }

    public void CopyFrom(TimelineState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        MagneticMainTrack = state.MagneticMainTrack;
        Snapping = state.Snapping;
        AutoRipple = state.AutoRipple;
        RippleMode = state.RippleMode;
        AutoCrossfade = state.AutoCrossfade;
        LockEnvelopes = state.LockEnvelopes;
        IgnoreGroups = state.IgnoreGroups;
        PixelsPerSecond = state.PixelsPerSecond;
    }

    public void Normalize()
    {
        if (!Enum.IsDefined(RippleMode))
        {
            RippleMode = RippleMode.AffectedTracks;
        }

        PixelsPerSecond = double.IsFinite(PixelsPerSecond)
            ? Math.Clamp(PixelsPerSecond, TimelineState.MinPixelsPerSecond, TimelineState.MaxPixelsPerSecond)
            : TimelineState.DefaultPixelsPerSecond;
    }
}