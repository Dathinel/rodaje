using Rodaje.App.Services.Shortcuts;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Services.Settings;

/// <summary>Preferencias de la persona que sobreviven entre sesiones (<c>ajustes.json</c>).</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;
    public const int MaxRecentProjects = 10;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Última carpeta de la que se importaron medios.</summary>
    public string? LastMediaFolder { get; set; }

    /// <summary>Última carpeta donde se abrió o guardó un proyecto.</summary>
    public string? LastProjectFolder { get; set; }

    public List<string> RecentProjects { get; set; } = [];

    /// <summary>Ancho de la biblioteca (con el riel de íconos), en píxeles independientes del DPI.</summary>
    public double LibraryWidth { get; set; } = 320;

    public double InspectorWidth { get; set; } = 320;

    public double TimelineHeight { get; set; } = 300;

    /// <summary>Tamaño de la ventana en píxeles independientes del DPI; 0 para el de fábrica.</summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }

    public ShortcutPreset ShortcutPreset { get; set; } = ShortcutPreset.Rodaje;

    public ProxyUsage ProxyUsage { get; set; } = ProxyUsage.Auto;

    /// <summary>Velocidad de caída de los medidores de audio, en dB por segundo.</summary>
    public double MeterDecayDbPerSecond { get; set; } = 20;

    /// <summary>El reproductor muestra los márgenes seguros de acción y de títulos.</summary>
    public bool ShowSafeAreas { get; set; }

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
        LibraryWidth = Clamp(LibraryWidth, 220, 900, 320);
        InspectorWidth = Clamp(InspectorWidth, 240, 900, 320);
        TimelineHeight = Clamp(TimelineHeight, 160, 1400, 300);
        WindowWidth = WindowWidth == 0 ? 0 : Clamp(WindowWidth, 800, 10000, 0);
        WindowHeight = WindowHeight == 0 ? 0 : Clamp(WindowHeight, 560, 10000, 0);
        MeterDecayDbPerSecond = Clamp(MeterDecayDbPerSecond, 3, 120, 20);
        RecentProjects = [.. (RecentProjects ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxRecentProjects)];
        if (!Enum.IsDefined(ShortcutPreset))
        {
            ShortcutPreset = ShortcutPreset.Rodaje;
        }

        if (!Enum.IsDefined(ProxyUsage))
        {
            ProxyUsage = ProxyUsage.Auto;
        }
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
