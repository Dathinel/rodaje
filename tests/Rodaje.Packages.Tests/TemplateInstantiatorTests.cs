using System.Diagnostics;
using System.Text.Json.Nodes;
using Rodaje.Packages.Content;
using Xunit.Abstractions;

namespace Rodaje.Packages.Tests;

[Collection("Plantillas")]
public sealed class TemplateInstantiatorTests(ITestOutputHelper output)
{
    [Fact]
    public void Sustituye_todos_los_tipos_y_destinos_y_conserva_los_defectos()
    {
        TemplateDocument document = ContentReaders.ReadTemplate(TemplateReaderTests.Example);
        var values = new Dictionary<string, JsonNode?>
        {
            ["nombre"] = JsonValue.Create("Ana Ruiz"),
            ["acento"] = JsonValue.Create("#2a9d8f80"),
            ["fuente"] = JsonNode.Parse("""{ "family": "Segoe UI", "weight": 400 }"""),
            ["tamano"] = JsonValue.Create(80),
            ["kerning"] = JsonValue.Create(false),
            ["peso"] = JsonValue.Create(700),
            ["id-desconocido"] = null,
        };
        string before = document.Body.ToJsonString();
        TemplateInstance result = TemplateInstantiator.Instantiate(document, values);
        InstantiatedTemplateLayer first = result.Layers[0];
        Assert.Equal("Ana Ruiz", first.Text);
        Assert.Equal("Cargo", result.Layers[1].Text);
        Assert.Equal("#2A9D8F80", first.Style.Background!.Color);
        Assert.Equal("Segoe UI", first.Style.Font.Family);
        Assert.Equal("Segoe UI", result.Layers[1].Style.Font.Family);
        Assert.Equal(700, first.Style.Font.Weight);
        Assert.Equal(400, result.Layers[1].Style.Font.Weight);
        Assert.Equal(80, first.Style.Size);
        Assert.False(first.Style.Typography!.Kerning);
        Assert.Equal(JsonNode.Parse(first.StyleJson)!.ToJsonString(), first.StyleJson);
        Assert.Equal("Segoe UI", JsonNode.Parse(first.StyleJson)!["font"]!["family"]!.GetValue<string>());
        Assert.Equal(before, document.Body.ToJsonString());
        values["fuente"]!["family"] = "Otra";
        Assert.Equal("Segoe UI", first.Style.Font.Family);
        Assert.Equal("Nombre Apellido", TemplateInstantiator.Instantiate(document).Layers[0].Text);
    }

    [Fact]
    public void Copias_del_cuerpo_y_opciones_no_modifican_el_documento()
    {
        TemplateDocument document = ContentReaders.ReadTemplate(TemplateReaderTests.Example);
        document.Body["layers"]![0]!["text"] = "Cambio";
        document.Parameters[6].Options[0].Value.ReplaceWith(JsonValue.Create(999));
        Assert.Equal("Nombre Apellido", TemplateInstantiator.Instantiate(document).Layers[0].Text);
        Assert.Equal(400, document.Parameters[6].Options[0].Value.GetValue<int>());
    }

    [Theory]
    [InlineData("nombre", "42")]
    [InlineData("nombre", "null")]
    [InlineData("acento", "\"rojo\"")]
    [InlineData("acento", "\"#FFFFFFF\"")]
    [InlineData("acento", "\"#GGFFFFFF\"")]
    [InlineData("fuente", "\"Inter\"")]
    [InlineData("fuente", "{}")]
    [InlineData("fuente", "{\"family\":\"Inter\",\"weight\":901}")]
    [InlineData("tamano", "0")]
    [InlineData("tamano", "2001")]
    [InlineData("tamano", "1001")]
    [InlineData("tamano", "\"80\"")]
    [InlineData("kerning", "1")]
    [InlineData("peso", "900")]
    [InlineData("peso", "\"700\"")]
    public void Valores_invalidos_rechazados_por_parametro_o_lector_del_contenido(string id, string json)
    {
        TemplateDocument document = ContentReaders.ReadTemplate(TemplateReaderTests.Example);
        Assert.Throws<PackageContentException>(() => TemplateInstantiator.Instantiate(document,
            new Dictionary<string, JsonNode?> { [id] = JsonNode.Parse(json) }));
    }

    [Fact]
    public void Texto_instanciado_respeta_topes_y_unicode()
    {
        TemplateDocument document = ContentReaders.ReadTemplate(TemplateReaderTests.Example);
        foreach (string text in new[] { new string('a', 10001), new string('\n', 200), "\ud800" })
        {
            Assert.Throws<PackageContentException>(() => TemplateInstantiator.Instantiate(document,
                new Dictionary<string, JsonNode?> { ["nombre"] = JsonValue.Create(text) }));
        }

        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate("\ud800"));
    }

    [Fact]
    public void Rechaza_numeros_no_finitos_creados_en_memoria()
    {
        TemplateDocument document = ContentReaders.ReadTemplate(TemplateReaderTests.Example);
        Assert.Throws<PackageContentException>(() => TemplateInstantiator.Instantiate(document,
            new Dictionary<string, JsonNode?> { ["tamano"] = JsonValue.Create(double.NaN) }));
    }

    [Fact]
    public void Rutas_por_id_sobreviven_al_reordenamiento()
    {
        JsonObject root = TemplateReaderTests.Title();
        JsonArray layers = root["body"]!["layers"]!.AsArray();
        JsonNode first = layers[0]!;
        layers.RemoveAt(0);
        layers.Add(first);
        TemplateInstance result = TemplateInstantiator.Instantiate(ContentReaders.ReadTemplate(root.ToJsonString()),
            new Dictionary<string, JsonNode?> { ["nombre"] = JsonValue.Create("Ana") });
        Assert.Equal("cargo", result.Layers[0].Id);
        Assert.Equal("Ana", result.Layers[1].Text);
    }

    [Theory]
    [InlineData("transition", "incoming/opacity/0/value")]
    [InlineData("effect", "curves/0/keys/0/value")]
    public void Transicion_y_efecto_se_instancian_y_revalidan(string kind, string target)
    {
        JsonObject root = TemplateReaderTests.Other(kind);
        root["parameters"] = JsonNode.Parse($$"""
            [{ "id": "opacidad", "label": "Opacidad", "type": "number", "min": 0, "max": 2, "targets": ["{{target}}"] }]
            """);
        TemplateDocument document = ContentReaders.ReadTemplate(root.ToJsonString());
        TemplateInstance result = TemplateInstantiator.Instantiate(document,
            new Dictionary<string, JsonNode?> { ["opacidad"] = JsonValue.Create(0.5) });
        Assert.Equal(0.5, kind == "transition" ? result.Transition!.Incoming[0].Keys[0].Value : result.Effect!.Curves[0].Keys[0].Value);
        Assert.Throws<PackageContentException>(() => TemplateInstantiator.Instantiate(document,
            new Dictionary<string, JsonNode?> { ["opacidad"] = JsonValue.Create(1.5) }));
    }

    [Fact]
    public void Transicion_instancia_el_angulo_expuesto()
    {
        JsonObject root = TemplateReaderTests.Other("transition");
        root["parameters"] = JsonNode.Parse("""
            [{ "id": "angulo", "label": "Ángulo", "type": "number", "min": -360, "max": 360, "targets": ["mixParameters/angle"] }]
            """);
        TemplateDocument document = ContentReaders.ReadTemplate(root.ToJsonString());
        TemplateInstance result = TemplateInstantiator.Instantiate(document,
            new Dictionary<string, JsonNode?> { ["angulo"] = JsonValue.Create(45) });
        Assert.Equal(45, result.Transition!.MixParameters["angle"]);
        Assert.Equal(90, TemplateInstantiator.Instantiate(document).Transition!.MixParameters["angle"]);
    }

    [Theory]
    [InlineData("\"text\"", "\"otro\"")]
    [InlineData("true", "false")]
    public void Choice_compara_el_tipo_de_valor_incluidos_ambos_booleanos(string initial, string changed)
    {
        JsonObject root = TemplateReaderTests.Title();
        root["body"]!["opcion"] = JsonNode.Parse(initial);
        root["parameters"] = JsonNode.Parse($$"""
            [{ "id": "opcion", "label": "Opción", "type": "choice", "targets": ["opcion"],
               "options": [{ "label": "Uno", "value": {{initial}} }, { "label": "Dos", "value": {{changed}} }] }]
            """);
        TemplateDocument document = ContentReaders.ReadTemplate(root.ToJsonString());
        _ = TemplateInstantiator.Instantiate(document, new Dictionary<string, JsonNode?> { ["opcion"] = JsonNode.Parse(changed) });
        root["body"]!["opcion"] = 42;
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(root.ToJsonString()));
    }

    [Fact]
    [Trait("Category", "Rendimiento")]
    [Trait("Categoria", "TiempoReal")]
    public void Instanciar_y_validar_16_capas_1000_veces_mediana_hasta_2_ms()
    {
        JsonObject root = TemplateReaderTests.Title(16);
        root["parameters"] = JsonNode.Parse("""
            [{ "id": "texto", "label": "Texto", "type": "text", "targets": ["layers/capa-0/text", "layers/capa-15/text"] }]
            """);
        TemplateDocument document = ContentReaders.ReadTemplate(root.ToJsonString());
        var values = new Dictionary<string, JsonNode?> { ["texto"] = JsonValue.Create("Ana Ruiz") };
        for (int i = 0; i < 100; i++) _ = TemplateInstantiator.Instantiate(document, values);
        var times = new double[1000];
        for (int i = 0; i < times.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            TemplateInstance result = TemplateInstantiator.Instantiate(document, values);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(16, result.Layers.Count);
            Assert.Equal("Ana Ruiz", result.Layers[15].Text);
        }

        Array.Sort(times);
        double median = (times[499] + times[500]) / 2;
        output.WriteLine($"Mediana (16 capas, 1000 instancias y validación): {median:F6} ms");
        Assert.True(median <= 2, $"Mediana: {median:F6} ms; máximo: 2 ms");
    }
}

// Las mediciones de CPU se ejecutan sin competir con el fuzz ni con el lector de LUT 65³.
[CollectionDefinition("Plantillas", DisableParallelization = true)]
public sealed class TemplateTestsDefinition;
