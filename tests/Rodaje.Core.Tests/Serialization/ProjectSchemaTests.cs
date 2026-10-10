using System.Text.Json;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

public class ProjectSchemaTests
{
    private const string RegenerateVariable = "RODAJE_REGENERAR_ESQUEMA";

    private static string SchemaPath => Path.Combine(FindRepoRoot(), "schema", "rodaje-proyecto.v2.schema.json");

    /// <summary>Para regenerar el esquema versionado: RODAJE_REGENERAR_ESQUEMA=1 y correr las pruebas.</summary>
    [Fact]
    public void CommittedSchema_MatchesExport()
    {
        string exported = ProjectSchema.Export();

        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SchemaPath)!);
            File.WriteAllText(SchemaPath, exported + "\n");
        }

        Assert.True(File.Exists(SchemaPath), $"Falta {SchemaPath}. Para crearlo: {RegenerateVariable}=1 y correr las pruebas.");
        string committed = File.ReadAllText(SchemaPath).ReplaceLineEndings("\n").TrimEnd('\n');
        Assert.True(
            committed == exported,
            $"El esquema versionado no coincide con el código. Si el cambio del modelo es intencional, " +
            $"define {RegenerateVariable}=1, corre las pruebas y revisa el diff de {SchemaPath}.");
    }

    /// <summary>Un esquema publicado no cambia nunca: el de la versión 1 se conserva tal cual para los archivos viejos.</summary>
    [Fact]
    public void PublishedV1Schema_IsKept()
    {
        string v1 = Path.Combine(FindRepoRoot(), "schema", "rodaje-proyecto.v1.schema.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(v1));

        Assert.Equal(1, doc.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());
        Assert.EndsWith("v1.schema.json", doc.RootElement.GetProperty("$id").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_DescribesCustomConvertersAndRoot()
    {
        using JsonDocument doc = JsonDocument.Parse(ProjectSchema.Export());
        JsonElement root = doc.RootElement;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.Equal(ProjectSchema.SchemaId, root.GetProperty("$id").GetString());
        Assert.Equal(Project.CurrentSchemaVersion, root.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());

        string text = root.GetRawText();
        Assert.Contains("705 600 000", text, StringComparison.Ordinal);
        Assert.Contains("\"num\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("allTracks", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", ProjectSchema.Export(), StringComparison.Ordinal);
    }

    /// <summary>El ejemplo de docs/formato-proyecto.md es válido y está escrito tal como lo guarda Rodaje.</summary>
    [Fact]
    public void DocumentationExample_LoadsAndIsCanonical()
    {
        string doc = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "formato-proyecto.md")).ReplaceLineEndings("\n");
        int start = doc.IndexOf("```json\n", StringComparison.Ordinal) + "```json\n".Length;
        int end = doc.IndexOf("\n```", start, StringComparison.Ordinal);
        string example = doc[start..end];

        var project = ProjectSerializer.Deserialize(example);

        Assert.Equal(example, ProjectSerializer.Serialize(project));
        Assert.Equal(2, project.Sequences[0].GetLinkGroup("clip-1").Count);
    }

    /// <summary>Sube desde la carpeta de la prueba hasta la que contiene Rodaje.slnx.</summary>
    internal static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No se encontró la raíz del repositorio (Rodaje.slnx).");
    }
}
