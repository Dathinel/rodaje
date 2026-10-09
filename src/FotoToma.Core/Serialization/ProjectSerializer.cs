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
        return JsonSerializer.Serialize(project, ProjectJsonContext.ProjectInfo);
    }

    /// <summary>
    /// Lee un proyecto desde texto JSON: lo analiza, lo migra a la versión actual, lo convierte en objetos
    /// y comprueba sus reglas con <see cref="ModelValidator"/>.
    /// </summary>
    /// <exception cref="ProjectFormatException">Si el texto no es un proyecto válido; el mensaje lo explica.</exception>
    public static Project Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonObject root = ParseRoot(json);
        ProjectMigrator.Default.Migrate(root);

        Project? project;
        try
        {
            project = root.Deserialize(ProjectJsonContext.ProjectInfo);
        }
        catch (JsonException ex)
        {
            string where = string.IsNullOrEmpty(ex.Path) ? "" : $" en «{ex.Path}»";
            throw new ProjectFormatException(
                $"El archivo tiene un dato con un tipo o un valor que no corresponde{where}. " +
                "Es posible que se haya editado a mano o que esté dañado.",
                ex);
        }

        // La raíz ya se comprobó como objeto, así que el resultado nunca es null.
        IReadOnlyList<string> problems = FindNullItems(project!);
        if (problems.Count == 0)
        {
            problems = ModelValidator.Validate(project!);
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
    /// </remarks>
    public static void Save(Project project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        string tempPath = fullPath + TempSuffix;
        byte[] bytes = Utf8NoBom.GetBytes(Serialize(project) + "\n");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, fullPath + BackupSuffix);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        catch
        {
            TryDelete(tempPath);
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
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException(
                "El archivo no es un proyecto de FotoToma válido: su contenido no es JSON bien formado " +
                $"(error cerca de la línea {ex.LineNumber + 1}). Puede estar incompleto o dañado; si existe, " +
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
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
}
