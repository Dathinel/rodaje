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

    /// <summary>
    /// Lo que quedó de la última vez; vacío si no hay archivo o está dañado (entonces se aparta). Una entrada dañada se salta
    /// sola y se cuenta en <see cref="Skipped"/>: las demás se recuperan.
    /// </summary>
    public IReadOnlyList<(ExportRequest Request, bool ContinueWithoutMissing)> Load()
    {
        Skipped = 0;
        if (!File.Exists(Path))
        {
            return [];
        }

        try
        {
            SavedExportFile? file = JsonSerializer.Deserialize(File.ReadAllText(Path), ExportQueueJsonContext.Default.SavedExportFile);
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

            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or ProjectFormatException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppDataPaths.SetAside(Path, ".dañado");
            return [];
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
