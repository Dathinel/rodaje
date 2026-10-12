using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.App.Views.Library;

/// <summary>
/// Guarda las carpetas y los favoritos de la biblioteca en la carpeta de datos de Rodaje, un archivo por proyecto
/// (por su id y la ruta de su <c>.rodaje</c>, ver <see cref="KeyFor"/>), no dentro del <c>.rodaje</c>: organizan la
/// vista y no cambian el video, así que el formato del proyecto no sube de versión. Un proyecto copiado a otro PC llega
/// sin sus carpetas (todos los medios quedan en la raíz).
/// </summary>
public sealed class LibraryOrganizationStore
{
    /// <summary>Subcarpeta de <c>%LOCALAPPDATA%\Rodaje</c> donde quedan los archivos.</summary>
    public const string FolderName = "biblioteca";

    public const int CurrentVersion = 1;

    /// <summary>Nombres que Windows reserva para dispositivos, también con extensión («con.json» no es un archivo).</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly Dictionary<string, string> _lastWritten = new(StringComparer.Ordinal);

    /// <summary>
    /// Archivos de claves que se cargaron por herencia (sin archivo propio, desde el de <c>fallbackKey</c>). Para ellos
    /// una organización vacía sí se escribe: si no, la próxima vez se volvería a heredar lo que se borró.
    /// </summary>
    private readonly HashSet<string> _inherited = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public LibraryOrganizationStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = System.IO.Path.GetFullPath(directory);
    }

    public string Directory { get; }

    /// <summary>
    /// Clave del archivo de organización. Copiar el <c>.rodaje</c> conserva el <c>Project.Id</c>, así que el id solo no
    /// distingue el original de la copia: con él, las dos compartían archivo y lo que se ordenaba en una cambiaba la otra.
    /// Por eso, una vez guardado, la clave es el id más la ruta del archivo (sin mirar mayúsculas, como Windows). Un
    /// proyecto que nunca se guardó usa solo el id, que es nuevo en cada proyecto.
    /// </summary>
    public static string KeyFor(string projectId, string? projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        return string.IsNullOrEmpty(projectFilePath)
            ? projectId
            : projectId + "|" + projectFilePath.Replace('/', '\\').ToUpperInvariant();
    }

    /// <summary>
    /// Archivo de la clave: la clave si es un nombre seguro (letras, cifras, «-» y «_», y no un nombre reservado de
    /// Windows como <c>CON</c> o <c>nul</c>), o un resumen de ella si no.
    /// </summary>
    public string PathFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        bool safe = key.Length is > 0 and <= 80 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && !ReservedNames.Contains(key);
        string name = safe
            ? key
            : "h-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return System.IO.Path.Combine(Directory, name + ".json");
    }

    /// <summary>
    /// Lee la organización de <paramref name="key"/>; si aún no tiene archivo, parte de la de
    /// <paramref name="fallbackKey"/> (la clave por id de antes, o la del proyecto antes de guardarlo por primera vez), sin
    /// tocar ese archivo. Así nadie pierde sus carpetas al actualizar, y una copia empieza con las del original pero
    /// desde ahí guarda las suyas aparte. Para que la copia parta del estado actual y no de uno viejo, la vista guarda con
    /// <see cref="PrepareWithTemplate"/>, que mantiene al día el archivo por id.
    /// </summary>
    public LibraryOrganization Load(string key, string? fallbackKey)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (fallbackKey is null || fallbackKey == key)
        {
            return Load(key);
        }

        if (Exists(key))
        {
            return Load(key);
        }

        if (Exists(fallbackKey))
        {
            lock (_lock)
            {
                _inherited.Add(PathFor(key));
            }
        }

        return Load(fallbackKey);
    }

    private bool Exists(string key)
    {
        try
        {
            return File.Exists(PathFor(key));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lee la organización de la clave (ver <see cref="KeyFor"/>). Sin archivo, o si estaba dañado (que se aparta con
    /// «.dañado») o es de una versión más nueva de Rodaje (que se aparta con «.vN», para que guardar no la pise), empieza
    /// vacía: en el peor caso los medios se ven todos en la raíz, nunca se pierde un medio.
    /// </summary>
    public LibraryOrganization Load(string key)
    {
        string path = PathFor(key);
        string text;
        try
        {
            if (!File.Exists(path))
            {
                return new LibraryOrganization();
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LibraryOrganization();
        }

        LibraryFile? file;
        try
        {
            file = JsonSerializer.Deserialize(text, LibraryJsonContext.Default.LibraryFile);
        }
        catch (JsonException)
        {
            file = null;
        }

        if (file is null || file.Version > CurrentVersion)
        {
            SetAside(path, file is null ? "dañado" : $"v{file.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return new LibraryOrganization();
        }

        lock (_lock)
        {
            _lastWritten[path] = text;
        }

        return LibraryOrganization.From(
            file.Folders?.Select(f => new LibraryFolder { Id = f?.Id ?? "", Name = f?.Name ?? "", ParentId = f?.Parent }),
            file.Media,
            file.Favorites);
    }

    /// <summary>
    /// Escribe solo si cambió algo; una organización vacía sin archivo previo no crea archivo, salvo que la clave se haya
    /// cargado por herencia (ver <see cref="Load(string, string?)"/>).
    /// </summary>
    public void Save(string key, LibraryOrganization organization)
    {
        if (Prepare(key, organization) is { } request)
        {
            Write(request);
        }
    }

    /// <summary>Como <see cref="Save(string, LibraryOrganization)"/>, y además actualiza la plantilla (ver <see cref="PrepareWithTemplate"/>).</summary>
    public void Save(string key, string? templateKey, LibraryOrganization organization)
    {
        foreach (LibrarySaveRequest request in PrepareWithTemplate(key, templateKey, organization))
        {
            Write(request);
        }
    }

    /// <summary>
    /// Lo que hay que escribir para <paramref name="key"/> y, si es otra, también para <paramref name="templateKey"/> (la
    /// clave por id). El archivo por id es la plantilla de la que parte una copia del <c>.rodaje</c> que aún no tiene
    /// archivo propio; si solo se escribiera la clave con ruta, quedaría congelado en un estado viejo y la copia no
    /// heredaría el estado actual del proyecto. Se escriben en este orden.
    /// </summary>
    public IReadOnlyList<LibrarySaveRequest> PrepareWithTemplate(string key, string? templateKey, LibraryOrganization organization)
    {
        ArgumentNullException.ThrowIfNull(key);
        List<LibrarySaveRequest> requests = [];
        if (Prepare(key, organization) is { } own)
        {
            requests.Add(own);
        }

        if (templateKey is not null && templateKey != key && Prepare(templateKey, organization) is { } template)
        {
            requests.Add(template);
        }

        return requests;
    }

    /// <summary>
    /// Lo que hay que escribir, o <c>null</c> si el archivo ya tiene esto. Toma una foto de la organización (va en el
    /// hilo que la edita); <see cref="Write"/> hace la escritura, que puede ir en otro hilo.
    /// </summary>
    public LibrarySaveRequest? Prepare(string key, LibraryOrganization organization)
    {
        ArgumentNullException.ThrowIfNull(organization);
        string path = PathFor(key);
        List<LibraryFolderEntry?> folders = [.. organization.Folders.Select(f => new LibraryFolderEntry { Id = f.Id, Name = f.Name, Parent = organization.FindFolder(f.ParentId)?.Id })];
        Dictionary<string, string> media = organization.MediaFolders.Where(p => organization.FindFolder(p.Value) is not null)
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        List<string> favorites = [.. organization.Favorites.Order(StringComparer.Ordinal)];
        var file = new LibraryFile { Version = CurrentVersion, Folders = folders, Media = media, Favorites = favorites };
        string json = JsonSerializer.Serialize(file, LibraryJsonContext.Default.LibraryFile);
        bool inherited;
        lock (_lock)
        {
            if (_lastWritten.TryGetValue(path, out string? last) && last == json)
            {
                return null;
            }

            inherited = _inherited.Contains(path);
        }

        // Vacía y heredada sí se escribe: si no, al abrir otra vez se heredaría de nuevo lo que se borró.
        return new LibrarySaveRequest(path, json, Empty: folders.Count == 0 && favorites.Count == 0 && !inherited);
    }

    /// <summary>
    /// Escribe a un temporal con <c>WriteThrough</c> y lo pone en su lugar. Va fuera del hilo de la interfaz: vaciar al
    /// disco puede tardar. Las escrituras de una misma clave deben ir en orden (una tras otra).
    /// </summary>
    public void Write(LibrarySaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_lock)
        {
            if (_lastWritten.TryGetValue(request.Path, out string? last) && last == request.Json)
            {
                return;
            }
        }

        if (request.Empty && !File.Exists(request.Path))
        {
            return;
        }

        System.IO.Directory.CreateDirectory(Directory);
        string temp = request.Path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(request.Json);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, request.Path, overwrite: true);
        lock (_lock)
        {
            _lastWritten[request.Path] = request.Json;
        }
    }

    private static void SetAside(string path, string suffix)
    {
        try
        {
            File.Move(path, System.IO.Path.ChangeExtension(path, null) + "." + suffix + ".json", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Si no se puede apartar, el próximo guardado lo reemplaza.
        }
    }
}

/// <summary>Una escritura pendiente de <see cref="LibraryOrganizationStore"/>: el archivo y su contenido.</summary>
public sealed record LibrarySaveRequest(string Path, string Json, bool Empty);

internal sealed class LibraryFile
{
    public int Version { get; set; } = LibraryOrganizationStore.CurrentVersion;

    public List<LibraryFolderEntry?>? Folders { get; set; } = [];

    /// <summary>Medio → carpeta; los medios de la raíz no aparecen.</summary>
    public Dictionary<string, string>? Media { get; set; } = new(StringComparer.Ordinal);

    public List<string>? Favorites { get; set; } = [];
}

internal sealed class LibraryFolderEntry
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Parent { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(LibraryFile))]
internal sealed partial class LibraryJsonContext : JsonSerializerContext;
