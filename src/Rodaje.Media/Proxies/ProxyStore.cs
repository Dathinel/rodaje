using System.Text.Json;

namespace Rodaje.Media.Proxies;

/// <summary>Un proxy listo en el almacén.</summary>
public sealed record ProxyEntry(string ProxyPath, string MetadataPath, ProxyMetadata Metadata);

/// <summary>Qué hace <see cref="ProxyStore.Cleanup"/>.</summary>
public sealed record ProxyCleanupOptions
{
    /// <summary>Temporales sin escribir desde hace más de esto se consideran abandonados (un proceso que murió).</summary>
    public TimeSpan TemporaryMaxAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Borra los proxies cuyo origen ya no existe o cambió (relee 2 MiB por origen si su fecha y tamaño coinciden).</summary>
    public bool RemoveStale { get; init; } = true;

    /// <summary>Tamaño máximo del almacén en bytes; se borran primero los proxies usados hace más tiempo.</summary>
    public long? MaxTotalBytes { get; init; }

    /// <summary>Claves de origen que el presupuesto no puede borrar (por ejemplo, los medios del proyecto abierto).</summary>
    public IReadOnlySet<string>? ProtectedSourceKeys { get; init; }
}

/// <summary>Resultado de <see cref="ProxyStore.Cleanup"/>.</summary>
/// <param name="TemporaryFiles">Temporales abandonados borrados.</param>
/// <param name="Orphans">Proxies sin metadatos, metadatos sin proxy o metadatos ilegibles borrados.</param>
/// <param name="Stale">Proxies cuyo origen desapareció o cambió.</param>
/// <param name="Evicted">Proxies borrados para cumplir el presupuesto.</param>
/// <param name="BytesFreed">Bytes liberados en total.</param>
/// <param name="InUse">Archivos que no se pudieron borrar porque alguien los tiene abiertos.</param>
/// <param name="RemainingBytes">Bytes que ocupan los proxies que quedan.</param>
public sealed record ProxyCleanupReport(int TemporaryFiles, int Orphans, int Stale, int Evicted, long BytesFreed, int InUse, long RemainingBytes);

/// <summary>
/// Carpeta de proxies: <c>&lt;clave&gt;-&lt;ajustes&gt;.mp4</c> con su <c>&lt;clave&gt;-&lt;ajustes&gt;.json</c> al lado.
/// </summary>
/// <remarks>
/// Un proxy solo cuenta como listo si existen los dos archivos y el JSON coincide con la huella del origen, los
/// ajustes y el tamaño del proxy. Todo se escribe primero como temporal (con <c>.tmp</c> en el nombre) y se
/// renombra al final, así que un apagón nunca deja un proxy a medias que parezca bueno.
/// </remarks>
public sealed class ProxyStore
{
    public const string DirectoryEnvironmentVariable = EnvironmentVariables.Proxies;
    public const string ProxyExtension = ".mp4";
    public const string MetadataExtension = ".json";
    private const string TempMarker = ".tmp";

    private readonly TimeProvider _time;

    public ProxyStore(string? directory = null, TimeProvider? timeProvider = null)
    {
        Directory = Path.GetFullPath(string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary><c>RODAJE_PROXIES</c> si está definida; si no, <c>%LOCALAPPDATA%\Rodaje\proxies</c>.</summary>
    public static string DefaultDirectory
    {
        get
        {
            string? configured = EnvironmentVariables.Read(DirectoryEnvironmentVariable);
            return !string.IsNullOrWhiteSpace(configured)
                ? configured
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rodaje", "proxies");
        }
    }

    public string Directory { get; }

    public string ProxyPath(SourceFingerprint source, ProxySettings settings) => BasePath(source, settings) + ProxyExtension;

    public string MetadataPath(SourceFingerprint source, ProxySettings settings) => BasePath(source, settings) + MetadataExtension;

    /// <summary>El proxy de ese origen con esos ajustes, si está completo y sigue valiendo; marca su último uso.</summary>
    public ProxyEntry? TryGet(SourceFingerprint source, ProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ProxyEntry? entry = ReadEntry(MetadataPath(source, settings));
        if (entry is null || entry.Metadata.SourceKey != source.Key || entry.Metadata.SettingsTag != settings.Tag)
        {
            return null;
        }

        try
        {
            File.SetLastAccessTimeUtc(entry.ProxyPath, _time.GetUtcNow().UtcDateTime);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return entry;
    }

    /// <summary>Ruta nueva y única para que ffmpeg escriba un proxy; crea la carpeta si hace falta.</summary>
    public string CreateTemporaryPath(SourceFingerprint source, ProxySettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return $"{BasePath(source, settings)}.{Guid.NewGuid():N}{TempMarker}{ProxyExtension}";
    }

    /// <summary>
    /// Mueve el proxy terminado a su nombre definitivo y después escribe sus metadatos (temporal y renombrado).
    /// </summary>
    /// <remarks>
    /// Si ya hay un proxy válido para ese origen y esos ajustes (otra instancia lo terminó antes), se descarta el
    /// temporal y se devuelve el existente. Si el definitivo está abierto y no se puede reemplazar, también se
    /// devuelve el existente cuando sigue siendo válido. Mientras el JSON viejo no se reemplaza, no describe al
    /// proxy nuevo salvo que coincida su tamaño, y el nuevo es de la misma huella y los mismos ajustes.
    /// </remarks>
    public ProxyEntry Commit(string temporaryProxyPath, ProxyMetadata metadata, ProxySettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryProxyPath);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(settings);
        var source = new SourceFingerprint(metadata.SourcePath, metadata.SourceSize, metadata.SourceLastWriteUtc, metadata.SourceKey);
        string proxyPath = ProxyPath(source, settings);
        string metadataPath = MetadataPath(source, settings);
        if (TryGet(source, settings) is { } existing)
        {
            TryDelete(temporaryProxyPath, out _);
            return existing;
        }

        ProxyMetadata final = metadata with { ProxyBytes = new FileInfo(temporaryProxyPath).Length };
        try
        {
            File.Move(temporaryProxyPath, proxyPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (TryGet(source, settings) is { } current)
            {
                TryDelete(temporaryProxyPath, out _);
                return current;
            }

            throw;
        }

        string temporaryMetadata = $"{metadataPath}.{Guid.NewGuid():N}{TempMarker}";
        try
        {
            using (var stream = new FileStream(temporaryMetadata, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, final, ProxyJsonContext.Default.ProxyMetadata);
            }

            File.Move(temporaryMetadata, metadataPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryMetadata);
        }

        try
        {
            File.SetLastAccessTimeUtc(proxyPath, _time.GetUtcNow().UtcDateTime);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new ProxyEntry(proxyPath, metadataPath, final);
    }

    /// <summary>Borra el proxy y sus metadatos; devuelve <c>false</c> (sin borrar nada) si alguien lo tiene abierto.</summary>
    public static bool Delete(ProxyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return TryDelete(entry.ProxyPath, out _) && TryDelete(entry.MetadataPath, out _);
    }

    /// <summary>Proxies completos y legibles, sin comprobar sus orígenes.</summary>
    public IReadOnlyList<ProxyEntry> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(Directory, "*" + MetadataExtension)
            .Where(p => !IsTemporary(p))
            .Select(ReadEntry)
            .OfType<ProxyEntry>()
            .ToList();
    }

    /// <summary>Bytes que ocupa todo lo que hay en la carpeta, temporales incluidos.</summary>
    public long TotalBytes() => System.IO.Directory.Exists(Directory)
        ? new DirectoryInfo(Directory).EnumerateFiles().Sum(f => f.Length)
        : 0;

    /// <summary>
    /// Borra temporales abandonados, huérfanos, proxies de orígenes que cambiaron o desaparecieron y, si hay
    /// presupuesto, los usados hace más tiempo hasta caber. Lo que está abierto se salta y se cuenta en <c>InUse</c>.
    /// </summary>
    public ProxyCleanupReport Cleanup(ProxyCleanupOptions? options = null)
    {
        options ??= new ProxyCleanupOptions();
        if (!System.IO.Directory.Exists(Directory))
        {
            return new ProxyCleanupReport(0, 0, 0, 0, 0, 0, 0);
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        int temporary = 0, orphans = 0, stale = 0, evicted = 0, inUse = 0;
        long freed = 0;
        void Remove(string path, ref int counter)
        {
            if (TryDelete(path, out long bytes))
            {
                counter++;
                freed += bytes;
            }
            else
            {
                inUse++;
            }
        }

        // El proxy primero: si está abierto no se borra nada y sus metadatos siguen describiéndolo.
        bool RemoveEntry(ProxyEntry entry, ref int counter)
        {
            if (!TryDelete(entry.ProxyPath, out long bytes))
            {
                inUse++;
                return false;
            }

            TryDelete(entry.MetadataPath, out long jsonBytes);
            counter++;
            freed += bytes + jsonBytes;
            return true;
        }

        var files = new DirectoryInfo(Directory).EnumerateFiles().ToList();
        foreach (FileInfo file in files.Where(f => IsTemporary(f.Name)))
        {
            if (now - file.LastWriteTimeUtc > options.TemporaryMaxAge)
            {
                Remove(file.FullName, ref temporary);
            }
        }

        var entries = new List<ProxyEntry>();
        var proxies = files.Where(f => !IsTemporary(f.Name) && f.Name.EndsWith(ProxyExtension, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (FileInfo json in files.Where(f => !IsTemporary(f.Name) && f.Name.EndsWith(MetadataExtension, StringComparison.OrdinalIgnoreCase)))
        {
            ProxyEntry? entry = ReadEntry(json.FullName);
            if (entry is null)
            {
                Remove(json.FullName, ref orphans);
                continue;
            }

            proxies.Remove(entry.ProxyPath);
            if (options.RemoveStale && !SourceStillMatches(entry.Metadata))
            {
                RemoveEntry(entry, ref stale);
                continue;
            }

            entries.Add(entry);
        }

        foreach (string proxy in proxies)
        {
            Remove(proxy, ref orphans);
        }

        long remaining = entries.Sum(e => SizeOf(e.ProxyPath));
        if (options.MaxTotalBytes is { } budget && remaining > budget)
        {
            IReadOnlySet<string> keep = options.ProtectedSourceKeys ?? new HashSet<string>();
            foreach (ProxyEntry entry in entries.Where(e => !keep.Contains(e.Metadata.SourceKey)).OrderBy(e => LastUsed(e.ProxyPath)))
            {
                if (remaining <= budget)
                {
                    break;
                }

                long size = SizeOf(entry.ProxyPath);
                if (RemoveEntry(entry, ref evicted))
                {
                    remaining -= size;
                }
            }
        }

        return new ProxyCleanupReport(temporary, orphans, stale, evicted, freed, inUse, remaining);
    }

    internal static bool IsTemporary(string path) =>
        Path.GetFileName(path).Contains(TempMarker, StringComparison.OrdinalIgnoreCase);

    private string BasePath(SourceFingerprint source, ProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        return Path.Combine(Directory, $"{source.Key}-{settings.Tag}");
    }

    private static ProxyEntry? ReadEntry(string metadataPath)
    {
        string proxyPath = Path.ChangeExtension(metadataPath, ProxyExtension);
        ProxyMetadata? metadata;
        try
        {
            if (!File.Exists(metadataPath) || !File.Exists(proxyPath))
            {
                return null;
            }

            using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            metadata = JsonSerializer.Deserialize(stream, ProxyJsonContext.Default.ProxyMetadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        if (metadata is null || metadata.FormatVersion != ProxySettings.FormatVersion
            || Path.GetFileNameWithoutExtension(metadataPath) != $"{metadata.SourceKey}-{metadata.SettingsTag}"
            || SizeOf(proxyPath) != metadata.ProxyBytes)
        {
            return null;
        }

        return new ProxyEntry(proxyPath, metadataPath, metadata);
    }

    private static bool SourceStillMatches(ProxyMetadata metadata)
    {
        var info = new FileInfo(metadata.SourcePath);
        if (!info.Exists || info.Length != metadata.SourceSize || info.LastWriteTimeUtc != metadata.SourceLastWriteUtc)
        {
            return false;
        }

        try
        {
            return SourceFingerprint.Compute(metadata.SourcePath).Key == metadata.SourceKey;
        }
        catch (IOException)
        {
            // Si el origen no se puede leer ahora (bloqueado, disco desconectado a medias) no se borra su proxy.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static long SizeOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : -1;
    }

    // TryGet y Commit fijan la fecha de último acceso a mano: no depende de que NTFS la actualice.
    private static DateTime LastUsed(string path) => new FileInfo(path).LastAccessTimeUtc;

    private static bool TryDelete(string path, out long bytes)
    {
        bytes = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return true;
            }

            bytes = info.Length;
            info.Delete();
            return true;
        }
        catch (IOException)
        {
            bytes = 0;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            bytes = 0;
            return false;
        }
    }
}
