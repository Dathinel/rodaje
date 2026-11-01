using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>«Exportar otros proyectos…»: cada .rodaje elegido entra a la cola junto a su archivo, con los ajustes de ahora.</summary>
public sealed class ProjectBatchTests
{
    private static readonly ExportSettings Web = ExportPresets.YouTube1080p.Settings;

    private static string SaveProject(TempDirectory dir, string file, string name)
    {
        Project project = ExportTestData.Project();
        project.Name = name;
        string path = dir.File(file);
        ProjectSerializer.Save(project, path);
        return path;
    }

    [Fact]
    public void Each_project_exports_its_first_sequence_next_to_its_file_with_the_current_settings()
    {
        using var dir = new TempDirectory();
        string boda = SaveProject(dir, "boda.rodaje", "Boda de Ana");
        string viaje = SaveProject(dir, "viaje.rodaje", "");

        ProjectBatchPlan plan = ProjectBatch.Plan([boda, viaje], Web);

        Assert.Empty(plan.Skipped);
        Assert.Equal(["Boda de Ana.mp4", "viaje.mp4"], plan.OutputNames);
        Assert.All(plan.Requests, r =>
        {
            Assert.Equal(dir.Path, Path.GetDirectoryName(r.OutputPath));
            Assert.Equal(dir.Path, r.ProjectDirectory);
            Assert.Equal("sec", r.SequenceId);
            Assert.Null(r.Range);
            Assert.Equal(Web.Container, r.Settings.Container);
        });
    }

    [Fact]
    public void Outputs_never_replace_a_file_a_pending_export_or_each_other()
    {
        using var dir = new TempDirectory();
        string a = SaveProject(dir, "a.rodaje", "Mismo nombre");
        string b = SaveProject(dir, "b.rodaje", "Mismo nombre");
        File.WriteAllText(dir.File("Mismo nombre.mp4"), "");

        ProjectBatchPlan plan = ProjectBatch.Plan([a, b, a], Web, pendingOutputs: [dir.File("Mismo nombre (2).mp4")]);

        Assert.Equal(["Mismo nombre (3).mp4", "Mismo nombre (4).mp4"], plan.OutputNames);
    }

    [Fact]
    public void Broken_files_the_open_project_and_empty_projects_are_skipped_with_a_reason()
    {
        using var dir = new TempDirectory();
        string good = SaveProject(dir, "bien.rodaje", "Bien");
        string broken = dir.File("roto.rodaje");
        File.WriteAllText(broken, "{ esto no es un proyecto");
        string missing = dir.File("no-existe.rodaje");
        var empty = new Project { Id = "e", Name = "Vacío" };
        string emptyPath = dir.File("vacio.rodaje");
        ProjectSerializer.Save(empty, emptyPath);

        ProjectBatchPlan plan = ProjectBatch.Plan([good, broken, missing, emptyPath, good], Web, openProjectPath: good);

        Assert.Empty(plan.Requests);
        Assert.Equal(4, plan.Skipped.Count);
        Assert.Contains("proyecto abierto", plan.Skipped[0].Problem, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(plan.Skipped[1].Problem));
        Assert.False(string.IsNullOrWhiteSpace(plan.Skipped[2].Problem));
        Assert.False(string.IsNullOrWhiteSpace(plan.Skipped[3].Problem));
    }

    [Fact]
    public void A_crop_of_the_current_sequence_is_dropped_and_the_loader_can_be_replaced()
    {
        ExportSettings cropped = Web with { Video = Web.Video! with { Crop = new ExportCrop(0, 0, 100, 100) } };
        Project project = ExportTestData.Project();

        ProjectBatchPlan plan = ProjectBatch.Plan([@"C:\proyectos\x.rodaje"], cropped, load: _ => project, pendingOutputs: []);

        Assert.Single(plan.Requests);
        Assert.Null(plan.Requests[0].Settings.Video!.Crop);
        Assert.Same(project, plan.Requests[0].Project);
        Assert.Equal(@"C:\proyectos\Mi película_ versión 1.mp4", plan.Requests[0].OutputPath);
    }
}
