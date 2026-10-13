using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Core.Model;

namespace Rodaje.Core.Serialization;

/// <summary>
/// Lee y escribe proyectos en el formato <c>.rodaje</c>: JSON en UTF-8 descrito en
/// <c>docs/formato-proyecto.md</c> y en el esquema <c>schema/rodaje-proyecto.v13.schema.json</c>.
/// </summary>
public static class ProjectSerializer
{
    /// <summary>Extensión de los archivos de proyecto, con el punto.</summary>
    public const string FileExtension = ".rodaje";

    /// <summary>Sufijo del archivo temporal que se escribe antes de reemplazar el proyecto.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>Sufijo de la copia de la versión anterior que deja cada guardado.</summary>
    public const string BackupSuffix = ".bak";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Convierte el proyecto en texto JSON indentado con saltos de línea LF.</summary>
    /// <exception cref="ProjectFormatException">
    /// Si el proyecto tiene un valor que el archivo no puede guardar tal cual (un número NaN o infinito, o un texto con
    /// un sustituto UTF-16 suelto); el mensaje dice dónde está. Antes, el número daba una <see cref="ArgumentException"/>
    /// sin ruta y el texto se cambiaba en silencio por U+FFFD.
    /// </exception>
    public static string Serialize(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            return JsonSerializer.Serialize(project, ProjectJson.WriteInfo);
        }
        catch (JsonException ex)
        {
            string where = SaveCheck.Locate(project) ?? ex.Path ?? "$";
            throw new ProjectFormatException(
                $"No se puede guardar el proyecto: el valor de «{where}» {ex.Message}. " +
                "El archivo anterior, si lo había, queda como estaba.",
                ex);
        }
    }

    /// <summary>
    /// Lee un proyecto desde texto JSON: lo analiza, lo migra a la versión actual, lo convierte en objetos
    /// y comprueba sus reglas con <see cref="ModelValidator"/>.
    /// </summary>
    /// <exception cref="ProjectFormatException">Si el texto no es un proyecto válido; el mensaje lo explica.</exception>
    public static Project Deserialize(string json) => Deserialize(json, ModelValidator.Validate, out ClampSummary _);

    /// <summary>
    /// Igual que <see cref="Deserialize(string)"/>; además dice cuántos valores de la v1.0.0 fuera de rango se acotaron
    /// al leer (<see cref="Rodaje.Core.Effects.ParameterLimits.Normalize(Project)"/> y
    /// <see cref="TimelineLimits.Normalize(Project)"/>). Si es más de cero, el proyecto en
    /// memoria ya no es igual al archivo: quien lo abre debe avisarlo y tratarlo como modificado.
    /// </summary>
    /// <exception cref="ProjectFormatException">Si el texto no es un proyecto válido; el mensaje lo explica.</exception>
    public static Project Deserialize(string json, out int clampedValues)
    {
        Project project = Deserialize(json, ModelValidator.Validate, out ClampSummary clamped);
        clampedValues = clamped.Total;
        return project;
    }

    /// <summary>
    /// Igual que <see cref="Deserialize(string, out int)"/>, pero dice qué se acotó por clase (tasas, clips recortados,
    /// clips quitados y valores) para que el aviso lo cuente.
    /// </summary>
    /// <exception cref="ProjectFormatException">Si el texto no es un proyecto válido; el mensaje lo explica.</exception>
    public static Project Deserialize(string json, out ClampSummary clamped) => Deserialize(json, ModelValidator.Validate, out clamped);

    /// <summary>Igual que <see cref="Deserialize(string)"/> con el validador inyectable, para las pruebas.</summary>
    internal static Project Deserialize(string json, Func<Project, IReadOnlyList<string>> validate) => Deserialize(json, validate, out ClampSummary _);

    private static Project Deserialize(string json, Func<Project, IReadOnlyList<string>> validate, out ClampSummary clamped)
    {
        ArgumentNullException.ThrowIfNull(json);
        clamped = ClampSummary.None;

        JsonObject root = ParseRoot(json);
        ProjectMigrator.Default.Migrate(root);

        Project? project;
        try
        {
            project = root.Deserialize(ProjectJson.ReadInfo);
        }
        catch (JsonException ex) when (ex.Message == FiniteDoubleConverter.ReadProblem)
        {
            // Abrirlo como infinito dejaría editar un proyecto que después no se puede guardar.
            throw new ProjectFormatException(
                $"El valor de «{ex.Path}» {FiniteDoubleConverter.ReadProblem} y no se puede abrir. " +
                "Es posible que se haya editado a mano o que esté dañado.",
                ex);
        }
        catch (JsonException ex)
        {
            // El serializador siempre indica la ruta del dato (por ejemplo $.sequences[0].frameRate).
            throw new ProjectFormatException(
                $"El archivo tiene un dato con un tipo o un valor que no corresponde en «{ex.Path}». " +
                "Es posible que se haya editado a mano o que esté dañado.",
                ex);
        }
        catch (InvalidOperationException ex)
        {
            // Un texto con un sustituto UTF-16 suelto (la mitad de un par) no se puede leer como cadena.
            throw new ProjectFormatException(
                "El archivo tiene un texto que no es UTF-16 válido (un carácter incompleto) y no se puede abrir. " +
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
                // Lo que la v1.0.0 guardaba sin límite (posición, ancla, giro, tasa de la secuencia, clips más allá del
                // final) se acota al abrir en vez de rechazarse.
                int values = Rodaje.Core.Effects.ParameterLimits.Normalize(project!);
                (int rates, int trimmed, List<Clip> removed) = TimelineLimits.NormalizeByKind(project!);
                clamped = new ClampSummary(rates, trimmed, removed, values);
                problems = validate(project!);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Último recurso: el validador no debería lanzar con ningún dato.
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
    /// Los bloqueos pasajeros (antivirus, indexador) se reintentan unas veces. Si el reemplazo deja el
    /// original en <c>ruta.bak</c> sin poner el nuevo, se renombra el temporal; si no se puede, se conserva
    /// y la <see cref="IOException"/> dice dónde está.
    /// </para>
    /// </remarks>
    public static void Save(Project project, string path) => Save(project, path, SaveFileSystem.Real);

    /// <summary>Igual que <see cref="Save(Project, string)"/>, con las operaciones de archivo inyectables.</summary>
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
            Retry(fileSystem, () =>
            {
                using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            });

            tempComplete = true;
            hadOriginal = fileSystem.FileExists(fullPath);
            if (hadOriginal)
            {
                Retry(fileSystem, () => fileSystem.Replace(tempPath, fullPath, backupPath));
            }
            else
            {
                Retry(fileSystem, () => fileSystem.Move(tempPath, fullPath));
            }
        }
        catch (Exception ex) when (tempComplete && hadOriginal && !fileSystem.FileExists(fullPath))
        {
            // El original ya está en .bak: el temporal es la única copia de la versión nueva.
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
            // El destino sigue intacto o nunca existió: el temporal sobra.
            TryDelete(fileSystem, tempPath);
            throw;
        }
    }

    /// <summary>Abre un proyecto desde disco.</summary>
    /// <exception cref="ProjectFormatException">Si el archivo no es un proyecto válido.</exception>
    /// <exception cref="IOException">Si el archivo no existe o no se puede leer.</exception>
    public static Project Load(string path) => Load(path, out ClampSummary _);

    /// <summary>
    /// Abre un proyecto desde disco y dice cuántos valores fuera de rango se acotaron al leerlo (ver
    /// <see cref="Deserialize(string, out int)"/>).
    /// </summary>
    /// <exception cref="ProjectFormatException">Si el archivo no es un proyecto válido.</exception>
    /// <exception cref="IOException">Si el archivo no existe o no se puede leer.</exception>
    public static Project Load(string path, out int clampedValues)
    {
        Project project = Load(path, out ClampSummary clamped);
        clampedValues = clamped.Total;
        return project;
    }

    /// <summary>
    /// Abre un proyecto desde disco y dice qué se acotó al leerlo, por clase (ver
    /// <see cref="Deserialize(string, out ClampSummary)"/>).
    /// </summary>
    /// <exception cref="ProjectFormatException">Si el archivo no es un proyecto válido.</exception>
    /// <exception cref="IOException">Si el archivo no existe o no se puede leer.</exception>
    public static Project Load(string path, out ClampSummary clamped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        clamped = ClampSummary.None;

        string json;
        try
        {
            json = File.ReadAllText(path, Utf8NoBom);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ProjectFormatException(
                $"No se pudo abrir «{Path.GetFileName(path)}»: el archivo no es texto UTF-8, así que no es un proyecto de Rodaje.",
                ex);
        }

        try
        {
            return Deserialize(json, out clamped);
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
                "El archivo no es un proyecto de Rodaje válido: su contenido no es JSON bien formado o tiene " +
                $"campos repetidos{where}. Puede estar incompleto o dañado; si existe, " +
                $"prueba con la copia «{BackupSuffix}» que deja cada guardado.",
                ex);
        }

        if (node is not JsonObject root)
        {
            throw new ProjectFormatException(
                "El archivo no es un proyecto de Rodaje válido: el JSON no es un objeto.");
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

        void CheckEffects(List<EffectInstance> effects)
        {
            Check(effects, "efectos");
            foreach (EffectInstance effect in effects.OfType<EffectInstance>())
            {
                Check(effect.Parameters.Values, "parámetros");
                Check(effect.Options.Values, "opciones");
                foreach (Parameter parameter in effect.Parameters.Values.OfType<Parameter>())
                {
                    Check(parameter.Keyframes, "keyframes");
                }
            }
        }

        Check(project.Media, "medios");
        Check(project.Sequences, "secuencias");
        foreach (Sequence sequence in project.Sequences.OfType<Sequence>())
        {
            Check(sequence.VideoTracks, "pistas de video");
            Check(sequence.AudioTracks, "pistas de audio");
            Check(sequence.Markers, "marcadores");
            Check(sequence.Regions, "regiones");
            foreach (Track track in sequence.VideoTracks.Concat(sequence.AudioTracks).OfType<Track>())
            {
                Check(track.Clips, "clips");
                CheckEffects(track.Effects);
                foreach (Clip clip in track.Clips.OfType<Clip>())
                {
                    CheckEffects(clip.Effects);
                    if (clip.Speed?.Curve is { } curve)
                    {
                        Check(curve.Points, "puntos de la curva de velocidad");
                    }

                    if (clip.Generator?.Style is { } style)
                    {
                        Check(style.Font.Fallback, "fuentes de respaldo");
                        Check(style.ExtraStrokes ?? [], "bordes");
                        Check(style.ExtraShadows ?? [], "sombras");
                        Check(style.Typography?.Features ?? [], "funciones OpenType");
                        Check(style.Fill.Gradient?.Stops ?? [], "paradas del degradado");
                        Check(clip.Generator.Spans ?? [], "tramos de formato");

                        foreach (StyleAnimation animation in new[] { style.In, style.Out }.OfType<StyleAnimation>())
                        {
                            Check(animation.Curves.Values, "curvas de la animación");
                            foreach (List<StyleKey> keys in animation.Curves.Values.OfType<List<StyleKey>>())
                            {
                                Check(keys, "puntos de la animación");
                            }
                        }
                    }
                }

                Check(track.Transitions, "transiciones");
                foreach (TransitionInstance transition in track.Transitions.OfType<TransitionInstance>())
                {
                    Check(transition.Options.Values, "opciones");
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

    // Antivirus o indexador que abren el archivo recién escrito: ERROR_SHARING_VIOLATION,
    // ERROR_LOCK_VIOLATION o acceso denegado pasajero.
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    internal static bool IsTransient(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is IOException && ex.HResult is SharingViolation or LockViolation);

    private static void Retry(SaveFileSystem fileSystem, Action action)
    {
        IReadOnlyList<TimeSpan> delays = fileSystem.RetryDelays;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < delays.Count && IsTransient(ex))
            {
                fileSystem.Sleep(delays[attempt]);
            }
        }
    }

    private static bool TryMove(SaveFileSystem fileSystem, string source, string destination)
    {
        try
        {
            Retry(fileSystem, () => fileSystem.Move(source, destination));
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

    /// <summary>Operaciones de archivo de <see cref="Save(Project, string)"/>; las pruebas las reemplazan.</summary>
    internal class SaveFileSystem
    {
        public static readonly SaveFileSystem Real = new();

        private static readonly TimeSpan[] DefaultDelays =
            [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)];

        /// <summary>Esperas entre intentos ante un bloqueo pasajero (5 intentos en total).</summary>
        public virtual IReadOnlyList<TimeSpan> RetryDelays => DefaultDelays;

        public virtual void Sleep(TimeSpan delay) => Thread.Sleep(delay);

        public virtual bool FileExists(string path) => File.Exists(path);

        public virtual void Replace(string source, string destination, string backup) => File.Replace(source, destination, backup);

        public virtual void Move(string source, string destination) => File.Move(source, destination);

        public virtual void Delete(string path) => File.Delete(path);
    }
}
