using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.App.Services;
using Rodaje.Core.Serialization;
using Rodaje.Export;

namespace Rodaje.App.Views.Export;

/// <summary>Una exportación sin terminar tal como se guarda: el proyecto entero (JSON del <c>.rodaje</c>) y lo demás de la petición.</summary>
public sealed class SavedExport
{
    public string ProjectJson { get; set; } = "";

    public string SequenceId { get; set; } = "";

    public string OutputPath { get; set; } = "";

    public ExportSettings Settings { get; set; } = new();

    public ExportRange? Range { get; set; }

    public string? ProjectDirectory { get; set; }

    public bool ContinueWithoutMissing { get; set; }
}

/// <summary>El archivo de la cola: las que esperan y la que estaba en marcha, en el orden en que empiezan.</summary>
public sealed class SavedExportFile
{
    public int Version { get; set; } = 1;

    public List<SavedExport> Exports { get; set; } = [];
}

/// <summary>
/// Guarda la cola de exportaciones en <c>cola-exportaciones.json</c> cada vez que cambia, así sobrevive a cerrar la app
/// (o a un apagón); al abrir se pregunta si se reanuda. Una que estaba a medias vuelve a empezar.
/// </summary>
public sealed class ExportQueueStore(string path)
{
    public const string FileName = "cola-exportaciones.json";

    public static string DefaultPath => System.IO.Path.Combine(AppDataPaths.Root, FileName);

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>Cuántas entradas saltó el último <see cref="Load"/> por estar dañadas, para avisar de que no se recuperan.</summary>
    public int Skipped { get; private set; }

    /// <summary>
    /// Copia del archivo original que dejó el último <see cref="Load"/> al saltar entradas (o el archivo apartado, si no se
    /// pudo leer entero); <c>null</c> si no hubo nada dañado o no se pudo copiar.
    /// </summary>
    public string? DamagedPath { get; private set; }

    /// <summary>El aviso para quien usa la app tras el último <see cref="Load"/>; <c>null</c> si no se perdió nada.</summary>
    public string? DamagedNotice { get; private set; }

    /// <summary>Nombre de la copia del archivo con lo que no se pudo leer, junto a él.</summary>
    public string DamagedCopyPath => System.IO.Path.ChangeExtension(Path, null) + ".dañado" + System.IO.Path.GetExtension(Path);

    /// <summary>Escribe las peticiones (o borra el archivo si no queda ninguna).</summary>
    public void Save(IEnumerable<(ExportRequest Request, bool ContinueWithoutMissing)> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var file = new SavedExportFile();
        foreach ((ExportRequest request, bool continueWithoutMissing) in pending)
        {
            file.Exports.Add(new SavedExport
            {
                ProjectJson = ProjectSerializer.Serialize(request.Project),
                SequenceId = request.SequenceId,
                OutputPath = request.OutputPath,
                Settings = request.Settings,
                Range = request.Range,
                ProjectDirectory = request.ProjectDirectory,
                ContinueWithoutMissing = continueWithoutMissing,
            });
        }

        if (file.Exports.Count == 0)
        {
            Clear();
            return;
        }

        AppDataPaths.WriteAllTextAtomic(Path, JsonSerializer.Serialize(file, ExportQueueJsonContext.Default.SavedExportFile));
    }

    /// <summary>Cuántas copias «.dañado» se guardan como mucho junto al archivo: la de nombre simple y «-2» a «-N».</summary>
    public const int MaxDamagedCopies = 5;

    /// <summary>Intentos de lectura ante un <see cref="IOException"/> pasajero (otra instancia escribiendo, un antivirus).</summary>
    internal const int ReadAttempts = 3;

    /// <summary>Lee el archivo de la cola; se cambia en las pruebas para simular lecturas fallidas o carreras.</summary>
    internal Func<string, byte[]> ReadBytes { get; init; } = File.ReadAllBytes;

    /// <summary>Espera entre intentos de lectura; se cambia en las pruebas.</summary>
    internal Action<int> Pause { get; init; } = Thread.Sleep;

    /// <summary>
    /// Se llama al apartar una cola dañada justo antes de borrar el original, ya comparado, con su manejador abierto; las
    /// pruebas lo usan para que otra instancia intente reemplazar el archivo en ese momento.
    /// </summary>
    internal Action? BeforeRemove { get; init; }

    /// <summary>
    /// Lo que quedó de la última vez; vacío si no hay archivo o está dañado (entonces se aparta). Una entrada dañada se salta
    /// sola y se cuenta en <see cref="Skipped"/>: las demás se recuperan.
    /// </summary>
    public IReadOnlyList<(ExportRequest Request, bool ContinueWithoutMissing)> Load()
    {
        Skipped = 0;
        DamagedPath = null;
        DamagedNotice = null;
        if (!File.Exists(Path))
        {
            return [];
        }

        // Se lee una sola vez y todo lo demás (también la copia «.dañado») sale de estos bytes: antes KeepDamaged volvía a
        // leer y apartaba lo que hubiera entonces, aunque otra instancia ya hubiera escrito una cola buena.
        byte[] content;
        try
        {
            content = ReadWithRetry();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No poder leerlo ahora (en uso, sin permiso) no dice nada de su contenido: antes se apartaba como dañada una
            // cola válida. Se deja donde está y se vuelve a intentar en el siguiente arranque.
            DamagedNotice = "La cola de exportaciones de la última vez no se pudo leer ahora; se volverá a intentar al abrir de nuevo.";
            return [];
        }

        try
        {
            SavedExportFile? file = JsonSerializer.Deserialize(Decode(content), ExportQueueJsonContext.Default.SavedExportFile);
            List<(ExportRequest Request, bool ContinueWithoutMissing)> loaded = [];
            foreach (SavedExport? e in file?.Exports ?? [])
            {
                // Una entrada nula o sin proyecto, secuencia, salida o ajustes (un archivo dañado) lanzaba NullReference o
                // ArgumentNull fuera de este catch: se salta.
                if (e is not { ProjectJson: not null, SequenceId: not null, OutputPath.Length: > 0, Settings: not null })
                {
                    Skipped++;
                    continue;
                }

                // Un ProjectJson roto apartaba el archivo entero como «.dañado» y se perdían también las entradas buenas.
                try
                {
                    loaded.Add((new ExportRequest(ProjectSerializer.Deserialize(e.ProjectJson), e.SequenceId, e.OutputPath,
                        e.Settings, e.Range) { ProjectDirectory = e.ProjectDirectory }, e.ContinueWithoutMissing));
                }
                catch (Exception ex) when (ex is JsonException or ProjectFormatException or NotSupportedException)
                {
                    Skipped++;
                }
            }

            // El siguiente Save reescribe el archivo sin las entradas saltadas: sin esta copia se perdían sin rastro (y, si
            // estaban todas rotas, sin aviso, porque solo se preguntaba con alguna pendiente).
            // Si no quedó ninguna buena no hay nada que ofrecer y nadie más limpiaba el archivo: el aviso y la copia se
            // repetían en cada arranque. Entonces se aparta entero (sigue a salvo en la copia) en vez de copiarlo.
            if (Skipped > 0)
            {
                DamagedPath = KeepDamaged(content, removeOriginal: loaded.Count == 0);
                DamagedNotice = (Skipped == 1 ? "Una exportación de la última vez no se pudo leer" : $"{Skipped} exportaciones de la última vez no se pudieron leer")
                    + (DamagedPath is null ? "." : $"; el archivo original quedó en «{DamagedPath}».");
            }

            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or ProjectFormatException or NotSupportedException)
        {
            DamagedPath = KeepDamaged(content, removeOriginal: true);
            DamagedNotice = "La cola de exportaciones de la última vez no se pudo leer"
                + (DamagedPath is null ? "." : $"; el archivo quedó en «{DamagedPath}».");
            return [];
        }
    }

    private byte[] ReadWithRetry()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return ReadBytes(Path);
            }
            catch (IOException ex) when (attempt < ReadAttempts && ex is not (FileNotFoundException or DirectoryNotFoundException))
            {
                Pause(50 * attempt);
            }
        }
    }

    // Como File.ReadAllText: detecta la marca de orden de bytes y, sin ella, UTF-8.
    private static string Decode(byte[] content)
    {
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Guarda <paramref name="content"/> (lo que leyó <see cref="Load"/>, no lo que haya ahora en <see cref="Path"/>) en
    /// <see cref="DamagedCopyPath"/> sin pisar una copia anterior con otro contenido (antes se copiaba con
    /// <c>overwrite: true</c>): si ya hay una igual se reusa y, si es distinta, se usa «.dañado-2», «-3»… hasta
    /// <see cref="MaxDamagedCopies"/>; con todas ocupadas se reemplaza la más vieja (antes no había tope y cada
    /// <see cref="Load"/> las leía todas). Con <paramref name="removeOriginal"/> se borra <see cref="Path"/>, pero solo si
    /// sigue teniendo esos mismos bytes: si otra instancia escribió entretanto una cola buena, se deja.
    /// </summary>
    private string? KeepDamaged(byte[] content, bool removeOriginal)
    {
        try
        {
            string? target = null;
            string? oldest = null;
            DateTime oldestTime = DateTime.MaxValue;
            for (int n = 1; n <= MaxDamagedCopies; n++)
            {
                string candidate = n == 1 ? DamagedCopyPath : System.IO.Path.ChangeExtension(Path, null) + $".dañado-{n}" + System.IO.Path.GetExtension(Path);
                var info = new FileInfo(candidate);
                if (!info.Exists)
                {
                    target ??= candidate;
                    continue;
                }

                // Primero por longitud: así no se lee entera una copia que no puede ser igual.
                if (info.Length == content.Length && File.ReadAllBytes(candidate).AsSpan().SequenceEqual(content))
                {
                    RemoveOriginalIfUnchanged(content, removeOriginal);
                    return candidate;
                }

                if (info.LastWriteTimeUtc < oldestTime)
                {
                    oldestTime = info.LastWriteTimeUtc;
                    oldest = candidate;
                }
            }

            target ??= oldest!;
            File.WriteAllBytes(target, content);
            RemoveOriginalIfUnchanged(content, removeOriginal);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RemoveOriginalIfUnchanged(byte[] content, bool removeOriginal)
    {
        if (!removeOriginal)
        {
            return;
        }

        try
        {
            if (File.ReadAllBytes(Path).AsSpan().SequenceEqual(content))
            {
                BeforeRemove?.Invoke();
                File.Delete(Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ya no está o no se puede tocar: la copia ya está escrita y el archivo se vuelve a mirar al abrir de nuevo.
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    Converters = [typeof(RationalJsonConverter), typeof(TimeJsonConverter)])]
[JsonSerializable(typeof(SavedExportFile))]
internal sealed partial class ExportQueueJsonContext : JsonSerializerContext;
