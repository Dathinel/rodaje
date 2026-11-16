using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Media.RenderCache;

/// <summary>
/// DTO interno para serializar los metadatos de un fragmento de render en caché a JSON sin reflexión (Native AOT).
/// </summary>
internal sealed record RenderCacheMetadataDto
{
    public int FormatVersion { get; init; } = RenderCacheStore.CurrentFormatVersion;

    public int Width { get; init; }

    public int Height { get; init; }

    public long FrameRateNum { get; init; }

    public long FrameRateDen { get; init; }

    public SequenceColorSpace ColorSpace { get; init; }

    public string Codec { get; init; } = "";

    public bool PlaybackOnly { get; init; }

    public List<string> Frames { get; init; } = [];

    public long MovieBytes { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    NewLine = "\n",
    RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    Converters = [typeof(JsonStringEnumConverter<SequenceColorSpace>)])]
[JsonSerializable(typeof(RenderCacheMetadataDto))]
internal sealed partial class RenderCacheJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Almacén de fragmentos de render en caché (<c>.mov</c> y <c>.json</c>) con índice en memoria y limpieza atómica.
/// </summary>
public sealed class RenderCacheStore : IRenderCacheStore
{
    /// <summary>Variable de entorno que define la carpeta del almacén (<c>RODAJE_RENDER</c>).</summary>
    public const string DirectoryEnvironmentVariable = EnvironmentVariables.Render;

    /// <summary>Extensión del archivo de video de un fragmento.</summary>
    public const string MovieExtension = ".mov";

    /// <summary>Extensión del archivo de metadatos de un fragmento.</summary>
    public const string MetadataExtension = ".json";

    /// <summary>Versión actual del formato del archivo <c>.json</c> de metadatos.</summary>
    public const int CurrentFormatVersion = 1;

    private const string TempMarker = ".tmp";

    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<RenderCacheKey, List<RenderCacheHit>> _index = new();
    private readonly Dictionary<string, RenderCacheFragment> _fragments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastUsed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Crea un almacén en <paramref name="directory"/> o en <see cref="DefaultDirectory"/>, usando
    /// <paramref name="timeProvider"/> para controlar el reloj.
    /// </summary>
    /// <param name="directory">Carpeta del almacén; si es nula o vacía, usa la predeterminada.</param>
    /// <param name="timeProvider">Proveedor de tiempo opcional (para pruebas).</param>
    public RenderCacheStore(string? directory = null, TimeProvider? timeProvider = null)
    {
        Directory = Path.GetFullPath(string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory);
        _time = timeProvider ?? TimeProvider.System;
        Load();
    }

    /// <summary><c>RODAJE_RENDER</c> si está definida; si no, <c>%LOCALAPPDATA%\Rodaje\render</c>.</summary>
    public static string DefaultDirectory
    {
        get
        {
            string? configured = EnvironmentVariables.Read(DirectoryEnvironmentVariable);
            return !string.IsNullOrWhiteSpace(configured)
                ? configured
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rodaje", "render");
        }
    }

    /// <summary>Para las pruebas: corre entre enumerar la carpeta y reconciliar el índice en <see cref="Cleanup"/>.</summary>
    internal Action? AfterEnumerate { get; set; }

    /// <summary>Carpeta en disco donde se guardan los fragmentos.</summary>
    public string Directory { get; }

    /// <summary>Cuántas huellas distintas tiene el índice en memoria.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>
    /// El cuadro de <paramref name="key"/>, si está en el índice. Con varios fragmentos para la misma huella se prefiere
    /// el mismo fragmento de <paramref name="previous"/> con el cuadro siguiente, después cualquiera del mismo fragmento,
    /// y si no, el que deja más cuadros por delante en su fragmento.
    /// </summary>
    /// <param name="key">Huella del cuadro.</param>
    /// <param name="previous">Acierto anterior, para favorecer continuidad.</param>
    /// <param name="hit">Acierto encontrado.</param>
    /// <returns><c>true</c> si se encontró el cuadro; en caso contrario, <c>false</c>.</returns>
    public bool TryGet(RenderCacheKey key, RenderCacheHit? previous, out RenderCacheHit hit)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(key, out List<RenderCacheHit>? hits) || hits.Count == 0)
            {
                hit = default;
                return false;
            }

            if (previous is { } prev)
            {
                // 1. Mismo fragmento de previous con FrameIndex == prev.FrameIndex + 1
                for (int i = 0; i < hits.Count; i++)
                {
                    RenderCacheHit candidate = hits[i];
                    if (string.Equals(candidate.Fragment.MoviePath, prev.Fragment.MoviePath, StringComparison.OrdinalIgnoreCase)
                        && candidate.FrameIndex == prev.FrameIndex + 1)
                    {
                        hit = candidate;
                        Touch(hit.Fragment);
                        return true;
                    }
                }

                // 2. Mismo fragmento de previous
                for (int i = 0; i < hits.Count; i++)
                {
                    RenderCacheHit candidate = hits[i];
                    if (string.Equals(candidate.Fragment.MoviePath, prev.Fragment.MoviePath, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = candidate;
                        Touch(hit.Fragment);
                        return true;
                    }
                }
            }

            // 3. El que deja más cuadros por delante: un fragmento viejo de pocos cuadros no tapa a uno continuo.
            hit = hits[0];
            for (int i = 1; i < hits.Count; i++)
            {
                if (hits[i].Fragment.Metadata.Frames.Count - hits[i].FrameIndex > hit.Fragment.Metadata.Frames.Count - hit.FrameIndex)
                {
                    hit = hits[i];
                }
            }

            Touch(hit.Fragment);
            return true;
        }
    }

    /// <summary>
    /// Ruta única temporal en la carpeta para escribir el <c>.mov</c> de un fragmento nuevo.
    /// </summary>
    /// <returns>Ruta absoluta del archivo temporal.</returns>
    public string CreateTemporaryPath()
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Path.Combine(Directory, $"{Guid.NewGuid():N}{TempMarker}{MovieExtension}");
    }

    /// <summary>
    /// Publica un fragmento terminado: valida el temporal y su tamaño, renombra el <c>.mov</c>, escribe el
    /// <c>.json</c> de forma atómica y lo incorpora al índice en memoria.
    /// </summary>
    /// <param name="temporaryMoviePath">Ruta del archivo de video temporal.</param>
    /// <param name="metadata">Metadatos del fragmento.</param>
    /// <returns>El fragmento publicado.</returns>
    public RenderCacheFragment Commit(string temporaryMoviePath, RenderCacheFragmentMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryMoviePath);
        ArgumentNullException.ThrowIfNull(metadata);

        var tempInfo = new FileInfo(temporaryMoviePath);
        if (!tempInfo.Exists)
        {
            throw new ArgumentException($"El archivo temporal '{temporaryMoviePath}' no existe.", nameof(temporaryMoviePath));
        }

        if (tempInfo.Length != metadata.MovieBytes)
        {
            throw new ArgumentException(
                $"El tamaño del archivo temporal ({tempInfo.Length}) no coincide con MovieBytes ({metadata.MovieBytes}).",
                nameof(temporaryMoviePath));
        }

        if (metadata.Frames is null || metadata.Frames.Count == 0)
        {
            throw new ArgumentException("El fragmento debe tener al menos un cuadro.", nameof(metadata));
        }

        if (metadata.Width <= 0 || metadata.Height <= 0)
        {
            throw new ArgumentException("Las dimensiones deben ser positivas.", nameof(metadata));
        }

        if (metadata.FrameRate.Num <= 0 || metadata.FrameRate.Den <= 0)
        {
            throw new ArgumentException("La tasa de cuadros debe ser positiva.", nameof(metadata));
        }

        System.IO.Directory.CreateDirectory(Directory);
        string name;
        string moviePath;
        string metadataPath;
        do
        {
            name = Guid.NewGuid().ToString("N");
            moviePath = Path.Combine(Directory, name + MovieExtension);
            metadataPath = Path.Combine(Directory, name + MetadataExtension);
        } while (File.Exists(moviePath) || File.Exists(metadataPath));

        // 1. Primero el .mov definitivo para que un corte de luz deje un .mov huérfano y nunca un .json roto
        File.Move(temporaryMoviePath, moviePath);

        // 2. Escribir el .json a un temporal y renombrar
        string tempMetadata = Path.Combine(Directory, $"{name}.{Guid.NewGuid():N}{TempMarker}{MetadataExtension}");
        var dto = new RenderCacheMetadataDto
        {
            FormatVersion = CurrentFormatVersion,
            Width = metadata.Width,
            Height = metadata.Height,
            FrameRateNum = metadata.FrameRate.Num,
            FrameRateDen = metadata.FrameRate.Den,
            ColorSpace = metadata.ColorSpace,
            Codec = metadata.Codec ?? "",
            PlaybackOnly = metadata.PlaybackOnly,
            Frames = metadata.Frames.Select(k => k.ToString()).ToList(),
            MovieBytes = metadata.MovieBytes,
        };

        try
        {
            using (var stream = new FileStream(tempMetadata, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, dto, RenderCacheJsonContext.Default.RenderCacheMetadataDto);
            }

            File.Move(tempMetadata, metadataPath);
        }
        finally
        {
            if (File.Exists(tempMetadata))
            {
                try
                {
                    File.Delete(tempMetadata);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        var fragment = new RenderCacheFragment(moviePath, metadataPath, metadata);

        lock (_lock)
        {
            AddFragmentToMemory(fragment);
            _lastUsed[moviePath] = _time.GetUtcNow().UtcDateTime;
        }

        try
        {
            File.SetLastAccessTimeUtc(moviePath, _time.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return fragment;
    }

    /// <summary>
    /// Retira el fragmento del índice en memoria (todas sus huellas). No borra archivos en disco.
    /// </summary>
    /// <param name="fragment">Fragmento que se olvida.</param>
    public void Forget(RenderCacheFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        lock (_lock)
        {
            ForgetCore(fragment);
        }
    }

    /// <summary>Saca del índice el fragmento de <paramref name="moviePath"/> (uno que no se pudo leer); <c>false</c> si no estaba.</summary>
    public bool Forget(string moviePath)
    {
        ArgumentNullException.ThrowIfNull(moviePath);
        lock (_lock)
        {
            if (!_fragments.TryGetValue(Path.GetFullPath(moviePath), out RenderCacheFragment? fragment))
            {
                return false;
            }

            ForgetCore(fragment);
            return true;
        }
    }

    /// <summary>
    /// Suma de bytes de los archivos <c>.mov</c> y <c>.json</c> en la carpeta (0 si no existe).
    /// </summary>
    /// <returns>Bytes totales ocupados.</returns>
    public long TotalBytes()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return 0;
        }

        try
        {
            return new DirectoryInfo(Directory).EnumerateFiles()
                .Where(f => f.Name.EndsWith(MovieExtension, StringComparison.OrdinalIgnoreCase)
                         || f.Name.EndsWith(MetadataExtension, StringComparison.OrdinalIgnoreCase))
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Borra temporales antiguos, huérfanos y fragmentos inválidos. Si se supera el límite de bytes, desaloja
    /// los fragmentos menos recientemente usados respetando los protegidos.
    /// </summary>
    /// <param name="options">Opciones de limpieza.</param>
    /// <returns>Informe de la limpieza efectuada.</returns>
    public RenderCacheCleanupReport Cleanup(RenderCacheCleanupOptions? options = null)
    {
        options ??= new RenderCacheCleanupOptions();
        if (!System.IO.Directory.Exists(Directory))
        {
            return new RenderCacheCleanupReport(0, 0, 0, 0, 0, 0);
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        int temporary = 0, orphans = 0, evicted = 0, protectedCount = 0;
        long freed = 0;

        // Solo se reconcilia lo que ya estaba en el índice antes de enumerar: un Commit de otro hilo durante la limpieza
        // publica archivos que la enumeración no vio y no debe olvidarse.
        HashSet<string> indexed;
        lock (_lock)
        {
            indexed = [.. _fragments.Keys];
        }

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(Directory).EnumerateFiles().ToList();
            AfterEnumerate?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RenderCacheCleanupReport(0, 0, 0, 0, 0, TotalBytes());
        }

        // 1. Borrar temporales abandonados (más viejos que TemporaryMaxAge)
        foreach (FileInfo file in files.Where(f => IsTemporary(f.Name)))
        {
            if (now - file.LastWriteTimeUtc > options.TemporaryMaxAge)
            {
                if (TryDelete(file.FullName, out long bytes))
                {
                    temporary++;
                    freed += bytes;
                }
            }
        }

        // 2. Clasificar .mov y .json no temporales
        var movies = files
            .Where(f => !IsTemporary(f.Name) && f.Name.EndsWith(MovieExtension, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.FullName, f => f, StringComparer.OrdinalIgnoreCase);

        var jsons = files
            .Where(f => !IsTemporary(f.Name) && f.Name.EndsWith(MetadataExtension, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var validFragments = new List<RenderCacheFragment>();

        foreach (FileInfo jsonFile in jsons)
        {
            string moviePath = Path.ChangeExtension(jsonFile.FullName, MovieExtension);
            bool hasMovie = movies.TryGetValue(moviePath, out FileInfo? movieFile);

            if (!hasMovie || movieFile is null)
            {
                // .json sin .mov (huérfano): solo borrar si tiene más de TemporaryMaxAge
                if (now - jsonFile.LastWriteTimeUtc > options.TemporaryMaxAge)
                {
                    if (TryDelete(jsonFile.FullName, out long bytes))
                    {
                        orphans++;
                        freed += bytes;
                    }
                }
                continue;
            }

            RenderCacheFragment? fragment = ReadFragment(jsonFile.FullName, movieFile);
            if (fragment is null)
            {
                // .json inválido: se borra
                if (TryDelete(jsonFile.FullName, out long jsonBytes))
                {
                    orphans++;
                    freed += jsonBytes;
                }

                // El .mov asociado ahora no tiene .json válido (huérfano): borrar si tiene más de TemporaryMaxAge
                if (now - movieFile.LastWriteTimeUtc > options.TemporaryMaxAge)
                {
                    if (TryDelete(movieFile.FullName, out long movBytes))
                    {
                        orphans++;
                        freed += movBytes;
                        movies.Remove(moviePath);
                    }
                }
                continue;
            }

            movies.Remove(moviePath);
            validFragments.Add(fragment);
        }

        // 3. .mov sin .json (huérfanos): solo borrar si tienen más de TemporaryMaxAge
        foreach (FileInfo orphanMovie in movies.Values)
        {
            if (now - orphanMovie.LastWriteTimeUtc > options.TemporaryMaxAge)
            {
                if (TryDelete(orphanMovie.FullName, out long bytes))
                {
                    orphans++;
                    freed += bytes;
                }
            }
        }

        // 4. Asegurar que lo borrado salga del índice
        lock (_lock)
        {
            var validPaths = validFragments.Select(f => f.MoviePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var toForget = _fragments.Values.Where(f => indexed.Contains(f.MoviePath) && !validPaths.Contains(f.MoviePath)).ToList();
            foreach (RenderCacheFragment f in toForget)
            {
                ForgetCore(f);
            }
        }

        // 5. Tope de disco (MaxTotalBytes)
        long remaining = TotalBytes();
        if (options.MaxTotalBytes is { } budget && remaining > budget)
        {
            var ordered = validFragments
                .OrderBy(f => GetLastUsed(f))
                .ToList();

            foreach (RenderCacheFragment fragment in ordered)
            {
                if (remaining <= budget)
                {
                    break;
                }

                if (options.ProtectedKeys is { Count: > 0 } protectedKeys
                    && fragment.Metadata.Frames.Any(k => protectedKeys.Contains(k)))
                {
                    protectedCount++;
                    continue;
                }

                if (!TryDelete(fragment.MoviePath, out long movBytes))
                {
                    continue;
                }

                TryDelete(fragment.MetadataPath, out long jsonBytes);
                evicted++;
                freed += movBytes + jsonBytes;
                remaining -= (movBytes + jsonBytes);

                lock (_lock)
                {
                    ForgetCore(fragment);
                }
            }
        }

        long finalRemaining = TotalBytes();
        return new RenderCacheCleanupReport(temporary, orphans, evicted, freed, protectedCount, finalRemaining);
    }

    internal static bool IsTemporary(string path) =>
        Path.GetFileName(path).Contains(TempMarker, StringComparison.OrdinalIgnoreCase);

    private void Load()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(Directory).GetFiles("*" + MetadataExtension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        lock (_lock)
        {
            _index.Clear();
            _fragments.Clear();

            foreach (FileInfo jsonFile in files)
            {
                if (IsTemporary(jsonFile.Name))
                {
                    continue;
                }

                RenderCacheFragment? fragment = ReadFragment(jsonFile.FullName);
                if (fragment is not null)
                {
                    AddFragmentToMemory(fragment);
                }
            }
        }
    }

    private void AddFragmentToMemory(RenderCacheFragment fragment)
    {
        _fragments[fragment.MoviePath] = fragment;
        for (int i = 0; i < fragment.Metadata.Frames.Count; i++)
        {
            RenderCacheKey key = fragment.Metadata.Frames[i];
            if (!_index.TryGetValue(key, out List<RenderCacheHit>? hits))
            {
                hits = [];
                _index[key] = hits;
            }

            hits.Add(new RenderCacheHit(fragment, i));
        }
    }

    private void ForgetCore(RenderCacheFragment fragment)
    {
        _fragments.Remove(fragment.MoviePath);
        _lastUsed.Remove(fragment.MoviePath);

        for (int i = 0; i < fragment.Metadata.Frames.Count; i++)
        {
            RenderCacheKey key = fragment.Metadata.Frames[i];
            if (_index.TryGetValue(key, out List<RenderCacheHit>? hits))
            {
                hits.RemoveAll(h => string.Equals(h.Fragment.MoviePath, fragment.MoviePath, StringComparison.OrdinalIgnoreCase));
                if (hits.Count == 0)
                {
                    _index.Remove(key);
                }
            }
        }
    }

    private void Touch(RenderCacheFragment fragment)
    {
        DateTime now = _time.GetUtcNow().UtcDateTime;
        bool recent = _lastUsed.TryGetValue(fragment.MoviePath, out DateTime last) && now - last < TouchInterval;
        _lastUsed[fragment.MoviePath] = now;

        // TryGet se llama por cada cuadro de la secuencia: escribir la fecha del archivo cada vez costaría segundos.
        if (recent)
        {
            return;
        }

        try
        {
            File.SetLastAccessTimeUtc(fragment.MoviePath, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private DateTime GetLastUsed(RenderCacheFragment fragment)
    {
        lock (_lock)
        {
            if (_lastUsed.TryGetValue(fragment.MoviePath, out DateTime lastUsed))
            {
                return lastUsed;
            }
        }

        try
        {
            var info = new FileInfo(fragment.MoviePath);
            if (info.Exists)
            {
                // Touch deja el último uso en la fecha de acceso: así sobrevive a reabrir el almacén.
                return info.LastAccessTimeUtc;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return DateTime.MinValue;
    }

    private static RenderCacheFragment? ReadFragment(string metadataPath, FileInfo? movieInfo = null)
    {
        string moviePath = Path.ChangeExtension(metadataPath, MovieExtension);
        try
        {
            if (!File.Exists(metadataPath))
            {
                return null;
            }

            movieInfo ??= new FileInfo(moviePath);
            if (!movieInfo.Exists)
            {
                return null;
            }

            RenderCacheMetadataDto? dto;
            using (var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                dto = JsonSerializer.Deserialize(stream, RenderCacheJsonContext.Default.RenderCacheMetadataDto);
            }

            if (dto is null)
            {
                return null;
            }

            if (dto.FormatVersion != CurrentFormatVersion)
            {
                return null;
            }

            if (dto.Width <= 0 || dto.Height <= 0)
            {
                return null;
            }

            if (dto.FrameRateNum <= 0 || dto.FrameRateDen <= 0)
            {
                return null;
            }

            if (dto.Frames is null || dto.Frames.Count == 0)
            {
                return null;
            }

            if (movieInfo.Length != dto.MovieBytes)
            {
                return null;
            }

            var frames = new RenderCacheKey[dto.Frames.Count];
            for (int i = 0; i < dto.Frames.Count; i++)
            {
                if (!RenderCacheKey.TryParse(dto.Frames[i], out frames[i]))
                {
                    return null;
                }
            }

            var metadata = new RenderCacheFragmentMetadata(
                dto.Width,
                dto.Height,
                new Rational(dto.FrameRateNum, dto.FrameRateDen),
                dto.ColorSpace,
                dto.Codec ?? "",
                dto.PlaybackOnly,
                frames,
                dto.MovieBytes);

            return new RenderCacheFragment(moviePath, metadataPath, metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bytes = 0;
            return false;
        }
    }
}
