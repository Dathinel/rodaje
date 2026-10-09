using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

public class ProjectMigratorTests
{
    private static JsonObject Doc(string json) => JsonNode.Parse(json)!.AsObject();

    [Theory]
    [InlineData("""{"id": "p"}""")]
    [InlineData("""{"schemaVersion": "1"}""")]
    [InlineData("""{"schemaVersion": null}""")]
    [InlineData("""{"schemaVersion": true}""")]
    [InlineData("""{"schemaVersion": 1.5}""")]
    [InlineData("""{"schemaVersion": {"v": 1}}""")]
    [InlineData("""{"schemaVersion": 0}""")]
    [InlineData("""{"schemaVersion": -3}""")]
    [InlineData("""{"schemaVersion": 99999999999}""")]
    public void Migrate_InvalidVersion_ThrowsNotAProject(string json)
    {
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectMigrator.Default.Migrate(Doc(json)));
        Assert.Contains("no es un proyecto de Rodaje válido", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrate_NewerVersion_ThrowsUpdateMessage()
    {
        var ex = Assert.Throws<ProjectFormatException>(() =>
            ProjectMigrator.Default.Migrate(Doc($$"""{"schemaVersion": {{Project.CurrentSchemaVersion + 1}}}""")));
        Assert.Contains("fue creado con una versión más nueva de Rodaje", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_NewerVersion_ThrowsUpdateMessage()
    {
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize("""{"schemaVersion": 2, "id": "p"}"""));
        Assert.Contains("versión más nueva", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_TargetsCurrentVersionAndLeavesCurrentFilesUntouched()
    {
        Assert.Equal(Project.CurrentSchemaVersion, ProjectMigrator.Default.TargetVersion);
        JsonObject root = Doc("""{"schemaVersion": 1, "id": "p"}""");
        string before = root.ToJsonString();

        Assert.Equal(1, ProjectMigrator.Default.Migrate(root));
        Assert.Equal(before, root.ToJsonString());
    }

    [Fact]
    public void Migrate_ChainsStepsInOrder()
    {
        var calls = new List<int>();
        var chain = new Dictionary<int, Action<JsonObject>>
        {
            [1] = r =>
            {
                calls.Add(1);
                r["nombreNuevo"] = r["name"]!.DeepClone();
                r.Remove("name");
                r["schemaVersion"] = 2;
            },
            [2] = r =>
            {
                calls.Add(2);
                r["speed"] = 1;
                r["schemaVersion"] = 3;
            },
        };
        var migrator = new ProjectMigrator(chain, targetVersion: 3);

        JsonObject v1 = Doc("""{"schemaVersion": 1, "name": "viejo"}""");
        Assert.Equal(1, migrator.Migrate(v1));
        Assert.Equal([1, 2], calls);
        Assert.Equal(3, (int)v1["schemaVersion"]!);
        Assert.Equal("viejo", (string)v1["nombreNuevo"]!);
        Assert.False(v1.ContainsKey("name"));
        Assert.Equal(1, (int)v1["speed"]!);

        calls.Clear();
        JsonObject v2 = Doc("""{"schemaVersion": 2}""");
        Assert.Equal(2, migrator.Migrate(v2));
        Assert.Equal([2], calls);
        Assert.Equal(3, (int)v2["schemaVersion"]!);
    }

    [Fact]
    public void Migrate_MissingStep_ThrowsInvalidOperation()
    {
        var migrator = new ProjectMigrator(new Dictionary<int, Action<JsonObject>>(), targetVersion: 2);
        var ex = Assert.Throws<InvalidOperationException>(() => migrator.Migrate(Doc("""{"schemaVersion": 1}""")));
        Assert.Contains("Falta la migración del formato 1 al 2", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(3)]
    public void Migrate_StepThatDoesNotSetVersion_ThrowsInvalidOperation(int? written)
    {
        var chain = new Dictionary<int, Action<JsonObject>>
        {
            [1] = r =>
            {
                if (written is null)
                {
                    r.Remove("schemaVersion");
                }
                else
                {
                    r["schemaVersion"] = written.Value;
                }
            },
        };
        var migrator = new ProjectMigrator(chain, targetVersion: 2);

        var ex = Assert.Throws<InvalidOperationException>(() => migrator.Migrate(Doc("""{"schemaVersion": 1}""")));
        Assert.Contains("no dejó schemaVersion en 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Arguments_AreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectMigrator.Default.Migrate(null!));
        Assert.Throws<ArgumentNullException>(() => new ProjectMigrator(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectMigrator(new Dictionary<int, Action<JsonObject>>(), 0));
    }
}
