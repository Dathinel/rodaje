using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>
/// Un número del archivo que desborda <see cref="double"/> (<c>1e400</c>) se leía como infinito donde el validador no
/// acota (un parámetro que el efecto no conoce, o un efecto de un complemento que no está): el proyecto se abría, pero el
/// guardado lo rechaza desde la tarea 213, así que se editaba un proyecto que ya no se podía guardar (tarea 226). Ahora se
/// rechaza al abrir, con la ruta del dato, como al guardar.
/// </summary>
public sealed class OverflowingNumbersTests
{
    private const int Seeds = 12;

    private static readonly string[] Overflows = ["1e400", "-1e400", "1.8e308", "-17976931348623159e292", "1E+999"];

    /// <summary>
    /// Semillas 1 a 12 y el proyecto de muestra completo: cada número del archivo, uno a uno, cambiado por uno que
    /// desborda, hace que abrir falle con un <see cref="ProjectFormatException"/> que dice dónde está.
    /// </summary>
    [Fact]
    public void EveryNumberInTheFile_WhenItOverflows_IsRejectedOnOpenWithItsPath()
    {
        var projects = new List<(string Name, Project Project)> { ("muestra", SerializationSamples.Rich()) };
        for (int seed = 1; seed <= Seeds; seed++)
        {
            projects.Add(($"semilla {seed}", RoundTripProjects.Generate(seed)));
        }

        var rng = new Random(226);
        int checkedNumbers = 0;
        foreach ((string name, Project project) in projects)
        {
            string json = ProjectSerializer.Serialize(project);
            int count = Numbers(JsonNode.Parse(json)!).Count;
            Assert.True(count > 0);
            for (int i = 0; i < count; i++)
            {
                string overflow = Overflows[rng.Next(Overflows.Length)];
                JsonNode root = JsonNode.Parse(json)!;
                JsonValue target = Numbers(root)[i];
                string path = target.GetPath();
                Replace(target, overflow);

                ProjectFormatException? ex = Record.Exception(() => ProjectSerializer.Deserialize(root.ToJsonString())) as ProjectFormatException;

                Assert.True(ex is not null, $"{name}: «{path}» = {overflow} se abrió.");
                // La versión la lee el migrador antes que nada y su aviso no lleva ruta.
                Assert.True(
                    path == "$.schemaVersion" || NamesThePlace(ex.Message, path),
                    $"{name}: se esperaba «{path}» en «{ex.Message}».");
                checkedNumbers++;
            }
        }

        Assert.True(checkedNumbers > 500, $"Solo se comprobaron {checkedNumbers} números.");
    }

    /// <summary>Los dos casos de la tarea: un parámetro que el efecto no conoce y un efecto de tipo desconocido.</summary>
    [Theory]
    [InlineData("rodaje.opacity", "1e400")]
    [InlineData("rodaje.opacity", "-1e400")]
    [InlineData("complemento.desconocido", "1e400")]
    [InlineData("complemento.desconocido", "-1e400")]
    public void UnboundedParameter_ThatOverflows_IsRejectedOnOpen(string type, string overflow)
    {
        Project project = SerializationSamples.Rich();
        project.Sequences[0].MasterEffects.Add(new EffectInstance
        {
            Id = "fx-extra",
            Type = type,
            Parameters = { ["extra"] = new Parameter { Value = 7.25 } },
        });
        string json = ProjectSerializer.Serialize(project);
        Project opened = ProjectSerializer.Deserialize(json);
        Assert.Equal(7.25, opened.Sequences[0].MasterEffects[^1].Parameters["extra"].Value);
        string broken = json.Replace("\"value\": 7.25", $"\"value\": {overflow}", StringComparison.Ordinal);
        Assert.NotEqual(json, broken);

        ProjectFormatException ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(broken));

        int last = project.Sequences[0].MasterEffects.Count - 1;
        Assert.Contains($"«$.sequences[0].masterEffects[{last}].parameters.extra.value»", ex.Message, StringComparison.Ordinal);
        Assert.Contains("desborda", ex.Message, StringComparison.Ordinal);
        Assert.IsType<JsonException>(ex.InnerException);
    }

    /// <summary>
    /// NaN e infinito escritos como literales no son JSON y se rechazan al analizar; escritos como texto, el lector no
    /// los convierte en número.
    /// </summary>
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-Infinity\"")]
    public void NamedNonFiniteValues_AreRejectedOnOpen(string literal)
    {
        Project project = SerializationSamples.Rich();
        project.Sequences[0].MasterEffects.Add(new EffectInstance
        {
            Id = "fx-extra",
            Type = "complemento.desconocido",
            Parameters = { ["extra"] = new Parameter { Value = 7.25 } },
        });
        string json = ProjectSerializer.Serialize(project).Replace("\"value\": 7.25", $"\"value\": {literal}", StringComparison.Ordinal);

        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json));
    }

    /// <summary>Lo que cabe sigue abriendo igual: los extremos finitos y los subnormales no se tocan.</summary>
    [Theory]
    [InlineData("1.7976931348623157e308", double.MaxValue)]
    [InlineData("-1.7976931348623157e308", -double.MaxValue)]
    [InlineData("5e-324", double.Epsilon)]
    [InlineData("1e-400", 0.0)]
    public void FiniteExtremes_StillOpen(string literal, double expected)
    {
        Project project = SerializationSamples.Rich();
        project.Sequences[0].MasterEffects.Add(new EffectInstance
        {
            Id = "fx-extra",
            Type = "complemento.desconocido",
            Parameters = { ["extra"] = new Parameter { Value = 7.25 } },
        });
        string json = ProjectSerializer.Serialize(project).Replace("\"value\": 7.25", $"\"value\": {literal}", StringComparison.Ordinal);

        Project opened = ProjectSerializer.Deserialize(json);

        Assert.Equal(expected, opened.Sequences[0].MasterEffects[^1].Parameters["extra"].Value);
    }

    /// <summary>
    /// El mensaje dice la ruta del número o la del valor que lo contiene: las tasas (<c>{num, den}</c>) se leen enteras y
    /// el lector señala la tasa.
    /// </summary>
    private static bool NamesThePlace(string message, string path)
    {
        for (string place = path; place.Length > 1; place = place[..place.LastIndexOfAny(['.', '['])])
        {
            if (message.Contains($"«{place}»", StringComparison.Ordinal))
            {
                return place == path || path[place.Length..].Count(c => c is '.' or '[') == 1;
            }
        }

        return false;
    }

    /// <summary>Los números del árbol en orden de documento; el mismo texto da siempre la misma lista.</summary>
    private static List<JsonValue> Numbers(JsonNode root)
    {
        var numbers = new List<JsonValue>();

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach ((_, JsonNode? child) in obj)
                    {
                        Walk(child);
                    }

                    break;
                case JsonArray array:
                    foreach (JsonNode? item in array)
                    {
                        Walk(item);
                    }

                    break;
                case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                    numbers.Add(value);
                    break;
            }
        }

        Walk(root);
        return numbers;
    }

    private static void Replace(JsonValue target, string number)
    {
        JsonNode replacement = JsonNode.Parse(number)!;
        switch (target.Parent)
        {
            case JsonObject obj:
                obj[target.GetPropertyName()] = replacement;
                break;
            case JsonArray array:
                array[target.GetElementIndex()] = replacement;
                break;
            default:
                throw new InvalidOperationException(target.GetPath());
        }
    }
}
