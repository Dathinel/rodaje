using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Serialization;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Formato <c>.rodaje</c> (<c>Serialization/</c>)» de <c>docs/arquitectura/nucleo.md</c>. Cada prueba cita la frase.
/// </summary>
public sealed class SerializationSectionContractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Rodaje pruebas", $"Formato núcleo {Guid.NewGuid():N}");

    public SerializationSectionContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Una carpeta temporal sobrante no afecta a otras pruebas.
        }
        catch (UnauthorizedAccessException)
        {
            // Igual: el antivirus puede tener abierto un archivo recién escrito.
        }
    }

    private string PathFor(string name) => Path.Combine(_dir, name + ProjectSerializer.FileExtension);

    private static string RichJson() => ProjectSerializer.Serialize(SerializationSamples.Rich());

    private static JsonObject RichTree() => JsonNode.Parse(RichJson())!.AsObject();

    // «JSON UTF-8 legible, con tiempos en ticks y tasas como `{"num":30000,"den":1001}`»
    [Fact]
    public void Save_WritesReadableUtf8WithTicksAndRationalObjects()
    {
        string path = PathFor("Canción ñ");
        ProjectSerializer.Save(SerializationSamples.Rich(), path);

        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        Assert.Contains("edición ñ", text, StringComparison.Ordinal);
        Assert.Contains('\n', text);

        JsonObject root = JsonNode.Parse(text)!.AsObject();
        JsonObject rate = root["sequences"]![0]!["frameRate"]!.AsObject();
        Assert.Equal(30000, (long)rate["num"]!);
        Assert.Equal(1001, (long)rate["den"]!);
        Assert.Equal(2, rate.Count);

        JsonNode duration = root["media"]![0]!["duration"]!;
        Assert.Equal(JsonValueKind.Number, duration.GetValueKind());
        Assert.Equal(SerializationSamples.Rich().Media[0].Duration.Ticks, (long)duration);
    }

    // «Lee UTF-8 estricto.»
    [Fact]
    public void Load_InvalidUtf8_IsRejected()
    {
        string path = PathFor("bytes sueltos");
        byte[] json = Encoding.UTF8.GetBytes(RichJson());
        byte[] broken = [.. json[..20], 0xFF, .. json[20..]];
        File.WriteAllBytes(path, broken);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(path));

        Assert.Contains("UTF-8", ex.Message, StringComparison.Ordinal);
    }

    // «Lee UTF-8 estricto.» Regresión: un archivo UTF-16 o UTF-32 con su BOM se abría porque el lector seguía la marca.
    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    public void Load_OtherUnicodeEncodingWithBom_IsRejected(string encodingName)
    {
        Encoding encoding = Encoding.GetEncoding(encodingName);
        string path = PathFor("otra codificación");
        File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(RichJson())]);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(path));

        Assert.Contains("no es texto UTF-8", ex.Message, StringComparison.Ordinal);
    }

    // «Analiza el árbol JSON sin admitir campos repetidos.»
    [Fact]
    public void Deserialize_DuplicateProperty_IsRejected()
    {
        string json = RichJson().Replace("\"name\": \"Vacaciones", "\"name\": \"otro\",\n  \"name\": \"Vacaciones", StringComparison.Ordinal);
        Assert.NotEqual(RichJson(), json);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json));

        Assert.Contains("campos repetidos", ex.Message, StringComparison.Ordinal);
    }

    // «`ProjectMigrator` aplica en cadena las migraciones N → N+1 sobre el árbol»
    [Fact]
    public void Migrator_AppliesEachStepInOrderOnTheTree()
    {
        var steps = new List<int>();
        var chain = new Dictionary<int, Action<JsonObject>>
        {
            [1] = root => { steps.Add(1); root["schemaVersion"] = 2; },
            [2] = root => { steps.Add(2); root["schemaVersion"] = 3; },
            [3] = root => { steps.Add(3); root["schemaVersion"] = 4; },
        };
        var root = new JsonObject { ["schemaVersion"] = 1 };

        int original = new ProjectMigrator(chain, 4).Migrate(root);

        Assert.Equal(1, original);
        Assert.Equal([1, 2, 3], steps);
        Assert.Equal(4, (int)root["schemaVersion"]!);
    }

    // «`ProjectMigrator` aplica en cadena las migraciones N → N+1 sobre el árbol»
    [Fact]
    public void Deserialize_OldestVersion_IsMigratedToCurrent()
    {
        JsonObject root = RichTree();
        root["schemaVersion"] = 1;

        Project project = ProjectSerializer.Deserialize(root.ToJsonString());

        Assert.Equal(Project.CurrentSchemaVersion, project.SchemaVersion);
    }

    // «y rechaza versiones futuras.» / «`schemaVersion` es un entero (comparar versiones como texto falla).»
    [Theory]
    [InlineData(1)]
    [InlineData(87)]
    [InlineData(100)]
    public void Deserialize_FutureVersion_IsRejected(int ahead)
    {
        JsonObject root = RichTree();
        root["schemaVersion"] = Project.CurrentSchemaVersion + ahead;

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));

        Assert.Contains("versión más nueva", ex.Message, StringComparison.Ordinal);
    }

    // «Versión `schemaVersion` es un entero»
    [Theory]
    [InlineData("\"13\"")]
    [InlineData("13.5")]
    [InlineData("0")]
    [InlineData("null")]
    public void Deserialize_VersionNotPositiveInteger_IsRejected(string version)
    {
        JsonObject root = RichTree();
        root["schemaVersion"] = JsonNode.Parse(version);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));

        Assert.Contains("schemaVersion", ex.Message, StringComparison.Ordinal);
    }

    // «Convierte con el generador de código de System.Text.Json (`ProjectJsonContext`, sin reflexión, apto para AOT).»
    [Fact]
    public void Context_ProvidesGeneratedMetadataForProject()
    {
        Assert.NotNull(ProjectJsonContext.Default.Project);
        Assert.Same(ProjectJsonContext.Default, ProjectJsonContext.Default.Project.OriginatingResolver);
    }

    // «Busca nulos dentro de las listas»
    [Fact]
    public void Deserialize_NullInsideList_IsRejected()
    {
        JsonObject root = RichTree();
        root["media"]!.AsArray().Add(null);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));

        Assert.Contains(ex.Problems, p => p.Contains("medios", StringComparison.Ordinal));
    }

    // «y valida todo. Cualquier problema produce un `ProjectFormatException` con la lista completa en español.»
    [Fact]
    public void Deserialize_SeveralProblems_AreAllListedInSpanish()
    {
        Project project = SerializationSamples.Rich();
        project.Media.Clear();

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)));

        Assert.True(ex.Problems.Count > 1);
        Assert.StartsWith("El proyecto tiene datos inconsistentes", ex.Message, StringComparison.Ordinal);
        foreach (string problem in ex.Problems)
        {
            Assert.Contains(problem, ex.Message, StringComparison.Ordinal);
        }
    }

    // «Cualquier problema produce un `ProjectFormatException`»
    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\": 13, \"sequences\": 5}")]
    public void Deserialize_MalformedInput_ThrowsProjectFormatException(string json)
    {
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json));
    }

    // «Guardar es atómico: escribe `.tmp` con `WriteThrough`, lo pone en su lugar con `File.Replace` (la versión anterior
    // queda como `.bak`)»
    [Fact]
    public void Save_OverExisting_ReplacesThroughTempAndKeepsBackup()
    {
        string path = PathFor("atómico");
        Project first = SerializationSamples.Rich();
        first.Name = "primera";
        ProjectSerializer.Save(first, path);
        string before = File.ReadAllText(path);

        var fs = new Recording();
        Project second = SerializationSamples.Rich();
        second.Name = "segunda";
        ProjectSerializer.Save(second, path, fs);

        Assert.Equal(["Replace"], fs.Calls);
        Assert.Equal(path + ProjectSerializer.TempSuffix, fs.Source);
        Assert.Equal(path + ProjectSerializer.BackupSuffix, fs.Backup);
        Assert.Equal(before, File.ReadAllText(path + ProjectSerializer.BackupSuffix));
        Assert.Equal("segunda", ProjectSerializer.Load(path).Name);
        Assert.False(File.Exists(path + ProjectSerializer.TempSuffix));
    }

    // «y reintenta ante bloqueos pasajeros.»
    [Fact]
    public void Save_TransientLock_IsRetried()
    {
        string path = PathFor("bloqueo");
        ProjectSerializer.Save(SerializationSamples.Rich(), path);

        var fs = new Recording { FailuresLeft = 2 };
        ProjectSerializer.Save(SerializationSamples.Rich(), path, fs);

        Assert.Equal(["Replace", "Replace", "Replace"], fs.Calls);
        Assert.Equal(2, fs.Sleeps);
        Assert.Equal("project-1", ProjectSerializer.Load(path).Id);
    }

    // «Guardar nunca valida: nunca rechaza el trabajo de quien edita.»
    [Fact]
    public void Save_InvalidProject_IsStillWritten()
    {
        Project project = SerializationSamples.Rich();
        project.Media.Clear();
        Assert.NotEmpty(ModelValidator.Validate(project));
        string path = PathFor("inválido");

        ProjectSerializer.Save(project, path);

        Assert.Equal(ProjectSerializer.Serialize(project) + "\n", File.ReadAllText(path));
    }

    // «`ProjectSchema.Export` genera el esquema desde los mismos metadatos que usa el lector, y una prueba exige que
    // coincida con el archivo publicado»
    [Fact]
    public void Schema_FixesCurrentVersionAndDescribesRationals()
    {
        JsonObject schema = JsonNode.Parse(ProjectSchema.Export())!.AsObject();

        Assert.Equal(Project.CurrentSchemaVersion, (int)schema["properties"]!["schemaVersion"]!["const"]!);
        Assert.Equal("integer", (string)schema["properties"]!["schemaVersion"]!["type"]!);
        Assert.EndsWith($"v{Project.CurrentSchemaVersion}.schema.json", ProjectSchema.SchemaId, StringComparison.Ordinal);
        Assert.Contains("\"num\"", ProjectSchema.Export(), StringComparison.Ordinal);
    }

    private sealed class Recording : ProjectSerializer.SaveFileSystem
    {
        public List<string> Calls { get; } = [];

        public string? Source { get; private set; }

        public string? Backup { get; private set; }

        public int FailuresLeft { get; set; }

        public int Sleeps { get; private set; }

        public override IReadOnlyList<TimeSpan> RetryDelays => [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

        public override void Sleep(TimeSpan delay) => Sleeps++;

        public override void Replace(string source, string destination, string backup)
        {
            Calls.Add("Replace");
            Source = source;
            Backup = backup;
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new UnauthorizedAccessException("bloqueo pasajero simulado");
            }

            base.Replace(source, destination, backup);
        }

        public override void Move(string source, string destination)
        {
            Calls.Add("Move");
            Source = source;
            base.Move(source, destination);
        }
    }
}
