using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Services.Templates;

/// <summary>
/// Las plantillas propias en <c>plantillas.json</c>, junto a los ajustes (JSON generado en compilación, apto para AOT).
/// Lo que llega del archivo se corrige con <see cref="ProjectTemplate.Normalized"/> y lo que no tiene arreglo se descarta.
/// </summary>
public sealed class ProjectTemplateStore
{
    public const string FileName = "plantillas.json";

    /// <summary>Cuántas plantillas propias se guardan como mucho.</summary>
    public const int MaxUserTemplates = 50;

    /// <summary>Un archivo más grande que esto no se lee (nadie escribe tantas plantillas a mano).</summary>
    private const long MaxFileBytes = 1024 * 1024;

    private readonly List<ProjectTemplate> _user = [];

    public ProjectTemplateStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public static string DefaultPath => System.IO.Path.Combine(AppDataPaths.Root, FileName);

    public string Path { get; }

    /// <summary>Las propias, en el orden en que se guardaron.</summary>
    public IReadOnlyList<ProjectTemplate> UserTemplates => _user;

    /// <summary>Las de fábrica y después las propias.</summary>
    public IEnumerable<ProjectTemplate> All => BuiltInTemplates.All.Concat(_user);

    /// <summary>
    /// Lee el archivo; sin archivo no hay plantillas propias. Devuelve <c>null</c> si todo fue bien o el aviso en español
    /// si no se pudo leer o estaba dañado (en ese caso se aparta para no pisarlo).
    /// </summary>
    public string? Load()
    {
        _user.Clear();
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            if (new FileInfo(Path).Length <= MaxFileBytes
                && JsonSerializer.Deserialize(File.ReadAllText(Path), TemplatesJsonContext.Default.TemplateFile) is { } file)
            {
                foreach (ProjectTemplate? template in file.Templates ?? [])
                {
                    if (template?.Normalized() is { IsBuiltIn: false } valid && _user.Count < MaxUserTemplates && Find(valid.Id) is null)
                    {
                        _user.Add(valid);
                    }
                }

                return null;
            }
        }
        catch (JsonException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"No se pudieron leer las plantillas ({ex.Message}).";
        }

        string? aside = AppDataPaths.SetAside(Path, ".dañado");
        return "El archivo de plantillas estaba dañado; se siguen ofreciendo las de fábrica" + (aside is null ? "." : $" y el archivo quedó en {aside}.");
    }

    /// <summary>La plantilla (de fábrica o propia) con ese identificador, o <c>null</c>.</summary>
    public ProjectTemplate? Find(string id) => BuiltInTemplates.Find(id) ?? _user.Find(t => t.Id == id);

    /// <summary>
    /// Guarda una plantilla propia y escribe el archivo (si falla la escritura, lanza y no cambia nada). Si ya hay una propia con el mismo nombre (sin distinguir
    /// mayúsculas), la reemplaza en su lugar. Devuelve la plantilla tal como quedó guardada.
    /// </summary>
    /// <exception cref="ArgumentException">La plantilla no tiene arreglo o usa un identificador de fábrica.</exception>
    /// <exception cref="InvalidOperationException">Ya hay <see cref="MaxUserTemplates"/> plantillas propias.</exception>
    public ProjectTemplate Save(ProjectTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        ProjectTemplate valid = template.Normalized() ?? throw new ArgumentException("La plantilla no tiene un formato válido.", nameof(template));
        if (valid.IsBuiltIn)
        {
            throw new ArgumentException("Las plantillas de fábrica no se pueden pisar.", nameof(template));
        }

        List<ProjectTemplate> next = [.. _user];
        int index = next.FindIndex(t => t.Id == valid.Id || string.Equals(t.Name, valid.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            valid.Id = next[index].Id;
            next[index] = valid;
        }
        else if (next.Count >= MaxUserTemplates)
        {
            throw new InvalidOperationException($"Ya hay {MaxUserTemplates} plantillas propias; borra alguna antes de guardar otra.");
        }
        else
        {
            next.Add(valid);
        }

        Commit(next);
        return valid;
    }

    /// <summary>Borra una plantilla propia y escribe el archivo; <c>false</c> si no había ninguna con ese identificador.</summary>
    public bool Remove(string id)
    {
        List<ProjectTemplate> next = [.. _user];
        if (next.RemoveAll(t => t.Id == id) == 0)
        {
            return false;
        }

        Commit(next);
        return true;
    }

    /// <summary>Escribe primero y cambia la lista después: si el disco falla, en memoria queda lo que hay en el archivo.</summary>
    private void Commit(List<ProjectTemplate> next)
    {
        AppDataPaths.WriteAllTextAtomic(Path, JsonSerializer.Serialize(new TemplateFile { Templates = next }, TemplatesJsonContext.Default.TemplateFile));
        _user.Clear();
        _user.AddRange(next);
    }
}

/// <summary>Contenido de <c>plantillas.json</c>.</summary>
internal sealed class TemplateFile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<ProjectTemplate>? Templates { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = [typeof(RationalJsonConverter)])]
[JsonSerializable(typeof(TemplateFile))]
internal sealed partial class TemplatesJsonContext : JsonSerializerContext;
