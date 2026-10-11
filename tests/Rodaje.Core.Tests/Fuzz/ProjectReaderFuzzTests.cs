using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Fuzz;

/// <summary>
/// Pruebas hostiles del lector de proyectos: un <c>.rodaje</c> dañado o malicioso abre un proyecto válido
/// o lanza <see cref="ProjectFormatException"/>; nunca otra excepción ni un cuelgue. Mutaciones
/// deterministas (semilla fija) sobre los ejemplos v1, v2 y v3.
/// </summary>
public class ProjectReaderFuzzTests
{
    private const int Seed = 20261011;
    private const int CasesPerSeedFile = 2000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static string ExamplePath =>
        Path.Combine(Serialization.ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Serialization", "ejemplos", "proyecto-v3.rodaje");

    /// <summary>Semillas v1, v2 y v3; las dos primeras se obtienen quitando del v3 lo que esos formatos no tenían.</summary>
    private static IReadOnlyList<(string Name, string Json)> Seeds()
    {
        string v3 = File.ReadAllText(ExamplePath);
        return [("v1", Downgrade(v3, 1)), ("v2", Downgrade(v3, 2)), ("v3", v3)];
    }

    private static string Downgrade(string v3, int version)
    {
        JsonObject root = JsonNode.Parse(v3)!.AsObject();
        root["schemaVersion"] = version;
        foreach (JsonObject sequence in Objects(root["sequences"]))
        {
            if (version == 1)
            {
                sequence.Remove("markers");
                sequence.Remove("regions");
            }

            foreach (JsonObject track in Objects(sequence["videoTracks"]).Concat(Objects(sequence["audioTracks"])))
            {
                track.Remove("effects");
                track.Remove("transitions");
                foreach (JsonObject clip in Objects(track["clips"]))
                {
                    foreach (JsonObject effect in Objects(clip["effects"]))
                    {
                        effect.Remove("options");
                    }
                }
            }
        }

        return root.ToJsonString();
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? array) =>
        array is JsonArray items ? items.OfType<JsonObject>() : [];

    [Fact]
    public void Seeds_AllOpen()
    {
        foreach ((string name, string json) in Seeds())
        {
            Project project = ProjectSerializer.Deserialize(json);
            Assert.Empty(ModelValidator.Validate(project));
            Assert.True(project.Sequences.Count > 0, name);
        }
    }

    [Fact]
    public void ByteMutations_NeverCrashTheReader()
    {
        var random = new Random(Seed);
        RunBudgeted(Seeds(), CasesPerSeedFile, (json, rng) =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            return Encoding.UTF8.GetString(MutateBytes(bytes, rng));
        }, random);
    }

    [Fact]
    public void StructuralMutations_NeverCrashTheReader()
    {
        var random = new Random(Seed + 1);
        RunBudgeted(Seeds(), CasesPerSeedFile, (json, rng) =>
        {
            JsonNode root = JsonNode.Parse(json)!;
            int edits = 1 + rng.Next(3);
            for (int i = 0; i < edits; i++)
            {
                MutateNode(root, rng);
            }

            return root.ToJsonString();
        }, random);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"texto\"")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":3,\"schemaVersion\":3}")]
    [InlineData("{\"schemaVersion\":4}")]
    [InlineData("{\"schemaVersion\":99999999999999999999}")]
    [InlineData("{\"schemaVersion\":-1}")]
    [InlineData("{\"id\":\"p\"}")]
    [InlineData("{\"schemaVersion\":3,\"sequences\":null}")]
    [InlineData("{\"schemaVersion\":3,\"sequences\":[null]}")]
    [InlineData("{\"schemaVersion\":3,\"sequences\":[[]]}")]
    [InlineData("{\"schemaVersion\":3,\"sequences\":\"x\"}")]
    [InlineData("{\"schemaVersion\":1,\"sequences\":{\"markers\":1}}")]
    [InlineData("{\"schemaVersion\":2,\"sequences\":[{\"videoTracks\":[{\"effects\":[1,null,\"x\",{\"type\":5}]}]}]}")]
    [InlineData("{\"schemaVersion\":2,\"sequences\":[{\"videoTracks\":[{\"clips\":[{\"effects\":[{\"type\":\"rodaje.volume\",\"parameters\":{\"gain\":{\"value\":\"x\",\"keyframes\":[1,{\"value\":null}]}}}]}]}]}]}")]
    public void HandwrittenHostileInputs_GiveControlledOutcome(string json) => AssertControlled(json, "manual");

    [Fact]
    public void HugeStringsAndDeepNesting_GiveControlledOutcome()
    {
        string big = new('a', 1_000_000);
        AssertControlled($$"""{"schemaVersion":3,"id":"{{big}}","name":"{{big}}"}""", "cadena 1 MB");

        string deep = new string('[', 5000) + new string(']', 5000);
        AssertControlled($$"""{"schemaVersion":3,"sequences":{{deep}}}""", "anidamiento 5000");

        string deepObj = string.Concat(Enumerable.Repeat("{\"a\":", 5000)) + "1" + new string('}', 5000);
        AssertControlled($$"""{"schemaVersion":3,"extra":{{deepObj}}}""", "objetos anidados");
    }

    [Fact]
    public void InvalidUtf8AndTruncation_GiveControlledOutcome()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Seeds()[2].Json);
        for (int cut = 0; cut < bytes.Length; cut += 97)
        {
            AssertControlled(Encoding.UTF8.GetString(bytes, 0, cut), $"truncado en {cut}");
        }

        AssertControlled(Encoding.Latin1.GetString([0xFF, 0xFE, 0x80, 0x7B, 0x7D]), "bytes inválidos");
    }

    private static void RunBudgeted(
        IReadOnlyList<(string Name, string Json)> seeds,
        int casesPerSeed,
        Func<string, Random, string> mutate,
        Random random)
    {
        var watch = Stopwatch.StartNew();
        int total = 0;
        int opened = 0;
        foreach ((string name, string json) in seeds)
        {
            for (int i = 0; i < casesPerSeed; i++)
            {
                string mutated = mutate(json, random);
                if (AssertControlled(mutated, $"{name} caso {i}"))
                {
                    opened++;
                }

                total++;
            }
        }

        Assert.True(total >= 5000 / 2, $"Pocos casos: {total}");
        Assert.True(watch.Elapsed < Budget, $"Tardó {watch.Elapsed.TotalSeconds:F1} s para {total} casos ({opened} abrieron).");
    }

    /// <summary>Devuelve true si el texto abrió; en ese caso comprueba validador y ciclo guardar/abrir.</summary>
    private static bool AssertControlled(string json, string label)
    {
        Project project;
        try
        {
            project = ProjectSerializer.Deserialize(json);
        }
        catch (ProjectFormatException ex)
        {
            Assert.False(string.IsNullOrWhiteSpace(ex.Message), $"{label}: mensaje vacío");
            return false;
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"{label}: excepción no controlada {ex.GetType().Name}: {ex.Message}\n{Truncate(json)}");
        }

        Assert.True(ModelValidator.Validate(project).Count == 0, $"{label}: abrió pero no pasa el validador\n{Truncate(json)}");
        string saved = ProjectSerializer.Serialize(project);
        Project again = ProjectSerializer.Deserialize(saved);
        Assert.True(saved == ProjectSerializer.Serialize(again), $"{label}: guardar y abrir no es estable\n{Truncate(json)}");
        return true;
    }

    private static string Truncate(string text) => text.Length <= 600 ? text : text[..600] + "…";

    private static byte[] MutateBytes(byte[] source, Random rng)
    {
        var bytes = new List<byte>(source);
        int edits = 1 + rng.Next(4);
        for (int i = 0; i < edits && bytes.Count > 0; i++)
        {
            int at = rng.Next(bytes.Count);
            switch (rng.Next(5))
            {
                case 0:
                    bytes[at] = (byte)rng.Next(256);
                    break;
                case 1:
                    bytes.RemoveRange(at, Math.Min(bytes.Count - at, 1 + rng.Next(40)));
                    break;
                case 2:
                    bytes.Insert(at, (byte)rng.Next(256));
                    break;
                case 3:
                    bytes.RemoveRange(at, bytes.Count - at);
                    break;
                default:
                    bytes[at] = (byte)"{}[],:\"0-9.eE"[rng.Next(13)];
                    break;
            }
        }

        return [.. bytes];
    }

    private static readonly JsonNode?[] Replacements =
    [
        null, 0, -1, 1, long.MaxValue, long.MinValue, 1e308, -1e308, 1e-320, double.MaxValue, 0.5,
        "", "x", "NaN", "Infinity", "-Infinity", "0/0", "1/0", "-5/3", "999999999999999999999/1", true, false,
        new JsonArray(), new JsonObject(), new JsonArray(1, "a", null), new JsonObject { ["a"] = 1 },
    ];

    private static void MutateNode(JsonNode root, Random rng)
    {
        var nodes = new List<JsonNode>();
        Collect(root, nodes, 0);
        JsonNode target = nodes[rng.Next(nodes.Count)];
        switch (target)
        {
            case JsonObject obj when obj.Count > 0:
                string key = obj.ElementAt(rng.Next(obj.Count)).Key;
                switch (rng.Next(4))
                {
                    case 0:
                        obj.Remove(key);
                        break;
                    case 1:
                        obj[key] = Replacements[rng.Next(Replacements.Length)]?.DeepClone();
                        break;
                    case 2:
                        obj[key + "x"] = Replacements[rng.Next(Replacements.Length)]?.DeepClone();
                        break;
                    default:
                        // Duplica el valor de otra propiedad (ids repetidos, referencias cruzadas).
                        string other = obj.ElementAt(rng.Next(obj.Count)).Key;
                        obj[key] = obj[other]?.DeepClone();
                        break;
                }

                break;
            case JsonArray arr:
                switch (arr.Count == 0 ? 3 : rng.Next(5))
                {
                    case 0:
                        arr.RemoveAt(rng.Next(arr.Count));
                        break;
                    case 1:
                        arr[rng.Next(arr.Count)] = Replacements[rng.Next(Replacements.Length)]?.DeepClone();
                        break;
                    case 2:
                        arr.Add(arr[rng.Next(arr.Count)]?.DeepClone());
                        break;
                    case 3:
                        arr.Add(Replacements[rng.Next(Replacements.Length)]?.DeepClone());
                        break;
                    default:
                        // Desordena: keyframes y clips fuera de orden.
                        JsonNode?[] items = [.. arr.Select(n => n?.DeepClone())];
                        arr.Clear();
                        foreach (JsonNode? item in items.OrderBy(_ => rng.Next()))
                        {
                            arr.Add(item);
                        }

                        break;
                }

                break;
        }
    }

    private static void Collect(JsonNode? node, List<JsonNode> into, int depth)
    {
        if (node is null || depth > 30)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            into.Add(obj);
            foreach (KeyValuePair<string, JsonNode?> pair in obj)
            {
                Collect(pair.Value, into, depth + 1);
            }
        }
        else if (node is JsonArray arr)
        {
            into.Add(arr);
            foreach (JsonNode? item in arr)
            {
                Collect(item, into, depth + 1);
            }
        }
    }
}
