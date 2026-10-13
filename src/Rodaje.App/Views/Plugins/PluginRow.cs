using System.ComponentModel;
using Microsoft.UI.Xaml;
using Rodaje.App.Services.Packages;
using Rodaje.Packages;

namespace Rodaje.App.Views.Plugins;

/// <summary>
/// Una fila de la página de complementos. Repite el avance de su descarga (que vive en la página) con una suscripción
/// propia que la página suelta al rehacer la lista (<see cref="Dispose"/>): la plantilla se enlaza a la fila, no a la descarga.
/// </summary>
/// <param name="othersOpen">Hay otras instancias de Rodaje abiertas: no se puede desinstalar ni actualizar.</param>
/// <param name="hasUpdate">Hay una versión publicada más nueva que la instalada.</param>
/// <param name="download">La descarga de este complemento (en curso o no).</param>
/// <param name="missingModelBytes">Lo que ocupan los modelos que le faltan a este módulo instalado (0 si ninguno).</param>
internal sealed partial class PluginRow(PluginEntry entry, IPackageDownloader downloader, bool othersOpen, bool hasUpdate, DownloadState download,
    long missingModelBytes = 0) : INotifyPropertyChanged, IDisposable
{
    private PropertyChangedRelay? _relay;

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add
        {
            _relay ??= new PropertyChangedRelay(Download, name => _propertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                string.IsNullOrEmpty(name) ? name : "Download" + name)));
            _propertyChanged += value;
        }
        remove => _propertyChanged -= value;
    }

    private PropertyChangedEventHandler? _propertyChanged;

    /// <summary>Suelta la suscripción a la descarga; la página lo hace antes de rehacer la lista.</summary>
    public void Dispose()
    {
        _relay?.Dispose();
        _propertyChanged = null;
    }

    public double DownloadPercent => Download.Percent;

    public string DownloadText => Download.Text;

    public Visibility DownloadActiveVisibility => Download.ActiveVisibility;

    public Visibility DownloadIdleVisibility => Download.IdleVisibility;

    /// <summary>El módulo está instalado pero le faltan modelos: el botón los baja en vez de reinstalar.</summary>
    public bool NeedsModels { get; } = missingModelBytes > 0 && !hasUpdate;

    public DownloadState Download { get; } = download;

    public PluginEntry Entry { get; } = entry;

    public string Name { get; } = entry.Name;

    public string Description { get; } = entry.Description;

    public string Glyph { get; } = entry.Kind == PackageKind.Module ? "\uE945" : "\uE7B8";

    public string State { get; } = entry.Installed switch
    {
        null => "No instalado",
        { Origin: PackageOrigin.Bundled } installed => $"Incluido · {installed.Manifest.Version}",
        { } installed when missingModelBytes > 0 => $"Instalado · {installed.Manifest.Version} · faltan modelos ({PackageService.FormatSize(missingModelBytes)})",
        { } installed => $"Instalado · {installed.Manifest.Version}",
    };

    public string WorksIn { get; } = "Funciona en: " + string.Join(", ", entry.WorksIn);

    public string Contents { get; } = entry.Installed is { } p ? PluginActions.Describe(p.Manifest) : "";

    public Uri? Repository { get; } = entry.Repository;

    public string RepositoryText { get; } = entry.Repository is { } uri ? uri.Host + uri.AbsolutePath : "";

    public Visibility RepositoryVisibility { get; } = entry.Repository is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility InstallVisibility { get; } =
        (entry.Catalog is not null && (!entry.IsInstalled || hasUpdate)) || missingModelBytes > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string InstallText { get; } = hasUpdate ? "Actualizar" : missingModelBytes > 0 ? "Bajar modelos" : "Instalar";

    public bool CanDownload { get; } = missingModelBytes > 0 && !hasUpdate || downloader.IsAvailable && !(hasUpdate && othersOpen);

    public string InstallTooltip { get; } = missingModelBytes > 0 && !hasUpdate ? "Descargar sus modelos (Hugging Face o las releases del proyecto), comprobar su hash y dejarlos en la carpeta del módulo"
        : !downloader.IsAvailable ? downloader.UnavailableReason ?? ""
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
