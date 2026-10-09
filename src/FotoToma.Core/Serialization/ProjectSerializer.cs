using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FotoToma.Core.Model;

namespace FotoToma.Core.Serialization;

/// <summary>
/// Lee y escribe proyectos en el formato <c>.ftoma</c>: JSON en UTF-8 descrito en
/// <c>docs/formato-proyecto.md</c> y en el esquema <c>schema/fototoma-proyecto.v1.schema.json</c>.
/// </summary>
public static class ProjectSerializer
{
    /// <summary>Extensión de los archivos de proyecto, con el punto.</summary>
    public const string FileExtension = ".ftoma";

    /// <summary>Sufijo del archivo temporal que se escribe antes de reemplazar el proyecto.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>Sufijo de la copia de la versión anterior que deja cada guardado.</summary>
    public const string BackupSuffix = ".bak";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Convierte el proyecto en texto JSON indentado con saltos de línea LF.</summary>
    public static string Serialize(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.Serialize(project, ProjectJson.ProjectInfo);
    }

    /// <summary>
    /// Lee un proyecto desde texto JSON: lo analiza, lo migra a la versión actual, lo convierte en objetos
    /// y comprueba sus reglas con <see cref="ModelValidator"/>.
    /// </summary>
    /// <exception cref="ProjectFormatException">Si el texto no es un proyecto válido; el mensaje lo explica.</exception>
    public static Project Deserialize(string json) => Deserialize(json, ModelValidator.Validate);

    /// <summary>Igual que <see cref="Deserialize(string)"/> con el validador inyectable, para las pruebas.</summary>
    internal static Project Deserialize(string json, Func<Project, IReadOnlyList<string>> validate)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonObject root = ParseRoot(json);
        ProjectMigrator.Default.Migrate(root);

        Project? project;
        try
        {
            project = root.Deserialize(ProjectJson.ProjectInfo);
        }
        catch (JsonException ex)
        {
            // El serializador siempre indica la ruta del dato (por ejemplo $.sequences[0].frameRate).
            throw new ProjectFormatException(
                $"El archivo tiene un dato con un tipo o un valor que no corresponde en «{ex.Path}». " +
                "Es posible que se haya editado a mano o que esté dañado.",
                ex);
        }

        // La raíz ya se comprobó como objeto, así que el resultado nunca es null.
        IReadOnlyList<string> problems;
        try
        {
            problems = FindNullItems(project!);
            if (problems.Count == 0)
            {
                problems = validate(project!);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Último recurso: el validador está pensado para no lanzar con ningún dato, pero si un caso no
            // previsto lo hace, quien abre el archivo recibe el error de formato de siempre y no un fallo.
            throw new ProjectFormatException(
                "El proyecto tiene datos que no se pudieron comprobar y no se puede abrir. " +
                "Es posible que se haya editado a mano o que esté dañado.",
                ex);
        }

        if (problems.Count > 0)
        {
            throw new ProjectFormatException(
                "El proyecto tiene datos inconsistentes y no se puede abrir:\n- " + string.Join("\n- ", problems),
                problems);
        }

        return project!;
    }

    /// <summary>
    /// Guarda el proyecto de forma atómica: si el equipo se apaga a mitad de camino, en disco queda la
    /// versión anterior completa o la nueva completa, nunca un archivo a medias.
    /// </summary>
    /// <remarks>
    /// Primero escribe <c>ruta.tmp</c> en la misma carpeta pidiendo al sistema que lo lleve hasta el disco
    /// (<see cref="FileOptions.WriteThrough"/> y <see cref="FileStream.Flush(bool)"/>). Después, si el
    /// proyecto ya existía, lo cambia por el nuevo con <see cref="File.Replace(string, string, string)"/>
    /// dejando la versión anterior en <c>ruta.bak</c>; si no existía, solo renombra el temporal. No valida
    /// el proyecto: guardar nunca rechaza el trabajo de quien edita.
    /// <para>
    /// Si algo falla, el temporal se borra solo cuando no hace falta: el proyecto sigue en su ruta o nunca
    /// existió. Si el reemplazo alcanzó a mover el original a <c>ruta.bak</c> pero no a poner el nuevo en
    /// su lugar, se intenta renombrar el temporal; si tampoco se puede, se conserva y la
    /// <see cref="IOException"/> dice dónde está para recuperarlo a mano.
    /// </para>
    /// </remarks>
    public static void Save(Project project, string path) => Save(project, path, SaveFileSystem.Real);

    /// <summary>
    /// Igual que <see cref="Save(Project, string)"/>, pero con las operaciones de archivo inyectables para
    /// que las pruebas simulen fallos que en un disco real son casi imposibles de provocar.
    /// </summary>
    internal static void Save(Project project, string path, SaveFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        string tempPath = fullPath + TempSuffix;
        string backupPath = fullPath + BackupSuffix;
        byte[] bytes = Utf8NoBom.GetBytes(Serialize(project) + "\n");
        bool tempComplete = false;
        bool hadOriginal = false;

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            tempComplete = true;
            hadOriginal = fileSystem.FileExists(fullPath);
            if (hadOriginal)
            {
                fileSystem.Replace(tempPath, fullPath, backupPath);
            }
            else
            {
                fileSystem.Move(tempPath, fullPath);
            }
        }
        catch (Exception ex) when (tempComplete && hadOriginal && !fileSystem.FileExists(fullPath))
        {
            // File.Replace puede fallar después de mover el original a .bak (por ejemplo con
            // ERROR_UNABLE_TO_MOVE_REPLACEMENT_2): entonces el temporal es la única copia de la versión
            // nueva y no se borra. Primero se intenta ponerlo en su sitio.
            if (TryMove(fileSystem, tempPath, fullPath))
            {
                return;
            }

            throw new IOException(
                $"No se pudo terminar de guardar «{fullPath}»: el sistema movió la versión anterior a " +
                $"«{backupPath}» pero no pudo poner la nueva en su lugar. La versión nueva está completa en " +
                $"«{tempPath}»; para recuperarla, cámbiale el nombre a «{Path.GetFileName(fullPath)}». " +
                $"Detalle: {ex.Message}",
                ex);
        }
        catch
        {
            // El destino sigue en su sitio (con la versión anterior) o nunca existió: el temporal sobra.
            TryDelete(fileSystem, tempPath);
            throw;
        }
    }

    /// <summary>Abre un proyecto desde disco.</summary>
    /// <exception cref="ProjectFormatException">Si el archivo no es un proyecto válido.</exception>
    /// <exception cref="IOException">Si el archivo no existe o no se puede leer.</exception>
    public static Project Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json;
        try
        {
            json = File.ReadAllText(path, Utf8NoBom);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ProjectFormatException(
                $"No se pudo abrir «{Path.GetFileName(path)}»: el archivo no es texto UTF-8, así que no es un proyecto de FotoToma.",
                ex);
        }

        try
        {
            return Deserialize(json);
        }
        catch (ProjectFormatException ex)
        {
            throw new ProjectFormatException($"No se pudo abrir «{Path.GetFileName(path)}». {ex.Message}", ex.Problems, ex);
        }
    }

    private static JsonObject ParseRoot(string json)
    {
        JsonNode? node;
        try
        {
            // Un campo repetido en el mismo objeto es ambiguo (¿cuál vale?), así que se rechaza al analizar.
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException ex)
        {
            string where = ex.LineNumber is { } line ? $" (error cerca de la línea {line + 1})" : "";
            throw new ProjectFormatException(
                "El archivo no es un proyecto de FotoToma válido: su contenido no es JSON bien formado o tiene " +
                $"campos repetidos{where}. Puede estar incompleto o dañado; si existe, " +
                $"prueba con la copia «{BackupSuffix}» que deja cada guardado.",
                ex);
        }

        if (node is not JsonObject root)
        {
            throw new ProjectFormatException(
                "El archivo no es un proyecto de FotoToma válido: el JSON no es un objeto.");
        }

        return root;
    }

    /// <summary>
    /// El lector respeta las anotaciones de nulabilidad en las propiedades, pero no dentro de las listas:
    /// <c>"clips": [null]</c> llega como un elemento nulo. Se detecta aquí, antes de validar.
    /// </summary>
    private static List<string> FindNullItems(Project project)
    {
        var problems = new List<string>();

        void Check<T>(IEnumerable<T?> items, string what)
            where T : class
        {
            if (items.Any(i => i is null))
            {
                problems.Add($"Hay un elemento vacío (null) en la lista de {what}.");
            }
        }

        Check(project.Media, "medios");
        Check(project.Sequences, "secuencias");
        foreach (Sequence sequence in project.Sequences.OfType<Sequence>())
        {
            Check(sequence.VideoTracks, "pistas de video");
            Check(sequence.AudioTracks, "pistas de audio");
            foreach (Track track in sequence.VideoTracks.Concat(sequence.AudioTracks).OfType<Track>())
            {
                Check(track.Clips, "clips");
                foreach (Clip clip in track.Clips.OfType<Clip>())
                {
                    Check(clip.Effects, "efectos");
                    foreach (EffectInstance effect in clip.Effects.OfType<EffectInstance>())
                    {
                        Check(effect.Parameters.Values, "parámetros");
                        foreach (Parameter parameter in effect.Parameters.Values.OfType<Parameter>())
                        {
                            Check(parameter.Keyframes, "keyframes");
                        }
                    }
                }
            }
        }

        return problems;
    }

    private static void TryDelete(SaveFileSystem fileSystem, string path)
    {
        try
        {
            fileSystem.Delete(path);
        }
        catch (IOException)
        {
            // El temporal sobrante no daña nada: el próximo guardado lo sobrescribe.
        }
        catch (UnauthorizedAccessException)
        {
            // Igual que arriba.
        }
    }

    private static bool TryMove(SaveFileSystem fileSystem, string source, string destination)
    {
        try
        {
            fileSystem.Move(source, destination);
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

    /// <summary>
    /// Las operaciones de archivo que hace <see cref="Save(Project, string)"/> después de escribir el
    /// temporal. La implementación real llama a <see cref="File"/>; las pruebas la reemplazan.
    /// </summary>
    internal class SaveFileSystem
    {
        public static readonly SaveFileSystem Real = new();

        public virtual bool FileExists(string path) => File.Exists(path);

        public virtual void Replace(string source, string destination, string backup) => File.Replace(source, destination, backup);

        public virtual void Move(string source, string destination) => File.Move(source, destination);

        public virtual void Delete(string path) => File.Delete(path);
    }
}
