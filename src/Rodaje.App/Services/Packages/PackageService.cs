using System.Text.Json;
using Rodaje.App.Services.Packages.Download;
using Rodaje.Packages;
using Rodaje.Packages.Download;

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

/// <summary>Avance de una instalación: qué se está haciendo («Paquete», «Modelo 2 de 4: …») y cuánto va de eso, de 0 a 1.</summary>
public readonly record struct InstallProgress(double Fraction, string Stage);

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

    /// <summary>Ids de módulos que cambiaron de nombre al publicarse en su repositorio (v1.0.0): lo instalado se migra.</summary>
    public static IReadOnlyDictionary<string, string> RenamedModules { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["rodaje-audio-ia"] = "modulo-audio-ia",
        ["rodaje-vision"] = "modulo-vision",
        ["rodaje-raw"] = "modulo-raw",
    };

    private readonly PackageInstaller _installer;
    private readonly InstanceRegistry? _instances;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _leftoversResolved;
    private Task<PackageScan>? _firstScan;

    /// <param name="instances">Para saber si hay otras instancias abiertas; sin él, esta se cree sola.</param>
    /// <param name="models">Dónde se guardan y de dónde se bajan los modelos de los módulos; por defecto, <c>complementos\modelos</c>.</param>
    public PackageService(PackageLocations locations, PackageCatalog catalog, IPackageDownloader? downloader = null, InstanceRegistry? instances = null,
        ModelStore? models = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(catalog);
        Locations = locations;
        Catalog = catalog;
        Downloader = downloader ?? CatalogSource.CreateDownloader(locations);
        Models = models ?? CatalogSource.CreateModelStore(locations);
        _installer = new PackageInstaller(locations, AppId);
        _instances = instances;
    }

    /// <summary>Almacén de modelos de los módulos.</summary>
    public ModelStore Models { get; }

    /// <summary>
    /// Cierra el proceso de un módulo antes de desinstalarlo o de reemplazar sus archivos (lo pone la ventana con
    /// <c>ModuleManager.CloseModuleAsync</c>); sin él, un módulo abierto bloquearía su carpeta.
    /// </summary>
    public Func<string, Task>? CloseModule { get; set; }

    /// <summary>Modelos que les faltan a los módulos instalados por la persona, por id de módulo.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ModuleModel>> MissingModels { get; private set; } = new Dictionary<string, IReadOnlyList<ModuleModel>>();

    /// <summary>Lo que falta bajar para que <paramref name="moduleId"/> funcione (vacío si nada).</summary>
    public IReadOnlyList<ModuleModel> MissingModelsOf(string moduleId) => MissingModels.TryGetValue(moduleId, out IReadOnlyList<ModuleModel>? missing) ? missing : [];

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

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        try
        {
            return PackageCatalog.ParseUtf8(buffer.ToArray(), problems);
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

        return [.. entries.OrderBy(e => e.Kind).ThenByDescending(e => e.IsInstalled).ThenBy(e => e.Name, SpanishText.Comparer)];
    }

    /// <summary>
    /// Lo instalado, leyéndolo antes si todavía no se leyó nunca. Las llamadas que llegan mientras dura esa primera lectura
    /// la comparten: antes, cada proyecto abierto en ese rato encolaba una lectura completa del disco detrás de las otras
    /// (y lo que esperaba a la suya seguía reteniendo su proyecto).
    /// </summary>
    public Task<PackageScan> ScannedAsync()
    {
        if (HasScanned)
        {
            return Task.FromResult(Installed);
        }

        if (_firstScan is null || _firstScan.IsFaulted || _firstScan.IsCanceled)
        {
            _firstScan = RefreshAsync();
        }

        return _firstScan;
    }

    /// <summary>Vuelve a leer lo instalado.</summary>
    public async Task<PackageScan> RefreshAsync()
    {
        await ExclusiveAsync(() => true);
        return Installed;
    }

    /// <summary>
    /// Instala un <c>.paquete</c>. Un módulo abierto con el mismo id se cierra antes de poner la versión nueva; sus modelos
    /// se bajan aparte (<see cref="EnsureModelsAsync"/>).
    /// </summary>
    /// <exception cref="PackageInstallException">No se pudo instalar.</exception>
    public Task<InstallResult> InstallFromFileAsync(string path, string? expectedSha256 = null, bool allowDowngrade = false) =>
        ExclusiveAsync(() =>
        {
            try
            {
                return _installer.Install(path, expectedSha256, allowDowngrade, allowReplace: ActiveInstances() == 1,
                    beforeCommit: m =>
                    {
                        if (m.Kind == PackageKind.Module)
                        {
                            CloseModuleNow(m.Id);
                        }
                    });
            }
            catch (PackageReplaceBlockedException ex)
            {
                throw new PackageInstallException($"«{ex.PackageName}» ya está instalado. {OtherInstancesMessage}", ex);
            }
        });

    /// <summary>
    /// Descarga e instala (o actualiza) una entrada del catálogo con <see cref="Downloader"/> y, si es un módulo, baja sus
    /// modelos (<see cref="EnsureModelsAsync"/>). Una descarga cancelada o cortada deja su parcial para reanudar; lo
    /// descargado se borra después de instalarlo o si no se pudo instalar.
    /// </summary>
    /// <exception cref="NotSupportedException">Esta versión no descarga.</exception>
    /// <exception cref="PackageDownloadException">No se pudo descargar o no es lo publicado (ya se borró).</exception>
    /// <exception cref="ModelsPendingException">El módulo quedó instalado, pero faltan modelos (se reanudan con <see cref="EnsureModelsAsync"/>).</exception>
    /// <exception cref="PackageInstallException">No se pudo instalar.</exception>
    public async Task<InstallResult> InstallFromCatalogAsync(CatalogEntry entry, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Downloader.IsAvailable)
        {
            throw new NotSupportedException(Downloader.UnavailableReason);
        }

        string stage = entry.Kind == PackageKind.Module ? "Módulo" : "Paquete";
        IProgress<double>? package = progress is null ? null : new DirectProgress<double>(f => progress.Report(new InstallProgress(f, stage)));
        // En el pool: leer la red, calcular el hash y escribir cada bloque no puede pasar por el hilo de la interfaz.
        DownloadedPackage downloaded = await Task.Run(() => Downloader.DownloadAsync(entry, package, cancellationToken), cancellationToken);
        InstallResult result;
        try
        {
            result = await InstallFromFileAsync(downloaded.Path, downloaded.ExpectedSha256);
        }
        finally
        {
            try
            {
                File.Delete(downloaded.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (result.Package.Manifest.Kind == PackageKind.Module)
        {
            try
            {
                await EnsureModelsAsync(result.Package.Id, progress, cancellationToken);
            }
            catch (Exception ex) when (ex is PackageDownloadException or IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
            {
                throw new ModelsPendingException(result, ex);
            }
        }

        return result;
    }

    /// <summary>
    /// Baja con <see cref="Models"/> (hash al vuelo y reanudación) los modelos que declara un módulo instalado y los deja
    /// en su carpeta, donde los busca (<c>&lt;carpeta del módulo&gt;\&lt;file&gt;</c>). Lo ya presente no se vuelve a bajar.
    /// </summary>
    /// <exception cref="PackageDownloadException">Un modelo no se pudo bajar o no es el publicado.</exception>
    /// <exception cref="OperationCanceledException">Se canceló; lo recibido queda para reanudar.</exception>
    public async Task EnsureModelsAsync(string moduleId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Installed.Find(moduleId) is not { Origin: PackageOrigin.Installed, Manifest.Kind: PackageKind.Module } module)
        {
            return;
        }

        try
        {
            IReadOnlyList<ModuleModel> models = await Task.Run(() => ModuleModels.Read(Path.Combine(module.Directory, PackageManifest.FileName)), cancellationToken);
            for (int i = 0; i < models.Count; i++)
            {
                ModuleModel model = models[i];
                string stage = $"Modelo {i + 1} de {models.Count}: {model.Id} ({FormatSize(model.Size)})";
                IProgress<DownloadProgress>? bytes = progress is null ? null : new DirectProgress<DownloadProgress>(p => progress.Report(new InstallProgress(p.Fraction, stage)));
                await Task.Run(() => Models.PlaceAsync(model, module.Directory, bytes, cancellationToken), cancellationToken);
            }
        }
        finally
        {
            await RefreshAsync();
        }
    }

    /// <summary>«1,2 GB», «340 MB».</summary>
    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30
            ? string.Create(Rodaje.App.Services.Format.SpanishNumbers.Format, $"{bytes / (double)(1L << 30):0.0} GB")
            : string.Create(Rodaje.App.Services.Format.SpanishNumbers.Format, $"{Math.Max(1, (long)Math.Round(bytes / (double)(1L << 20)))} MB");

    /// <summary>
    /// Versión publicada más nueva de cada complemento, para ofrecer «Actualizar». Sin conexión o sin catálogo publicado
    /// devuelve lo último que supo (vacío al principio) y deja el motivo en <see cref="UpdatesProblem"/>.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, PackageVersion>> AvailableVersionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Downloader.IsAvailable)
        {
            return Available;
        }

        try
        {
            Available = await Downloader.AvailableVersionsAsync(cancellationToken);
            UpdatesProblem = null;
        }
        catch (Exception ex) when (ex is Rodaje.Packages.Download.PackageDownloadException or HttpRequestException or IOException)
        {
            UpdatesProblem = ex.Message;
        }

        return Available;
    }

    /// <summary>Lo último que se supo de las versiones publicadas.</summary>
    public IReadOnlyDictionary<string, PackageVersion> Available { get; private set; } = new Dictionary<string, PackageVersion>();

    /// <summary>Por qué no se pudo consultar el catálogo la última vez, o <c>null</c>.</summary>
    public string? UpdatesProblem { get; private set; }

    /// <summary>Hay una versión publicada más nueva que la instalada (solo para lo instalado por la persona).</summary>
    public bool HasUpdate(PluginEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Installed is { Origin: PackageOrigin.Installed } installed && entry.Catalog is not null
            && Available.TryGetValue(entry.Id, out PackageVersion? published) && published.CompareTo(installed.Manifest.Version) > 0;
    }

    /// <summary>Desinstala; un módulo abierto se cierra antes y los modelos que ya no usa nadie se borran del almacén.</summary>
    /// <exception cref="PackageInstallException">No se pudo desinstalar.</exception>
    public Task UninstallAsync(string packageId) => ExclusiveAsync(() =>
    {
        if (ActiveInstances() > 1)
        {
            throw new PackageInstallException(OtherInstancesMessage);
        }

        if (Installed.Find(packageId) is null or { Manifest.Kind: PackageKind.Module })
        {
            CloseModuleNow(packageId);
        }

        _installer.Uninstall(packageId);
        PruneModels();
        return true;
    });

    /// <summary>Cierra el módulo desde el hilo de trabajo de <see cref="ExclusiveAsync{T}"/> (sin contexto de la interfaz).</summary>
    private void CloseModuleNow(string moduleId)
    {
        if (CloseModule is { } close)
        {
            close(moduleId).GetAwaiter().GetResult();
        }
    }

    private void PruneModels()
    {
        PackageScan scan = PackageLibrary.Scan(Locations, AppId);
        var inUse = new List<string>();
        foreach (InstalledPackage module in scan.Packages.Where(p => p.Manifest.Kind == PackageKind.Module))
        {
            inUse.AddRange(ModuleModels.Read(Path.Combine(module.Directory, PackageManifest.FileName)).Select(m => m.Sha256));
        }

        Models.Prune(inUse);
    }

    /// <summary>
    /// Pasa los módulos instalados con un id viejo (<see cref="RenamedModules"/>) al nuevo: cambia el id de su manifiesto y
    /// el nombre de su carpeta. Si el nuevo ya está instalado, el viejo se borra. Uno con el manifiesto dañado se deja donde
    /// está. Devuelve cuántos migró o borró.
    /// </summary>
    public static int MigrateRenamedModules(PackageLocations locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        int migrated = 0;
        foreach ((string oldId, string newId) in RenamedModules)
        {
            string from = Path.Combine(locations.Modules, oldId);
            if (!Directory.Exists(from))
            {
                continue;
            }

            string to = Path.Combine(locations.Modules, newId);
            if (Directory.Exists(to))
            {
                Directory.Delete(from, recursive: true);
                migrated++;
                continue;
            }

            string manifest = Path.Combine(from, PackageManifest.FileName);
            if (File.Exists(manifest))
            {
                // Un manifiesto dañado se deja como está: la lectura lo anota en los problemas de lo instalado. Antes, su
                // JsonException hacía fallar toda operación con complementos, y en cada intento (tarea 262).
                byte[] renamed;
                try
                {
                    renamed = WithId(File.ReadAllBytes(manifest), oldId, newId);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    continue;
                }

                File.WriteAllBytes(manifest, renamed);
            }

            Directory.Move(from, to);
            migrated++;
        }

        return migrated;
    }

    /// <summary>El mismo JSON con <c>id</c> = <paramref name="newId"/> si era <paramref name="oldId"/>; lo demás, igual.</summary>
    private static byte[] WithId(byte[] json, string oldId, string newId)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("id") && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == oldId)
                {
                    writer.WriteString("id", newId);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Hace <paramref name="work"/> fuera del hilo de la interfaz y sin otra operación a la vez, y vuelve a leer lo instalado.</summary>
    private async Task<T> ExclusiveAsync<T>(Func<T> work)
    {
        await _gate.WaitAsync();
        T value;
        try
        {
            (value, PackageScan scan, Dictionary<string, IReadOnlyList<ModuleModel>> missing) = await Task.Run(() =>
            {
                ResolveLeftoversOnce();
                T result = work();
                PackageScan found = PackageLibrary.Scan(Locations, AppId);
                return (result, found, FindMissingModels(found));
            });
            Installed = scan;
            MissingModels = missing;
            HasScanned = true;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return value;
    }

    private static Dictionary<string, IReadOnlyList<ModuleModel>> FindMissingModels(PackageScan scan)
    {
        var missing = new Dictionary<string, IReadOnlyList<ModuleModel>>(StringComparer.Ordinal);
        foreach (InstalledPackage module in scan.Packages.Where(p => p is { Origin: PackageOrigin.Installed, Manifest.Kind: PackageKind.Module }))
        {
            ModuleModel[] absent = [.. ModuleModels.Read(Path.Combine(module.Directory, PackageManifest.FileName)).Where(m => !m.IsPresentIn(module.Directory))];
            if (absent.Length > 0)
            {
                missing[module.Id] = absent;
            }
        }

        return missing;
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
            MigrateRenamedModules(Locations);
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

/// <summary>El módulo quedó instalado, pero no se pudieron bajar todos sus modelos; lo bajado se guarda para reanudar.</summary>
public sealed class ModelsPendingException(InstallResult result, Exception inner)
    : Exception($"«{result.Package.Manifest.Name}» quedó instalado, pero faltan sus modelos: {inner.Message} «Bajar modelos» sigue desde donde quedó.", inner)
{
    public InstallResult Result { get; } = result;
}

/// <summary>Progreso sin pasar por el contexto de sincronización (lo reenvía quien llama con su propio <see cref="Progress{T}"/>).</summary>
internal sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
