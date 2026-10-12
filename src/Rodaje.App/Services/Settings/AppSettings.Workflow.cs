namespace Rodaje.App.Services.Settings;

/// <summary>Flujo de trabajo: proxies al importar, espacios de trabajo, carpetas vigiladas y el recorrido inicial.</summary>
public sealed partial class AppSettings
{
    public const double DefaultTimelineHeight = 300;

    /// <summary>Generar en segundo plano el proxy de los videos pesados al importarlos (<see cref="Media.AutoProxyRule"/>).</summary>
    public bool AutoProxies { get; set; } = true;

    /// <summary>Espacio de trabajo elegido (<see cref="WorkspaceProfiles"/>); nulo si no se eligió ninguno.</summary>
    public string? Workspace { get; set; }

    /// <summary>Espacios de trabajo propios, en el orden en que se guardaron.</summary>
    public List<WorkspaceProfile> Workspaces { get; set; } = [];

    /// <summary>Pestañas del panel derecho escondidas por el espacio de trabajo, por su nombre visible.</summary>
    public List<string> HiddenInspectorTabs { get; set; } = [];

    /// <summary>Carpetas cuyos medios nuevos se ofrecen en la biblioteca.</summary>
    public List<Media.WatchedFolder> WatchedFolders { get; set; } = [];

    /// <summary>Archivos de las carpetas vigiladas ya importados o descartados (los más recientes al final).</summary>
    public List<string> HandledWatchedFiles { get; set; } = [];

    private void NormalizeWorkflow()
    {
        WatchedFolders = [.. (WatchedFolders ?? []).Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Path))
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];
        HandledWatchedFiles = [.. (HandledWatchedFiles ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).TakeLast(Media.WatchedFolderScanner.MaxHandled)];
        Workspaces = WorkspaceProfiles.Normalize(Workspaces);
        HiddenInspectorTabs = [.. (HiddenInspectorTabs ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct()];
        if (Workspace is not null && WorkspaceProfiles.Find(this, Workspace) is null)
        {
            Workspace = null;
        }
    }
}
