using Microsoft.UI.Xaml;
using Rodaje.App.Services.Packages;
using Rodaje.Paquetes;

namespace Rodaje.App.Views.Plugins;

/// <summary>Una fila de la página de complementos.</summary>
/// <param name="othersOpen">Hay otras instancias de Rodaje abiertas: no se puede desinstalar ni actualizar.</param>
/// <param name="hasUpdate">Hay una versión publicada más nueva que la instalada.</param>
/// <param name="download">La descarga de este complemento (en curso o no).</param>
internal sealed partial class PluginRow(PluginEntry entry, IPackageDownloader downloader, bool othersOpen, bool hasUpdate, DownloadState download)
{
    public DownloadState Download { get; } = download;

    public PluginEntry Entry { get; } = entry;

    public string Name { get; } = entry.Name;

    public string Description { get; } = entry.Description;

    public string Glyph { get; } = entry.Kind == PackageKind.Module ? "\uE945" : "\uE7B8";

    public string State { get; } = entry.Installed switch
    {
        null => "No instalado",
        { Origin: PackageOrigin.Bundled } installed => $"Incluido · {installed.Manifest.Version}",
        { } installed => $"Instalado · {installed.Manifest.Version}",
    };

    public string WorksIn { get; } = "Funciona en: " + string.Join(", ", entry.WorksIn);

    public string Contents { get; } = entry.Installed is { } p ? PluginActions.Describe(p.Manifest) : "";

    public Uri? Repository { get; } = entry.Repository;

    public string RepositoryText { get; } = entry.Repository is { } uri ? uri.Host + uri.AbsolutePath : "";

    public Visibility RepositoryVisibility { get; } = entry.Repository is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility InstallVisibility { get; } = entry.Catalog is not null && (!entry.IsInstalled || hasUpdate) ? Visibility.Visible : Visibility.Collapsed;

    public string InstallText { get; } = hasUpdate ? "Actualizar" : "Instalar";

    public bool CanDownload { get; } = downloader.IsAvailable && !(hasUpdate && othersOpen);

    public string InstallTooltip { get; } = !downloader.IsAvailable ? downloader.UnavailableReason ?? ""
        : hasUpdate ? "Descargar la versión nueva desde GitHub, comprobar su firma y su hash, y reemplazar la instalada"
        : "Descargar desde GitHub, comprobar su firma y su hash, e instalar";

    public Visibility UninstallVisibility { get; } = entry.CanUninstall ? Visibility.Visible : Visibility.Collapsed;

    public bool CanUninstallNow { get; } = !othersOpen;

    public double StateOpacity { get; } = entry.IsInstalled ? 1 : 0.6;
}

/// <summary>Un paquete que no se pudo cargar.</summary>
internal sealed partial class ProblemRow(PackageProblem problem)
{
    public string Folder { get; } = problem.Directory;

    public string Message { get; } = problem.Message;
}
