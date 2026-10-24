using System.Text.Json.Serialization;
using Rodaje.App.Services.Shortcuts;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Services.Settings;

/// <summary>Preferencias de la persona que sobreviven entre sesiones (<c>ajustes.json</c>).</summary>
public sealed partial class AppSettings
{
    /// <summary>
    /// 2: el panel derecho pasa a 380 px por defecto para que sus pestañas quepan con nombre. 3: los márgenes seguros se
    /// apagan una vez (se confundían con guías de recorte de la secuencia vertical). 4: el alto de la línea de tiempo pasa
    /// a guardarse como proporción de la ventana (<see cref="TimelineShare"/>).
    /// </summary>
    public const int CurrentVersion = 4;

    public const double DefaultLibraryWidth = 280;

    public const double DefaultInspectorWidth = 380;
    public const int MaxRecentProjects = 10;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Última carpeta de la que se importaron medios.</summary>
    public string? LastMediaFolder { get; set; }

    /// <summary>Última carpeta usada por un diálogo de abrir.</summary>
    public string? LastOpenFolder { get; set; }

    /// <summary>Carpeta para los medios que genera Rodaje; <c>null</c> para <c>Nombre.medios</c> junto al proyecto (D-034).</summary>
    public string? GeneratedMediaFolder { get; set; }

    /// <summary>Última carpeta donde se abrió o guardó un proyecto.</summary>
    public string? LastProjectFolder { get; set; }

    /// <summary>Última carpeta elegida al guardar un proyecto.</summary>
    public string? LastProjectSaveFolder { get; set; }

    /// <summary>Última carpeta elegida al exportar un fotograma.</summary>
    public string? LastStillExportFolder { get; set; }

    /// <summary>Carpeta para las capturas directas de fotogramas; <c>null</c> para «Capturas» junto al proyecto.</summary>
    public string? StillCaptureFolder { get; set; }

    /// <summary>Última carpeta elegida al guardar un preajuste de exportación.</summary>
    public string? LastExportPresetFolder { get; set; }

    /// <summary>Última carpeta elegida al exportar los atajos.</summary>
    public string? LastShortcutExportFolder { get; set; }

    public List<string> RecentProjects { get; set; } = [];

    /// <summary>Ancho de la biblioteca (con el riel de íconos), en píxeles independientes del DPI.</summary>
    public double LibraryWidth { get; set; } = DefaultLibraryWidth;

    public double InspectorWidth { get; set; } = DefaultInspectorWidth;

    /// <summary>Alto de la línea de tiempo como parte del alto de la ventana (<see cref="TimelineProportion"/>).</summary>
    public double TimelineShare { get; set; } = TimelineProportion.Balanced;

    /// <summary>El alto en píxeles de los ajustes de antes de la versión 4; solo se lee para pasarlo a <see cref="TimelineShare"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TimelineHeight { get; set; }

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

    /// <summary>Visor de 10 bits: automático según el monitor, siempre o nunca. Se aplica al volver a abrir el reproductor.</summary>
    public TenBitViewer TenBitViewer { get; set; } = TenBitViewer.Auto;

    /// <summary>Calidad de la IA local (Ver › Calidad de la IA): automática según el equipo, rápida o máxima (D-037).</summary>
    public Rodaje.Media.Ai.AiQuality AiQuality { get; set; } = Rodaje.Media.Ai.AiQuality.Auto;

    /// <summary>Velocidad de caída de los medidores de audio, en dB por segundo.</summary>
    public double MeterDecayDbPerSecond { get; set; } = 20;

    /// <summary>El reproductor muestra las zonas seguras elegidas en <see cref="SafeZones"/>.</summary>
    public bool ShowSafeAreas { get; set; }

    /// <summary>Las zonas del visor: <c>fabrica/clasicos</c>, una red de fábrica o la referencia de un paquete o de las propias.</summary>
    public string? SafeZones { get; set; }

    /// <summary>El panel de monitores de imagen junto al visor está abierto.</summary>
    public bool ShowScopes { get; set; }

    /// <summary>El monitor elegido (<c>Rodaje.Render.Scopes.ScopeKind</c> por su nombre); vacío, la forma de onda de la luma.</summary>
    public string? Scope { get; set; }

    /// <summary>Cómo quedan los clips al cambiar el formato del proyecto desde el reproductor.</summary>
    public Format.ClipFit FormatClipFit { get; set; } = Format.ClipFit.Fit;

    /// <summary>Secciones del inspector abiertas (true) o plegadas (false); las que faltan siguen su valor de fábrica.</summary>
    public Dictionary<string, bool> InspectorSections { get; set; } = [];

    /// <summary>Pistas de video que trae un proyecto nuevo (1–8).</summary>
    public int DefaultVideoTracks { get; set; } = NewProjectTracks.Default.Video;

    /// <summary>Pistas de audio que trae un proyecto nuevo (1–8).</summary>
    public int DefaultAudioTracks { get; set; } = NewProjectTracks.Default.Audio;

    /// <summary>Duración por defecto de las imágenes fijas al añadirlas (1–60 s).</summary>
    public double StillImageDurationSeconds { get; set; } = Rodaje.Core.Model.MediaRef.DefaultStillImageDurationSeconds;

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
        }

        if (Version < 4)
        {
            TimelineShare = TimelineProportion.FromLegacyHeight(TimelineHeight, WindowHeight);
        }

        Version = Math.Max(Version, CurrentVersion);
        TimelineHeight = null;

        LibraryWidth = Clamp(LibraryWidth, 220, 900, DefaultLibraryWidth);
        InspectorWidth = Clamp(InspectorWidth, 240, 900, DefaultInspectorWidth);
        TimelineShare = TimelineProportion.Normalize(TimelineShare);
        WindowWidth = WindowWidth == 0 ? 0 : Clamp(WindowWidth, 800, 10000, 0);
        WindowHeight = WindowHeight == 0 ? 0 : Clamp(WindowHeight, 560, 10000, 0);
        DefaultVideoTracks = Math.Clamp(DefaultVideoTracks, NewProjectTracks.Min, NewProjectTracks.Max);
        DefaultAudioTracks = Math.Clamp(DefaultAudioTracks, NewProjectTracks.Min, NewProjectTracks.Max);
        StillImageDurationSeconds = Clamp(StillImageDurationSeconds, Rodaje.Core.Model.MediaRef.MinStillImageDurationSeconds, Rodaje.Core.Model.MediaRef.MaxStillImageDurationSeconds, Rodaje.Core.Model.MediaRef.DefaultStillImageDurationSeconds);
        Rodaje.Core.Model.MediaRef.DefaultStillImageDuration = Rodaje.Core.Timing.Time.FromSeconds(StillImageDurationSeconds);
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
        Transitions ??= new TransitionPreferences();
        Transitions.Normalize();
        NormalizeWorkflow();
        if (!Enum.IsDefined(ProxyUsage))
        {
            ProxyUsage = ProxyUsage.Auto;
        }

        if (!Enum.IsDefined(PreviewQuality))
        {
            PreviewQuality = PreviewQuality.Auto;
        }

        if (!Enum.IsDefined(TenBitViewer))
        {
            TenBitViewer = TenBitViewer.Auto;
        }

        FormatClipFit = Format.ProjectFormats.Normalize(FormatClipFit);
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
        state.GapBehavior = AutoRipple
            ? TimelineGapBehavior.GlobalRipple
            : MagneticMainTrack ? TimelineGapBehavior.MagneticMainTrack : TimelineGapBehavior.LeaveGaps;
        state.Snapping = Snapping;
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
