using Rodaje.Paquetes;

namespace Rodaje.App.Services.Packages;

/// <summary>Un complemento tal como lo muestra la página de complementos: lo del catálogo junto con lo instalado.</summary>
public sealed record PluginEntry(
    string Id,
    PackageKind Kind,
    string Name,
    string Description,
    Uri? Repository,
    IReadOnlyList<string> WorksIn,
    InstalledPackage? Installed,
    CatalogEntry? Catalog)
{
    public bool IsInstalled => Installed is not null;

    public bool CanUninstall => Installed?.CanUninstall == true;

    public string? InstalledVersion => Installed?.Manifest.Version.ToString();
}

/// <summary>
/// Los complementos de Rodaje: el catálogo que viaja con el programa, lo instalado en
/// <c>%LOCALAPPDATA%\Rodaje\complementos</c> (y en <c>paquetes\</c> y <c>modulos\</c> junto al exe, en la distribución
/// completa), instalar desde un archivo y desinstalar. El disco se toca fuera del hilo de la interfaz y de a una operación
/// por vez; el estado y <see cref="Changed"/> se publican en el hilo que llamó. Los restos de un corte se resuelven una sola
/// vez, antes de la primera operación hecha sin otras instancias abiertas: después podrían ser la extracción de una
/// instalación en curso. Con otras instancias abiertas (<see cref="InstanceRegistry"/>) no se desinstala ni se reemplaza
/// un paquete instalado, porque la otra podría estar usando sus archivos; instalar uno nuevo sí se puede.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "SemaphoreSlim sin AvailableWaitHandle no reserva nada que liberar.")]
public sealed class PackageService
{
    public const string AppId = "rodaje";
    public const string CatalogResourceName = "Rodaje.App.catalogo.json";

    public const string OtherInstancesMessage = "Cierra las otras ventanas de Rodaje para desinstalar o actualizar complementos.";

    private readonly PackageInstaller _installer;
    private readonly InstanceRegistry? _instances;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _leftoversResolved;

    /// <param name="instances">Para saber si hay otras instancias abiertas; sin él, esta se cree sola.</param>
    public PackageService(PackageLocations locations, PackageCatalog catalog, IPackageDownloader? downloader = null, InstanceRegistry? instances = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(catalog);
        Locations = locations;
        Catalog = catalog;
        Downloader = downloader ?? new UnavailablePackageDownloader("Descarga desde GitHub: próximamente.");
        _installer = new PackageInstaller(locations, AppId);
        _instances = instances;
    }

    /// <summary>Cambió lo instalado.</summary>
    public event EventHandler? Changed;

    public PackageLocations Locations { get; }

    public PackageCatalog Catalog { get; }

    public IPackageDownloader Downloader { get; }

    public PackageScan Installed { get; private set; } = PackageScan.Empty;

    /// <summary>Ya se leyó lo instalado al menos una vez; antes, <see cref="Installed"/> está vacío.</summary>
    public bool HasScanned { get; private set; }

    /// <summary>Instancias de Rodaje abiertas, contando esta. Lee el disco, pero es barato (un archivo por instancia).</summary>
    public int ActiveInstances() => _instances?.CountActive() ?? 1;

    /// <summary>Carpetas de siempre: los datos de la persona y, junto al exe, las de la distribución completa.</summary>
    public static PackageLocations DefaultLocations() =>
        PackageLocations.Under(Path.Combine(AppDataPaths.Root, "complementos"),
            [Path.Combine(AppContext.BaseDirectory, "paquetes"), Path.Combine(AppContext.BaseDirectory, "modulos")]);

    /// <summary>El catálogo que viaja dentro del programa.</summary>
    public static PackageCatalog LoadBundledCatalog(List<string>? problems = null)
    {
        using Stream? stream = typeof(PackageService).Assembly.GetManifestResourceStream(CatalogResourceName);
        if (stream is null)
        {
            problems?.Add("Falta el catálogo de complementos.");
            return PackageCatalog.Empty;
        }

        using var reader = new StreamReader(stream);
        try
        {
            return PackageCatalog.Parse(reader.ReadToEnd(), problems);
        }
        catch (FormatException ex)
        {
            problems?.Add(ex.Message);
            return PackageCatalog.Empty;
        }
    }

    /// <summary>Catálogo e instalados juntos: primero lo instalado o conocido de contenido, luego los módulos.</summary>
    public IReadOnlyList<PluginEntry> Entries()
    {
        var entries = new List<PluginEntry>();
        foreach (CatalogEntry c in Catalog.Entries)
        {
            InstalledPackage? installed = Installed.Find(c.Id);
            entries.Add(new PluginEntry(c.Id, c.Kind, installed?.Manifest.Name ?? c.Name, c.Description, c.Repository,
                [.. (installed?.Manifest.Apps ?? c.Apps).Select(Catalog.AppName)], installed, c));
        }

        foreach (InstalledPackage p in Installed.Packages.Where(p => Catalog.Find(p.Id) is null && p.Origin != PackageOrigin.Own))
        {
            entries.Add(new PluginEntry(p.Id, p.Manifest.Kind, p.Manifest.Name, p.Manifest.Description ?? "", p.Manifest.Repository,
                [.. p.Manifest.Apps.Select(Catalog.AppName)], p, null));
        }

        return [.. entries.OrderBy(e => e.Kind).ThenByDescending(e => e.IsInstalled).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Vuelve a leer lo instalado.</summary>
    public async Task<PackageScan> RefreshAsync()
    {
        await ExclusiveAsync(() => true);
        return Installed;
    }

    /// <exception cref="PackageInstallException">No se pudo instalar.</exception>
    public Task<InstallResult> InstallFromFileAsync(string path, string? expectedSha256 = null, bool allowDowngrade = false) =>
        ExclusiveAsync(() =>
        {
            try
            {
                return _installer.Install(path, expectedSha256, allowDowngrade, allowReplace: ActiveInstances() == 1);
            }
            catch (PackageReplaceBlockedException ex)
            {
                throw new PackageInstallException($"«{ex.PackageName}» ya está instalado. {OtherInstancesMessage}", ex);
            }
        });

    /// <summary>Descarga e instala una entrada del catálogo con <see cref="Downloader"/> (desde la fase 7).</summary>
    /// <exception cref="NotSupportedException">No hay descarga todavía.</exception>
    /// <exception cref="PackageInstallException">No se pudo instalar.</exception>
    public async Task<InstallResult> InstallFromCatalogAsync(CatalogEntry entry, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Downloader.IsAvailable)
        {
            throw new NotSupportedException(Downloader.UnavailableReason);
        }

        DownloadedPackage downloaded = await Downloader.DownloadAsync(entry, progress, cancellationToken);
        return await InstallFromFileAsync(downloaded.Path, downloaded.ExpectedSha256);
    }

    /// <exception cref="PackageInstallException">No se pudo desinstalar.</exception>
    public Task UninstallAsync(string packageId) => ExclusiveAsync(() =>
    {
        if (ActiveInstances() > 1)
        {
            throw new PackageInstallException(OtherInstancesMessage);
        }

        _installer.Uninstall(packageId);
        return true;
    });

    /// <summary>Hace <paramref name="work"/> fuera del hilo de la interfaz y sin otra operación a la vez, y vuelve a leer lo instalado.</summary>
    private async Task<T> ExclusiveAsync<T>(Func<T> work)
    {
        await _gate.WaitAsync();
        T value;
        try
        {
            (value, PackageScan scan) = await Task.Run(() =>
            {
                ResolveLeftoversOnce();
                return (work(), PackageLibrary.Scan(Locations, AppId));
            });
            Installed = scan;
            HasScanned = true;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return value;
    }

    private void ResolveLeftoversOnce()
    {
        if (_leftoversResolved || ActiveInstances() > 1)
        {
            return;
        }

        try
        {
            _installer.CleanUpLeftovers();
            _leftoversResolved = true;
        }
        catch (PackageInstallException)
        {
        }
        catch (IOException)
        {
            _leftoversResolved = true;
        }
        catch (UnauthorizedAccessException)
        {
            _leftoversResolved = true;
        }
    }
}
