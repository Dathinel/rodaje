using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.App.Views.Library;

/// <summary>
/// Guarda las carpetas y los favoritos de la biblioteca en la carpeta de datos de Rodaje, un archivo por proyecto
/// (por su id), no dentro del <c>.rodaje</c>: organizan la vista y no cambian el video, así que el formato del proyecto
/// no sube de versión. Un proyecto copiado a otro PC llega sin sus carpetas (todos los medios quedan en la raíz).
/// </summary>
public sealed class LibraryOrganizationStore
{
    /// <summary>Subcarpeta de <c>%LOCALAPPDATA%\Rodaje</c> donde quedan los archivos.</summary>
    public const string FolderName = "biblioteca";

    public const int CurrentVersion = 1;

    private readonly Dictionary<string, string> _lastWritten = new(StringComparer.Ordinal);

    public LibraryOrganizationStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = System.IO.Path.GetFullPath(directory);
    }

    public string Directory { get; }

    /// <summary>Archivo del proyecto: su id si es un nombre seguro, o un resumen del id si no.</summary>
    public string PathFor(string projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        bool safe = projectId.Length is > 0 and <= 80 && projectId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        string name = safe
            ? projectId
            : "h-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(projectId)))[..32];
        return System.IO.Path.Combine(Directory, name + ".json");
    }

    /// <summary>
    /// Lee la organización del proyecto. Sin archivo, o si estaba dañado (que se aparta con «.dañado»), empieza vacía: en
    /// el peor caso los medios se ven todos en la raíz, nunca se pierde un medio.
    /// </summary>
    public LibraryOrganization Load(string projectId)
    {
        string path = PathFor(projectId);
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
            if (file is null)
            {
                SetAside(path);
            }

            return new LibraryOrganization();
        }

        _lastWritten[path] = text;
        return LibraryOrganization.From(
            file.Folders?.Select(f => new LibraryFolder { Id = f?.Id ?? "", Name = f?.Name ?? "", ParentId = f?.Parent }),
            file.Media,
            file.Favorites);
    }

    /// <summary>Escribe solo si cambió algo; una organización vacía sin archivo previo no crea archivo.</summary>
    public void Save(string projectId, LibraryOrganization organization)
    {
        ArgumentNullException.ThrowIfNull(organization);
        string path = PathFor(projectId);
        List<LibraryFolderEntry?> folders = [.. organization.Folders.Select(f => new LibraryFolderEntry { Id = f.Id, Name = f.Name, Parent = organization.FindFolder(f.ParentId)?.Id })];
        Dictionary<string, string> media = organization.MediaFolders.Where(p => organization.FindFolder(p.Value) is not null)
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        List<string> favorites = [.. organization.Favorites.Order(StringComparer.Ordinal)];
        var file = new LibraryFile { Version = CurrentVersion, Folders = folders, Media = media, Favorites = favorites };
        string json = JsonSerializer.Serialize(file, LibraryJsonContext.Default.LibraryFile);
        bool empty = folders.Count == 0 && favorites.Count == 0;
        if ((_lastWritten.TryGetValue(path, out string? last) && last == json) || (empty && !File.Exists(path)))
        {
            return;
        }

        System.IO.Directory.CreateDirectory(Directory);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
        _lastWritten[path] = json;
    }

    private static void SetAside(string path)
    {
        try
        {
            File.Move(path, System.IO.Path.ChangeExtension(path, null) + ".dañado.json", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Si no se puede apartar, el próximo guardado lo reemplaza.
        }
    }
}

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
