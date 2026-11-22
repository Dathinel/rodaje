namespace Rodaje.App.Services.Settings;

/// <summary>Flujo de trabajo: proxies al importar, espacios de trabajo, carpetas vigiladas y el recorrido inicial.</summary>
public sealed partial class AppSettings
{
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

    /// <summary>Ya se vio (o se saltó) el recorrido del primer arranque.</summary>
    public bool TutorialSeen { get; set; }

    /// <summary>Mostrar pistas la primera vez que se usa algo.</summary>
    public bool HintsEnabled { get; set; } = true;

    /// <summary>Pistas ya mostradas (<see cref="Onboarding.OnboardingGuide.Hints"/>).</summary>
    public List<string> HintsShown { get; set; } = [];

    /// <summary>Qué calcula el render en caché por su cuenta (Preferencias › Rendimiento, D-060).</summary>
    public RenderCache.RenderCacheMode RenderCacheMode { get; set; } = RenderCache.RenderCacheMode.Smart;

    /// <summary>Carpeta del render en caché; nula, la de siempre (<c>RODAJE_RENDER</c> o <c>%LOCALAPPDATA%\Rodaje\render</c>).</summary>
    public string? RenderCacheFolder { get; set; }

    /// <summary>Tope del render en caché en GB; nulo, el automático (el menor entre 50 GB y el 10 % del disco libre, mínimo 5).</summary>
    public double? RenderCacheCapGb { get; set; }

    private void NormalizeWorkflow()
    {
        if (!Enum.IsDefined(RenderCacheMode))
        {
            RenderCacheMode = RenderCache.RenderCacheMode.Smart;
        }

        // Una ruta que no es completa se resolvería contra la carpeta desde la que se abrió Rodaje (modo caos, ronda 2).
        RenderCacheFolder = Personalization.NormalizeFolder(RenderCacheFolder);
        if (RenderCacheCapGb is not (>= 1 and <= 100_000))
        {
            RenderCacheCapGb = null;
        }

        HintsShown = [.. (HintsShown ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct()];
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
