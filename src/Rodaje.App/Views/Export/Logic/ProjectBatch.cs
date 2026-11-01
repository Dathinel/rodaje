using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Export;

namespace Rodaje.App.Views.Export;

/// <summary>Lo que entra a la cola al exportar otros proyectos y lo que se saltó, con el porqué.</summary>
public sealed record ProjectBatchPlan(IReadOnlyList<ExportRequest> Requests, IReadOnlyList<(string ProjectPath, string Problem)> Skipped)
{
    /// <summary>Una línea por salida, para confirmar antes de encolar.</summary>
    public IEnumerable<string> OutputNames => Requests.Select(r => Path.GetFileName(r.OutputPath));
}

/// <summary>
/// «Exportar otros proyectos…»: varios archivos <c>.rodaje</c> entran a la cola con los ajustes de ahora, cada uno con su
/// secuencia principal entera y junto a su archivo («Boda.rodaje» → «Boda.mp4», con un número si ya existe). Sin WinUI;
/// la vista solo elige los archivos y muestra el resultado.
/// </summary>
public static class ProjectBatch
{
    /// <summary>Arma las peticiones; un archivo que no se puede abrir, sin secuencia o que es el proyecto abierto se salta con su motivo.</summary>
    /// <param name="projectPaths">Archivos elegidos, en orden.</param>
    /// <param name="settings">Los ajustes resueltos del diálogo (<c>ExportDialogModel.ResolvedSettings</c>).</param>
    /// <param name="load">Cómo se abre cada archivo; <c>null</c> usa <see cref="ProjectSerializer.Load(string)"/>.</param>
    /// <param name="pendingOutputs">Salidas que ya esperan en la cola: no se repiten.</param>
    /// <param name="openProjectPath">El proyecto abierto en el editor: se exporta con el botón Exportar, no desde aquí.</param>
    public static ProjectBatchPlan Plan(IEnumerable<string> projectPaths, ExportSettings settings, Func<string, Project>? load = null,
        IReadOnlyCollection<string>? pendingOutputs = null, string? openProjectPath = null)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        ArgumentNullException.ThrowIfNull(settings);
        load ??= ProjectSerializer.Load;
        // El recorte es de la secuencia de ahora: en otro proyecto podría salirse del cuadro.
        if (settings.Video is { Crop: not null } video)
        {
            settings = settings with { Video = video with { Crop = null } };
        }

        var requests = new List<ExportRequest>();
        var skipped = new List<(string, string)>();
        var used = new HashSet<string>(pendingOutputs ?? [], StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string projectPath in projectPaths)
        {
            string full;
            try
            {
                full = Path.GetFullPath(projectPath);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                skipped.Add((projectPath, "la ruta no es válida"));
                continue;
            }

            if (!seen.Add(full))
            {
                continue;
            }

            if (openProjectPath is not null && string.Equals(Path.GetFullPath(openProjectPath), full, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add((full, "es el proyecto abierto: expórtalo con el botón Exportar"));
                continue;
            }

            Project project;
            try
            {
                project = load(full);
            }
            catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException)
            {
                skipped.Add((full, ex.Message));
                continue;
            }

            if (project.Sequences.Count == 0)
            {
                skipped.Add((full, "no tiene ninguna secuencia"));
                continue;
            }

            string folder = Path.GetDirectoryName(full) ?? "";
            string name = Path.GetFileNameWithoutExtension(ExportRanges.SuggestFileName(
                string.IsNullOrWhiteSpace(project.Name) ? Path.GetFileNameWithoutExtension(full) : project.Name, settings));
            string extension = "." + ExportRanges.Extension(settings);
            string output = Path.Combine(folder, name + extension);
            for (int n = 2; !used.Add(output) || File.Exists(output) || Directory.Exists(output); n++)
            {
                output = Path.Combine(folder, $"{name} ({n}){extension}");
            }

            requests.Add(new ExportRequest(project, project.Sequences[0].Id, output, settings) { ProjectDirectory = folder });
        }

        return new ProjectBatchPlan(requests, skipped);
    }
}
