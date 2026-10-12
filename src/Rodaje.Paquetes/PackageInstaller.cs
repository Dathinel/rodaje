using System.IO.Compression;
using System.Security.Cryptography;

namespace Rodaje.Paquetes;

/// <summary>Un paquete que no se pudo instalar o desinstalar; el mensaje se le puede mostrar a la persona.</summary>
public class PackageInstallException : Exception
{
    public PackageInstallException()
    {
    }

    public PackageInstallException(string message)
        : base(message)
    {
    }

    public PackageInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Qué pasó al instalar.</summary>
public enum InstallOutcome
{
    Installed,
    Updated,
    Reinstalled,
    Downgraded,
}

/// <summary>El paquete ya está instalado y se pidió no reemplazarlo (por ejemplo, porque otra ventana podría estar usándolo).</summary>
public sealed class PackageReplaceBlockedException : PackageInstallException
{
    public PackageReplaceBlockedException()
    {
    }

    public PackageReplaceBlockedException(string message)
        : base(message)
    {
    }

    public PackageReplaceBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PackageReplaceBlockedException(string packageId, string packageName, PackageVersion? installedVersion)
        : base($"Ya está instalado «{packageName}» y ahora no se puede reemplazar.")
    {
        PackageId = packageId;
        PackageName = packageName;
        InstalledVersion = installedVersion;
    }

    public string PackageId { get; } = "";

    public string PackageName { get; } = "";

    public PackageVersion? InstalledVersion { get; }
}

public sealed record InstallResult(InstalledPackage Package, InstallOutcome Outcome, PackageVersion? PreviousVersion, IReadOnlyList<string> Warnings);

/// <summary>Límites contra zips malformados o «bombas» de compresión.</summary>
public sealed record InstallLimits
{
    public int MaxEntries { get; init; } = 20_000;

    /// <summary>Lo que puede ocupar descomprimido (los módulos de IA traen modelos de varios GB).</summary>
    public long MaxTotalBytes { get; init; } = 8L << 30;

    /// <summary>Tasa de compresión máxima de una entrada de más de 1 MB y del paquete entero si pasa de 16 MB.</summary>
    public int MaxCompressionRatio { get; init; } = 200;

    /// <summary>Lo que debe quedar libre en el disco después de instalar.</summary>
    public long FreeSpaceReserve { get; init; } = 256L << 20;

    public static InstallLimits Default { get; } = new();
}

/// <summary>
/// Instala un <c>.paquete</c> (zip con <c>paquete.json</c>) y desinstala paquetes. Nunca escribe fuera de la carpeta del
/// paquete: cada entrada del zip se valida (<see cref="SafePaths"/>) y se comprueba la ruta final. La carpeta definitiva se
/// reemplaza con un cambio de nombre, así un corte deja la versión anterior o la nueva, nunca una mezcla; si el corte llega
/// entre los dos cambios de nombre, <see cref="CleanUpLeftovers"/> devuelve la anterior a su sitio al arrancar.
/// Instalar, desinstalar y limpiar restos toman <see cref="OperationLockPath"/>, un bloqueo entre procesos: así dos
/// programas (o dos ventanas) con la misma carpeta nunca se cruzan, y la limpieza no ve como resto lo que otro está extrayendo.
/// </summary>
public sealed class PackageInstaller(PackageLocations locations, string appId, InstallLimits? limits = null)
{
    private const string InstallingPrefix = ".instalando-";
    private const string RemovingPrefix = ".desinstalando-";
    private const string PreviousPrefix = ".anterior-";
    private const int TagLength = 12;

    public const string OperationLockFileName = ".operacion.lock";

    private readonly InstallLimits _limits = limits ?? InstallLimits.Default;

    public PackageLocations Locations { get; } = locations ?? throw new ArgumentNullException(nameof(locations));

    public string AppId { get; } = string.IsNullOrEmpty(appId) ? throw new ArgumentException("Falta el programa.", nameof(appId)) : appId;

    /// <summary>Cuánto se espera a que otro proceso termine su operación antes de rendirse.</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Archivo junto a <see cref="PackageLocations.Packages"/> que se abre sin compartir mientras dura una operación.</summary>
    public string OperationLockPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Locations.Packages)) ?? Locations.Packages, OperationLockFileName);

    /// <summary>SHA-256 de un archivo en hexadecimal en minúsculas.</summary>
    public static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Instala o actualiza desde un archivo local.</summary>
    /// <param name="archivePath">El <c>.paquete</c>.</param>
    /// <param name="expectedSha256">Si se da (hexadecimal), el archivo debe tener ese hash; así se instala lo que se descargó.</param>
    /// <param name="allowDowngrade">Permite reemplazar una versión instalada más nueva.</param>
    /// <param name="allowReplace">Si es falso, un paquete ya instalado no se toca (<see cref="PackageReplaceBlockedException"/>).</param>
    /// <param name="cancellationToken">Cancela la extracción.</param>
    /// <exception cref="PackageInstallException">No se pudo instalar; nada cambió.</exception>
    public InstallResult Install(string archivePath, string? expectedSha256 = null, bool allowDowngrade = false, bool allowReplace = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        try
        {
            using FileStream operation = AcquireOperationLock();
            return InstallCore(Path.GetFullPath(archivePath), expectedSha256, allowDowngrade, allowReplace, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            throw new PackageInstallException("No se pudo instalar: " + ex.Message, ex);
        }
    }

    private InstallResult InstallCore(string archive, string? expectedSha256, bool allowDowngrade, bool allowReplace, CancellationToken cancellationToken)
    {
        if (!File.Exists(archive))
        {
            throw new PackageInstallException($"No existe {archive}.");
        }

        if (expectedSha256 is not null)
        {
            string actual = ComputeSha256(archive);
            if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageInstallException($"El archivo no es el esperado: su SHA-256 es {actual}.");
            }
        }

        // Se extrae primero bajo la carpeta de paquetes (mismo volumen, para mover sin copiar) y luego se decide dónde va.
        Directory.CreateDirectory(Locations.Packages);
        string staging = Path.Combine(Locations.Packages, InstallingPrefix + NewTag());
        try
        {
            Extract(archive, staging, cancellationToken);
            ManifestResult read = ManifestReader.ReadFile(Path.Combine(staging, PackageManifest.FileName));
            if (read.Manifest is not { } manifest)
            {
                throw new PackageInstallException("El paquete no es válido: " + string.Join(" ", read.Errors));
            }

            if (!manifest.WorksIn(AppId))
            {
                throw new PackageInstallException($"«{manifest.Name}» funciona en {string.Join(", ", manifest.Apps)}, no en este programa.");
            }

            if (manifest.Id == PackageLibrary.OwnPackageId)
            {
                throw new PackageInstallException($"El id «{PackageLibrary.OwnPackageId}» está reservado para los elementos propios.");
            }

            List<string> warnings = [.. read.Warnings];
            if (FontBytes(manifest, staging) is var (families, fontBytes) && families > 0)
            {
                warnings.Add(families == 1
                    ? $"Trae una fuente que ocupa {FormatBytes(fontBytes)}."
                    : $"Trae {families} fuentes que ocupan {FormatBytes(fontBytes)}.");
            }

            string root = Locations.InstallRoot(manifest.Kind);
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, manifest.Id);
            string? movedAside = null;
            PackageVersion? previous = null;
            InstallOutcome outcome = InstallOutcome.Installed;
            string? otherRoot = manifest.Kind == PackageKind.Module ? Locations.Packages : Locations.Modules;
            if (Directory.Exists(Path.Combine(otherRoot, manifest.Id)))
            {
                throw new PackageInstallException($"Ya hay instalado otro complemento con el id «{manifest.Id}» de distinto tipo; desinstálalo primero.");
            }

            if (Directory.Exists(target))
            {
                previous = ManifestReader.ReadFile(Path.Combine(target, PackageManifest.FileName), checkFiles: false).Manifest?.Version;
                if (!allowReplace)
                {
                    throw new PackageReplaceBlockedException(manifest.Id, manifest.Name, previous);
                }

                int order = previous is null ? 1 : manifest.Version.CompareTo(previous);
                if (order < 0 && !allowDowngrade)
                {
                    throw new PackageInstallException($"Ya está instalada la versión {previous}, más nueva que la {manifest.Version}.");
                }

                outcome = order > 0 ? InstallOutcome.Updated : order == 0 ? InstallOutcome.Reinstalled : InstallOutcome.Downgraded;
                movedAside = Path.Combine(root, PreviousPrefix + NewTag() + "-" + manifest.Id);
                Directory.Move(target, movedAside);
            }

            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                if (movedAside is not null)
                {
                    Directory.Move(movedAside, target);
                }

                throw;
            }

            if (movedAside is not null)
            {
                TryDelete(movedAside);
            }

            return new InstallResult(new InstalledPackage(manifest, Path.GetFullPath(target), PackageOrigin.Installed), outcome, previous, warnings);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                TryDelete(staging);
            }
        }
    }

    /// <summary>Borra un paquete instalado por la persona. Los incluidos con el programa y los propios no se tocan.</summary>
    /// <exception cref="PackageInstallException">No está instalado o no se pudo borrar.</exception>
    public void Uninstall(string packageId)
    {
        if (!Identifiers.IsValid(packageId) || packageId == PackageLibrary.OwnPackageId)
        {
            throw new PackageInstallException($"«{packageId}» no es un paquete que se pueda desinstalar.");
        }

        using FileStream operation = AcquireOperationLock();

        foreach (string root in new[] { Locations.Packages, Locations.Modules })
        {
            string target = Path.Combine(root, packageId);
            if (!Directory.Exists(target))
            {
                continue;
            }

            string aside = Path.Combine(root, RemovingPrefix + NewTag());
            try
            {
                Directory.Move(target, aside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PackageInstallException($"No se pudo desinstalar «{packageId}»: {ex.Message}", ex);
            }

            TryDelete(aside);
            return;
        }

        throw new PackageInstallException($"«{packageId}» no está instalado.");
    }

    /// <summary>
    /// Resuelve lo que dejó una instalación o desinstalación cortada (por ejemplo, por un apagón): borra las extracciones a
    /// medias y lo que se estaba desinstalando, y devuelve la versión anterior a su sitio si la nueva no quedó completa.
    /// Solo debe llamarse al arrancar, cuando no hay ninguna instalación en curso.
    /// </summary>
    /// <returns>Cuántos restos se resolvieron.</returns>
    /// <exception cref="PackageInstallException">Otro proceso tiene una operación en curso.</exception>
    public int CleanUpLeftovers()
    {
        using FileStream operation = AcquireOperationLock();
        int resolved = 0;
        foreach (string root in new[] { Locations.Packages, Locations.Modules })
        {
            List<string> directories;
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                directories = [.. Directory.EnumerateDirectories(root)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Primero las versiones anteriores: restaurar una puede apartar una nueva incompleta como «desinstalando».
            foreach (string directory in directories.Where(d => Path.GetFileName(d).StartsWith(PreviousPrefix, StringComparison.Ordinal)))
            {
                if (ResolvePrevious(root, directory))
                {
                    resolved++;
                }
            }

            foreach (string directory in directories)
            {
                string name = Path.GetFileName(directory);
                if ((name.StartsWith(InstallingPrefix, StringComparison.Ordinal) || name.StartsWith(RemovingPrefix, StringComparison.Ordinal)) && TryDelete(directory))
                {
                    resolved++;
                }
            }
        }

        return resolved;
    }

    /// <summary>Una versión apartada durante una actualización: sobra si la nueva está completa; si no, vuelve a su sitio.</summary>
    private static bool ResolvePrevious(string root, string aside)
    {
        string name = Path.GetFileName(aside);
        int idStart = PreviousPrefix.Length + TagLength + 1;
        string id = name.Length > idStart && name[idStart - 1] == '-' ? name[idStart..] : "";
        if (!Identifiers.IsValid(id))
        {
            return TryDelete(aside);
        }

        string target = Path.Combine(root, id);
        if (IsComplete(target, id) || !IsComplete(aside, id))
        {
            return TryDelete(aside);
        }

        try
        {
            if (Directory.Exists(target))
            {
                string broken = Path.Combine(root, RemovingPrefix + NewTag());
                Directory.Move(target, broken);
                TryDelete(broken);
            }

            Directory.Move(aside, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Si la carpeta tiene un manifiesto válido con ese id y todos los archivos que nombra.</summary>
    private static bool IsComplete(string directory, string id)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            return ManifestReader.ReadFile(Path.Combine(directory, PackageManifest.FileName)).Manifest?.Id == id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Abre el archivo de bloqueo sin compartir; si lo tiene otro proceso, reintenta hasta <see cref="BusyTimeout"/>.</summary>
    /// <exception cref="PackageInstallException">Otro proceso no terminó a tiempo.</exception>
    private FileStream AcquireOperationLock()
    {
        string path = OperationLockPath;
        long deadline = Environment.TickCount64 + (long)BusyTimeout.TotalMilliseconds;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                const int SharingViolation = 32, LockViolation = 33;
                if ((ex.HResult & 0xFFFF) is not (SharingViolation or LockViolation))
                {
                    throw new PackageInstallException("No se pudo empezar: " + ex.Message, ex);
                }

                if (Environment.TickCount64 >= deadline)
                {
                    throw new PackageInstallException("Otra ventana de Rodaje u otro programa está instalando o desinstalando complementos; prueba de nuevo cuando termine.", ex);
                }

                Thread.Sleep(50);
            }
        }
    }

    private static string NewTag() => Guid.NewGuid().ToString("N")[..TagLength];

    private void Extract(string archive, string destination, CancellationToken cancellationToken)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        CheckTotals(zip, new FileInfo(archive).Length, destination);

        string prefix = CommonWrapperFolder(zip);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();
        Directory.CreateDirectory(destination);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = entry.FullName;
            if (prefix.Length > 0)
            {
                name = name[prefix.Length..];
                if (name.Length == 0)
                {
                    continue;
                }
            }

            bool isDirectory = name.EndsWith('/') || name.EndsWith('\\');
            string trimmed = isDirectory ? name.TrimEnd('/', '\\') : name;
            if (!SafePaths.TryNormalizeRelative(trimmed, out string? relative))
            {
                throw new PackageInstallException($"El paquete tiene una ruta no permitida: «{entry.FullName}».");
            }

            string path = Path.GetFullPath(Path.Combine(destination, relative));
            if (!SafePaths.IsInside(path, destination))
            {
                throw new PackageInstallException($"El paquete intenta escribir fuera de su carpeta: «{entry.FullName}».");
            }

            if (!seen.Add(relative))
            {
                throw new PackageInstallException($"El paquete repite el archivo «{relative}».");
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(path);
                continue;
            }

            if (entry.Length > (1 << 20) && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > _limits.MaxCompressionRatio)
            {
                throw new PackageInstallException($"«{relative}» está comprimido de forma sospechosa.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using Stream source = entry.Open();
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyLimited(source, target, entry.Length, cancellationToken);
            written.Add(path);
        }

        // Al disco antes del cambio de nombre, para que un apagón no deje la carpeta nueva con archivos vacíos. En paralelo:
        // de a uno, cada vaciado espera al disco y un paquete de miles de archivos tardaría minutos.
        try
        {
            Parallel.ForEach(written, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 16 }, file =>
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.Flush(flushToDisk: true);
            });
        }
        catch (AggregateException ex)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
        }
    }

    /// <summary>
    /// Antes de escribir nada: número de entradas, tamaño descomprimido declarado, tasa de compresión del conjunto (muchas
    /// entradas chicas muy comprimidas esquivan la de cada una) y espacio libre. <see cref="CopyLimited"/> asegura que
    /// ninguna entrada pase de lo que declara.
    /// </summary>
    private void CheckTotals(ZipArchive zip, long archiveBytes, string destination)
    {
        if (zip.Entries.Count > _limits.MaxEntries)
        {
            throw new PackageInstallException($"El paquete tiene más de {_limits.MaxEntries} archivos.");
        }

        long total = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.Length < 0 || entry.Length > _limits.MaxTotalBytes - total)
            {
                throw new PackageInstallException($"El paquete descomprimido ocupa más de {FormatBytes(_limits.MaxTotalBytes)}.");
            }

            total += entry.Length;
        }

        if (total > 16L << 20 && total / Math.Max(archiveBytes, 1) > _limits.MaxCompressionRatio)
        {
            throw new PackageInstallException($"El paquete está comprimido de forma sospechosa: {FormatBytes(archiveBytes)} que ocupan {FormatBytes(total)}.");
        }

        long? free = FreeSpace(destination);
        if (free < total + _limits.FreeSpaceReserve)
        {
            throw new PackageInstallException($"No hay espacio en el disco: el paquete necesita {FormatBytes(total)} y quedan {FormatBytes(free.Value)} libres.");
        }
    }

    /// <summary>Espacio libre en la unidad de <paramref name="path"/>, o <c>null</c> si no se puede saber (por ejemplo, en una ruta de red).</summary>
    private static long? FreeSpace(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Cuántas familias tipográficas trae el paquete y cuánto ocupan sus archivos (ya validados por el lector).</summary>
    private static (int Families, long Bytes) FontBytes(PackageManifest manifest, string directory)
    {
        int families = 0;
        long bytes = 0;
        foreach (PackageItem item in manifest.Items.Where(i => i.Kind == ItemKind.Font))
        {
            families++;
            foreach (string relative in item.Files ?? [])
            {
                if (SafePaths.TryNormalizeRelative(relative, out string? normalized))
                {
                    bytes += new FileInfo(Path.Combine(directory, normalized)).Length;
                }
            }
        }

        return (families, bytes);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        _ => $"{Math.Max(bytes, 0) / 1024.0:0.#} KB",
    };

    /// <summary>Las releases de GitHub suelen envolver todo en una carpeta: si el manifiesto está ahí y no en la raíz, se quita.</summary>
    private static string CommonWrapperFolder(ZipArchive zip)
    {
        if (zip.Entries.Any(e => string.Equals(e.FullName, PackageManifest.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            return "";
        }

        string? first = null;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            int slash = entry.FullName.IndexOfAny(['/', '\\']);
            if (slash <= 0)
            {
                return "";
            }

            string folder = entry.FullName[..(slash + 1)];
            if (first is null)
            {
                first = folder;
            }
            else if (!string.Equals(first, folder, StringComparison.Ordinal))
            {
                return "";
            }
        }

        return first is not null && zip.Entries.Any(e => string.Equals(e.FullName, first + PackageManifest.FileName, StringComparison.OrdinalIgnoreCase))
            ? first
            : "";
    }

    /// <summary>Copia sin pasar del tamaño declarado: un zip que miente sobre el tamaño no llena el disco.</summary>
    private static void CopyLimited(Stream source, Stream target, long declared, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            copied += read;
            if (copied > declared)
            {
                throw new PackageInstallException("Una entrada del paquete es más grande de lo que declara.");
            }

            target.Write(buffer, 0, read);
        }
    }

    private static bool TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
