using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Base del formato 16: versión mínima al guardar, apertura de un 16 y rechazo en la 1.0.3.</summary>
public sealed class Format16Tests
{
    private static string ExampleV13 =>
        File.ReadAllText(Path.Combine(ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Serialization", "ejemplos", "proyecto-v13.rodaje"));

    private static Project Plain() => ProjectSerializer.Deserialize(Fuzz.ProjectFileMutationFuzzTests.RichProject());

    private static Project WithPhoto()
    {
        Project project = Plain();
        project.Media.Add(new MediaRef { Id = "foto", Path = "foto.png", IsStillImage = true, HasVideo = true, Width = 1024, Height = 768 });
        return project;
    }

    private static Project Hdr()
    {
        Project project = WithPhoto();
        project.Sequences[0].ColorSpace = SequenceColorSpace.HdrPq;
        return project;
    }

    /// <summary>El guardado de la 1.0.3, copiado tal cual: la referencia de «igual que hoy».</summary>
    private static string SerializeAs103(Project project) =>
        JsonSerializer.Serialize(new Project
        {
            SchemaVersion = project.Sequences.Any(sequence => sequence.ColorSpace != SequenceColorSpace.Sdr709) ? 15
                : project.Media.Any(media => media.IsStillImage) ? 14 : 13,
            Id = project.Id,
            Name = project.Name,
            Media = project.Media,
            Sequences = project.Sequences,
        }, ProjectJson.WriteInfo);

    private static int WrittenVersion(string json) => (int)JsonNode.Parse(json)!["schemaVersion"]!;

    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void Without_anything_new_the_file_is_byte_for_byte_what_1_0_3_writes(int version)
    {
        Project project = version switch { 13 => Plain(), 14 => WithPhoto(), _ => Hdr() };

        string written = ProjectSerializer.Serialize(project);

        Assert.Equal(version, WrittenVersion(written));
        Assert.Equal(SerializeAs103(project), written);
        Assert.Equal(written, ProjectSerializer.Serialize(ProjectSerializer.Deserialize(written)));
    }

    [Theory]
    [InlineData(false, false, false, 13)]
    [InlineData(false, true, false, 14)]
    [InlineData(false, true, true, 15)]
    [InlineData(true, false, false, 16)]
    [InlineData(true, true, true, 16)]
    public void Minimum_version_takes_16_only_when_a_registered_check_says_so(bool uses16, bool photo, bool hdr, int expected)
    {
        Project project = hdr ? Hdr() : photo ? WithPhoto() : Plain();
        Func<Project, bool>[] checks = [_ => false, _ => uses16];

        Assert.Equal(expected, ProjectSerializer.MinimumSchemaVersion(project, checks));
    }

    [Fact]
    public void Today_no_block_writes_16()
    {
        Assert.Equal(15, ProjectSerializer.MinimumSchemaVersion(Hdr()));
        Assert.Equal(13, ProjectSerializer.MinimumSchemaVersion(new Project()));
    }

    [Fact]
    public void A_file_marked_16_opens_and_is_saved_back_in_the_lowest_version()
    {
        JsonObject root = JsonNode.Parse(ExampleV13)!.AsObject();
        root["schemaVersion"] = 16;

        Project project = ProjectSerializer.Deserialize(root.ToJsonString());

        Assert.Equal(16, project.SchemaVersion);
        Assert.Empty(ModelValidator.Validate(project));
        Assert.Equal(13, WrittenVersion(ProjectSerializer.Serialize(project)));
    }

    [Fact]
    public void Migration_15_to_16_only_changes_the_number()
    {
        JsonObject root = JsonNode.Parse(ProjectSerializer.Serialize(Hdr()))!.AsObject();
        JsonObject expected = (JsonObject)root.DeepClone();
        expected["schemaVersion"] = 16;

        ProjectMigrator.AllowFormat16(root);

        Assert.True(JsonNode.DeepEquals(expected, root));
    }

    /// <summary>La 1.0.3 lee hasta el 15: un 16 se rechaza con el mismo mensaje de versión futura de siempre.</summary>
    [Fact]
    public void Rodaje_1_0_3_rejects_a_16_with_the_usual_future_version_message()
    {
        var rodaje103 = new ProjectMigrator(new Dictionary<int, Action<JsonObject>>(), targetVersion: 15);
        JsonObject root = JsonNode.Parse(ExampleV13)!.AsObject();
        root["schemaVersion"] = 16;

        var ex = Assert.Throws<ProjectFormatException>(() => rodaje103.Migrate(root));

        Assert.Equal(
            "El archivo fue creado con una versión más nueva de Rodaje (formato 16); esta versión solo sabe abrir " +
            "hasta el formato 15. Actualiza Rodaje para abrirlo.",
            ex.Message);
    }
}
