namespace Rodaje.App.Services.Settings;

/// <summary>
/// Una distribución de los paneles con nombre: anchos de la biblioteca y del panel derecho, proporción de la línea de tiempo,
/// la pestaña del panel derecho que se abre y las que se esconden (por su nombre visible: «Texto», «Velocidad»…).
/// </summary>
public sealed class WorkspaceProfile
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public double LibraryWidth { get; set; } = AppSettings.DefaultLibraryWidth;

    public double InspectorWidth { get; set; } = AppSettings.DefaultInspectorWidth;

    public double TimelineShare { get; set; } = TimelineProportion.Balanced;

    /// <summary>El alto en píxeles de los espacios guardados antes de la versión 4 de los ajustes; se pasa a <see cref="TimelineShare"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? TimelineHeight { get; set; }

    /// <summary>Pestaña del panel derecho que se elige al aplicar; nula deja la que esté.</summary>
    public string? InspectorTab { get; set; }

    public List<string> HiddenInspectorTabs { get; set; } = [];

    public WorkspaceProfile Copy(string id, string name) => new()
    {
        Id = id, Name = name, LibraryWidth = LibraryWidth, InspectorWidth = InspectorWidth, TimelineShare = TimelineShare,
        InspectorTab = InspectorTab, HiddenInspectorTabs = [.. HiddenInspectorTabs],
    };
}

/// <summary>Los espacios de trabajo de fábrica («Edición», «Color», «Audio») y los propios (<see cref="AppSettings.Workspaces"/>).</summary>
public static class WorkspaceProfiles
{
    public const int MaxOwn = 20;

    public const string EditingId = "fabrica.edicion";

    public static IReadOnlyList<WorkspaceProfile> Factory { get; } =
    [
        new() { Id = EditingId, Name = "Edición", InspectorTab = "Propiedades" },
        new()
        {
            Id = "fabrica.color", Name = "Color", LibraryWidth = 230, InspectorWidth = 480, TimelineShare = TimelineProportion.MoreViewer, InspectorTab = "Filtros",
            HiddenInspectorTabs = ["Texto", "Velocidad", "Seguimiento", "Dinámica"],
        },
        new()
        {
            Id = "fabrica.audio", Name = "Audio", LibraryWidth = 260, InspectorWidth = 400, TimelineShare = TimelineProportion.MoreTimeline, InspectorTab = "Dinámica",
            HiddenInspectorTabs = ["Texto", "Velocidad", "Máscaras", "Seguimiento"],
        },
    ];

    public static IEnumerable<WorkspaceProfile> All(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Factory.Concat(settings.Workspaces);
    }

    public static WorkspaceProfile? Find(AppSettings settings, string? id) =>
        id is null ? null : All(settings).FirstOrDefault(p => p.Id == id);

    public static bool IsFactory(string id) => Factory.Any(p => p.Id == id);

    /// <summary>
    /// Guarda la distribución de ahora con ese nombre (si ya hay uno propio con el mismo nombre, lo reemplaza) y la deja
    /// elegida.
    /// </summary>
    public static WorkspaceProfile SaveCurrent(AppSettings settings, string name, WorkspaceProfile current)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(current);
        name = (name ?? "").Trim();
        ArgumentException.ThrowIfNullOrEmpty(name);
        int existing = settings.Workspaces.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        string id = existing >= 0 ? settings.Workspaces[existing].Id : "propio." + Guid.NewGuid().ToString("N")[..8];
        WorkspaceProfile saved = current.Copy(id, name);
        if (existing >= 0)
        {
            settings.Workspaces[existing] = saved;
        }
        else
        {
            if (settings.Workspaces.Count >= MaxOwn)
            {
                settings.Workspaces.RemoveAt(0);
            }

            settings.Workspaces.Add(saved);
        }

        settings.Workspace = id;
        return saved;
    }

    public static bool Remove(AppSettings settings, string id)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool removed = settings.Workspaces.RemoveAll(p => p.Id == id) > 0;
        if (removed && settings.Workspace == id)
        {
            settings.Workspace = EditingId;
        }

        return removed;
    }

    /// <summary>Lleva la distribución al tamaño de los paneles de los ajustes y la deja elegida.</summary>
    public static void Apply(AppSettings settings, WorkspaceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        settings.LibraryWidth = profile.LibraryWidth;
        settings.InspectorWidth = profile.InspectorWidth;
        settings.TimelineShare = profile.TimelineShare;
        settings.HiddenInspectorTabs = [.. profile.HiddenInspectorTabs];
        settings.Workspace = profile.Id;
        settings.Normalize();
    }

    /// <summary>Corrige los propios leídos de un archivo: sin id o nombre fuera, ids repetidos fuera, medidas en rango.</summary>
    public static List<WorkspaceProfile> Normalize(List<WorkspaceProfile>? own)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<WorkspaceProfile>();
        foreach (WorkspaceProfile p in own ?? [])
        {
            if (p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name) || IsFactory(p.Id) || !seen.Add(p.Id))
            {
                continue;
            }

            p.LibraryWidth = Fit(p.LibraryWidth, 220, 900, AppSettings.DefaultLibraryWidth);
            p.InspectorWidth = Fit(p.InspectorWidth, 240, 900, AppSettings.DefaultInspectorWidth);
            p.TimelineShare = p.TimelineHeight is { } legacy ? TimelineProportion.FromLegacyHeight(legacy, 0) : TimelineProportion.Normalize(p.TimelineShare);
            p.TimelineHeight = null;
            p.HiddenInspectorTabs ??= [];
            result.Add(p);
        }

        return [.. result.TakeLast(MaxOwn)];
    }

    private static double Fit(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
