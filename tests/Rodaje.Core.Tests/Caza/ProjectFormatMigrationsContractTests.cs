using System.Text.Json.Nodes;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Serialization;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Versiones y migraciones» de <c>docs/formato-proyecto.md</c>. Cada prueba cita la frase.
/// </summary>
public sealed class ProjectFormatMigrationsContractTests
{
    private static JsonObject Doc(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject Track(JsonObject root, string kind, int index = 0) =>
        root["sequences"]![0]![kind]![index]!.AsObject();

    // «Si falta, no es un entero o es menor que 1, el archivo no es un proyecto de Rodaje.»
    [Theory]
    [InlineData("""{"id": "p"}""")]
    [InlineData("""{"schemaVersion": "3", "id": "p"}""")]
    [InlineData("""{"schemaVersion": 2.5, "id": "p"}""")]
    [InlineData("""{"schemaVersion": 0, "id": "p"}""")]
    [InlineData("""{"schemaVersion": -1, "id": "p"}""")]
    public void Sin_version_entera_positiva_no_es_un_proyecto(string json)
    {
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json));
        Assert.Contains("no es un proyecto de Rodaje", ex.Message, StringComparison.Ordinal);
    }

    // «Si es mayor que la versión que el programa conoce, el archivo fue creado con una versión más nueva y se pide
    // actualizar el programa en lugar de abrirlo a medias.»
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1000)]
    public void Una_version_mayor_pide_actualizar(int above)
    {
        string json = $$"""{"schemaVersion": {{Project.CurrentSchemaVersion + above}}, "id": "p", "sequences": [{"id": "s"}]}""";

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json));
        Assert.Contains("versión más nueva", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Actualiza Rodaje", ex.Message, StringComparison.Ordinal);
    }

    // «Si es menor, se aplican en cadena las migraciones de la versión 1 a la 2, de la 2 a la 3 y así hasta la actual»
    [Fact]
    public void Una_version_menor_se_migra_en_cadena_hasta_la_actual()
    {
        for (int version = 1; version < Project.CurrentSchemaVersion; version++)
        {
            JsonObject root = Doc($$"""{"schemaVersion": {{version}}, "id": "p", "sequences": [{"id": "s"}]}""");

            Assert.Equal(version, ProjectMigrator.Default.Migrate(root));
            Assert.Equal(Project.CurrentSchemaVersion, (int)root["schemaVersion"]!);
        }
    }

    // «y después se valida el resultado como cualquier otro proyecto.»
    [Fact]
    public void El_resultado_migrado_se_valida()
    {
        const string json = """{"schemaVersion": 1, "id": "p", "sequences": [{"id": "s"}, {"id": "s"}]}""";

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json));
        Assert.Contains("datos inconsistentes", ex.Message, StringComparison.Ordinal);
    }

    // «Cada versión tiene su propio esquema con el número en el nombre del archivo»
    [Fact]
    public void Cada_version_tiene_su_esquema()
    {
        for (int version = 1; version <= Project.CurrentSchemaVersion; version++)
        {
            string path = Path.Combine(ProjectSchemaTests.FindRepoRoot(), "schema", $"rodaje-proyecto.v{version}.schema.json");
            Assert.True(File.Exists(path), $"Falta {path}.");
        }
    }

    // «La migración de la 1 a la 2 agrega a cada secuencia las listas `markers` y `regions` vacías.»
    [Fact]
    public void De_1_a_2_cada_secuencia_gana_markers_y_regions_vacias()
    {
        JsonObject root = Doc("""{"schemaVersion": 1, "sequences": [{"id": "a"}, {"id": "b"}]}""");

        ProjectMigrator.AddMarkersAndRegions(root);

        Assert.Equal(2, (int)root["schemaVersion"]!);
        foreach (JsonNode? sequence in root["sequences"]!.AsArray())
        {
            Assert.Equal("[]", sequence!["markers"]!.ToJsonString());
            Assert.Equal("[]", sequence["regions"]!.ToJsonString());
        }
    }

    // «La de la 2 a la 3 agrega a cada pista `effects` y `transitions` vacías y a cada efecto `options` vacío»
    [Fact]
    public void De_2_a_3_pistas_y_efectos_ganan_listas_vacias()
    {
        JsonObject root = Doc("""
            {"schemaVersion": 2, "sequences": [{"id": "s",
              "videoTracks": [{"id": "v", "clips": [{"id": "c", "effects": [{"id": "e", "type": "rodaje.transform"}]}]}],
              "audioTracks": [{"id": "a", "clips": []}]}]}
            """);

        ProjectMigrator.AddEffectsAndTransitions(root);

        Assert.Equal(3, (int)root["schemaVersion"]!);
        foreach (JsonObject track in new[] { Track(root, "videoTracks"), Track(root, "audioTracks") })
        {
            Assert.Equal("[]", track["effects"]!.ToJsonString());
            Assert.Equal("[]", track["transitions"]!.ToJsonString());
        }

        Assert.Equal("{}", Track(root, "videoTracks")["clips"]![0]!["effects"]![0]!["options"]!.ToJsonString());
    }

    // «y convierte la envolvente vieja `rodaje.volume` (ganancia lineal en `gain`) en `rodaje.audio` con `gainDb` =
    // 20·log10(gain), keyframes incluidos; una ganancia de 0 o menos queda en −60 dB.»
    [Fact]
    public void De_2_a_3_el_volumen_lineal_pasa_a_decibelios()
    {
        JsonObject root = Doc("""
            {"schemaVersion": 2, "sequences": [{"id": "s", "videoTracks": [], "audioTracks": [{"id": "a", "clips": [
              {"id": "c", "effects": [{"id": "e", "type": "rodaje.volume", "parameters": {"gain": {"value": 0.1,
                "keyframes": [{"time": 0, "value": 10}, {"time": 1, "value": 0}, {"time": 2, "value": -2}]}}}]}]}]}]}
            """);

        ProjectMigrator.AddEffectsAndTransitions(root);

        JsonObject effect = Track(root, "audioTracks")["clips"]![0]!["effects"]![0]!.AsObject();
        Assert.Equal(BuiltInEffects.Audio, (string)effect["type"]!);
        JsonObject parameters = effect["parameters"]!.AsObject();
        Assert.False(parameters.ContainsKey("gain"));
        JsonObject gainDb = parameters[BuiltInEffects.AudioParameters.GainDb]!.AsObject();
        Assert.Equal(-20, (double)gainDb["value"]!, 9);
        JsonArray keyframes = gainDb["keyframes"]!.AsArray();
        Assert.Equal(20, (double)keyframes[0]!["value"]!, 9);
        Assert.Equal(EffectRegistry.SilenceDb, (double)keyframes[1]!["value"]!);
        Assert.Equal(EffectRegistry.SilenceDb, (double)keyframes[2]!["value"]!);
        Assert.Equal(-60, EffectRegistry.SilenceDb);
    }

    // «La de la 3 a la 4 quita los efectos `rodaje.speed`, que estaban reservados y nunca se aplicaron; los clips quedan
    // sin `speed`, a 1×.»
    [Fact]
    public void De_3_a_4_se_quitan_los_efectos_de_velocidad()
    {
        JsonObject root = Doc("""
            {"schemaVersion": 3, "sequences": [{"id": "s", "audioTracks": [],
              "videoTracks": [{"id": "v", "effects": [{"id": "t1", "type": "rodaje.speed"}, {"id": "t2", "type": "rodaje.transform"}],
                "clips": [{"id": "c", "effects": [{"id": "c1", "type": "rodaje.speed"}, {"id": "c2", "type": "rodaje.transform"}]}]}]}]}
            """);

        ProjectMigrator.RemoveSpeedEffects(root);

        Assert.Equal(4, (int)root["schemaVersion"]!);
        JsonObject track = Track(root, "videoTracks");
        Assert.Equal("t2", (string)track["effects"]!.AsArray().Single()!["id"]!);
        JsonObject clip = track["clips"]![0]!.AsObject();
        Assert.Equal("c2", (string)clip["effects"]!.AsArray().Single()!["id"]!);
        Assert.False(clip.ContainsKey("speed"));
    }

    [Fact]
    public void De_3_a_4_el_clip_abre_a_1x()
    {
        string json = File.ReadAllText(Path.Combine(ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Serialization", "ejemplos", "proyecto-v3.rodaje"));

        Project project = ProjectSerializer.Deserialize(json);

        Assert.All(project.Sequences.SelectMany(s => s.AllTracks).SelectMany(t => t.Clips), clip => Assert.Null(clip.Speed));
    }

    // «La de la 4 a la 5 solo cambia el número» … «La de la 5 a la 6 también solo cambia el número» … «La de la 6 a la 7
    // también» … «La de la 7 a la 8 solo cambia el número» … «La de la 8 a la 9 también» … «La de la 10 a la 11 solo cambia
    // el número» … «La de la 11 a la 12 también solo cambia el número» … «La de la 12 a la 13 solo cambia el número»
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Algunas_migraciones_solo_cambian_el_numero(int from)
    {
        string body = """
            "id": "p", "sequences": [{"id": "s", "masterEffects": [{"id": "m", "type": "rodaje.compressor"}, {"id": "q", "type": "rodaje.equalizer"}],
              "videoTracks": [{"id": "v", "effects": [{"id": "x", "type": "rodaje.speed"}],
                "clips": [{"id": "c", "effects": [{"id": "e", "type": "rodaje.volume", "parameters": {"gain": {"value": 0.5}}}]}]}],
              "audioTracks": [{"id": "a", "clips": []}]}]
            """;
        JsonObject root = Doc($$"""{"schemaVersion": {{from}}, {{body}}}""");
        JsonObject expected = Doc($$"""{"schemaVersion": {{from + 1}}, {{body}}}""");
        var migrator = new ProjectMigrator(StepOf(from), from + 1);

        migrator.Migrate(root);

        Assert.True(JsonNode.DeepEquals(expected, root), root.ToJsonString());
    }

    private static Dictionary<int, Action<JsonObject>> StepOf(int from) => new()
    {
        [from] = from switch
        {
            4 => ProjectMigrator.AllowGenerators,
            5 => ProjectMigrator.AllowCompounds,
            6 => ProjectMigrator.AllowBezierKeyframes,
            7 => ProjectMigrator.AllowMaskModes,
            8 => ProjectMigrator.AllowProTitles,
            10 => ProjectMigrator.AllowTimelineOrganization,
            11 => ProjectMigrator.AllowTransitionTiming,
            12 => ProjectMigrator.AllowTracking,
            _ => throw new ArgumentOutOfRangeException(nameof(from)),
        },
    };

    // «La de la 9 a la 10 deja sonando lo mismo con los efectos de audio en el orden de la pila: antes sonaban solo el
    // primer ecualizador y el primer compresor activos, siempre el ecualizador antes, así que ese ecualizador pasa delante
    // de ese compresor y los demás ecualizadores y compresores activos (que no sonaban) quedan desactivados.»
    [Fact]
    public void De_9_a_10_el_ecualizador_que_sonaba_pasa_delante_y_los_demas_se_apagan()
    {
        JsonObject root = Doc("""
            {"schemaVersion": 9, "sequences": [{"id": "s", "videoTracks": [], "audioTracks": [{"id": "a", "clips": [], "effects": [
              {"id": "c0", "type": "rodaje.compressor", "enabled": false},
              {"id": "c1", "type": "rodaje.compressor"},
              {"id": "q0", "type": "rodaje.equalizer", "enabled": false},
              {"id": "c2", "type": "rodaje.compressor", "enabled": true},
              {"id": "q1", "type": "rodaje.equalizer"},
              {"id": "q2", "type": "rodaje.equalizer"}]}]}]}
            """);

        ProjectMigrator.OrderAudioInserts(root);

        Assert.Equal(10, (int)root["schemaVersion"]!);
        JsonArray effects = Track(root, "audioTracks")["effects"]!.AsArray();
        Assert.Equal(["c0", "q1", "c1", "q0", "c2", "q2"], effects.Select(e => (string)e!["id"]!));
        bool Enabled(string id) => effects.Single(e => (string)e!["id"]! == id)!["enabled"] is not JsonValue v || (bool)v;
        Assert.True(Enabled("q1"));
        Assert.True(Enabled("c1"));
        Assert.False(Enabled("c2"));
        Assert.False(Enabled("q2"));
        Assert.False(Enabled("c0"));
        Assert.False(Enabled("q0"));
    }

    [Fact]
    public void De_9_a_10_si_el_ecualizador_ya_iba_delante_no_se_mueve()
    {
        JsonObject root = Doc("""
            {"schemaVersion": 9, "sequences": [{"id": "s", "videoTracks": [], "audioTracks": [{"id": "a", "clips": [], "effects": [
              {"id": "q1", "type": "rodaje.equalizer"},
              {"id": "c1", "type": "rodaje.compressor"}]}]}]}
            """);

        ProjectMigrator.OrderAudioInserts(root);

        Assert.Equal(["q1", "c1"], Track(root, "audioTracks")["effects"]!.AsArray().Select(e => (string)e!["id"]!));
    }

    // «Al volver a guardar, Rodaje siempre escribe la versión actual» … «La copia `.bak` descrita abajo conserva la
    // versión previa del archivo.»
    [Fact]
    public void Al_guardar_se_escribe_la_version_actual_y_el_bak_conserva_la_previa()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Rodaje pruebas", $"migraciones {Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "viejo.rodaje");
            const string v1 = """{"schemaVersion": 1, "id": "p", "sequences": [{"id": "s"}]}""";
            File.WriteAllText(path, v1);

            Project project = ProjectSerializer.Load(path);
            ProjectSerializer.Save(project, path);

            Assert.Equal(Project.CurrentSchemaVersion, (int)JsonNode.Parse(File.ReadAllText(path))!["schemaVersion"]!);
            Assert.Equal(v1, File.ReadAllText(path + ".bak"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
