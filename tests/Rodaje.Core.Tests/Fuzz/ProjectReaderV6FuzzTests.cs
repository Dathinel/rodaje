using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Tests.Fuzz;
using Xunit.Abstractions;
using static Rodaje.Core.Tests.Properties.V1Support;

namespace Rodaje.Core.Tests.Fuzz;

/// <summary>
/// Pruebas hostiles del lector con proyectos del formato 6: clips compuestos anidados, subtítulos con palabras y
/// <c>style.highlight</c>, máscaras, recorte y <c>masterGainDb</c>. Un archivo dañado abre un proyecto válido (que se guarda
/// estable y se puede desplegar con <see cref="CompoundOps.Flatten(Project, Sequence)"/>) o lanza
/// <see cref="ProjectFormatException"/>; nunca otra excepción. Mutaciones deterministas con semilla fija (<see cref="Seed"/>).
/// </summary>
public class ProjectReaderV6FuzzTests(ITestOutputHelper output)
{
    private const int Seed = 20261012;
    /// <summary>Sin pruebas largas, los primeros 1000 de los 4000 casos (los 4000 tardaban de 6 a 30 s).</summary>
    private static int Cases => FuzzRunner.Long ? 4000 : 1000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// El proyecto semilla: un compuesto de video y audio con una transición dentro, otro compuesto que contiene al primero
    /// (dos niveles), un subtítulo con palabras y resaltado, una máscara con keyframes, un recorte 9:16 y ganancia maestra.
    /// </summary>
    internal static string SeedProject()
    {
        static Time S(double s) => Time.FromSeconds(s);
        var project = new Project
        {
            Id = "proyecto",
            Name = "Semilla v6",
            Media = [Media("media-1", 60)],
            Sequences =
            [
                new Sequence
                {
                    Id = "sequence-main",
                    Name = "Principal",
                    MasterGainDb = -3.5,
                    VideoTracks =
                    [
                        new Track
                        {
                            Id = "track-v1", Kind = TrackKind.Video, Name = "Video 1",
                            Clips =
                            [
                                new Clip { Id = "clip-a", MediaId = "media-1", Start = S(0), SourceIn = S(0), SourceOut = S(4), LinkId = "link-a" },
                                new Clip { Id = "clip-b", MediaId = "media-1", Start = S(4), SourceIn = S(10), SourceOut = S(16) },
                                new Clip { Id = "clip-c", MediaId = "media-1", Start = S(12), SourceIn = S(30), SourceOut = S(35) },
                            ],
                            Transitions = [new TransitionInstance { Id = "transition-1", Type = BuiltInTransitions.Crossfade, From = "clip-a", To = "clip-b", Duration = S(1) }],
                        },
                        new Track
                        {
                            Id = "track-v2", Kind = TrackKind.Video, Name = "Subtítulos", Role = TrackRoles.Subtitles,
                            Clips =
                            [
                                new Clip
                                {
                                    Id = "clip-sub", Start = S(1), SourceIn = S(0), SourceOut = S(2),
                                    Generator = new ClipGenerator
                                    {
                                        Text = "Hola a\ntodos",
                                        Style = new TextStyle { Highlight = new StyleHighlight { Color = "#FFD400" } },
                                        Words =
                                        [
                                            new GeneratorWord { Start = S(0), End = S(0.4), Text = "Hola" },
                                            new GeneratorWord { Start = S(0.5), End = S(0.7), Text = "a" },
                                            new GeneratorWord { Start = S(0.8), End = S(1.5), Text = "todos" },
                                        ],
                                    },
                                },
                            ],
                        },
                    ],
                    AudioTracks =
                    [
                        new Track
                        {
                            Id = "track-a1", Kind = TrackKind.Audio, Name = "Audio 1",
                            Clips = [new Clip { Id = "clip-a-audio", MediaId = "media-1", Start = S(0), SourceIn = S(0), SourceOut = S(4), LinkId = "link-a" }],
                        },
                    ],
                },
            ],
        };

        var ids = new SequentialIdGenerator();
        const string seq = "sequence-main";
        var mask = Edits.AddMask(seq, "clip-c", MaskShape.Ellipse, ids: ids);
        mask.Apply(project);
        Edits.SetKeyframe(seq, "clip-c", mask.CreatedIds[0], BuiltInEffects.MaskParameters.X, S(30), 0.2, Interpolation.EaseInOut, ids).Apply(project);
        Edits.SetKeyframe(seq, "clip-c", mask.CreatedIds[0], BuiltInEffects.MaskParameters.X, S(34), 0.8, ids: ids).Apply(project);
        var crop = Edits.AddEffect(seq, "clip-c", BuiltInEffects.Crop, ids: ids);
        crop.Apply(project);
        Edits.SetEffectOption(seq, "clip-c", crop.CreatedIds[0], BuiltInEffects.CropOptions.Aspect, "9:16", ids).Apply(project);
        Edits.SetParameter(seq, "clip-c", crop.CreatedIds[0], BuiltInEffects.CropParameters.Left, 0.3, S(30), ids: ids).Apply(project);
        Edits.SetParameter(seq, "clip-c", crop.CreatedIds[0], BuiltInEffects.CropParameters.Right, 0.3, S(30), ids: ids).Apply(project);
        var inner = Edits.CreateCompound(seq, ["clip-a", "clip-b"], "Compuesto 1", ids);
        inner.Apply(project);
        var outer = Edits.CreateCompound(seq, [inner.CreatedIds[0]], "Compuesto 2", ids);
        outer.Apply(project);
        Assert.Empty(ModelValidator.Validate(project));
        return ProjectSerializer.Serialize(project);
    }

    [Fact]
    public void Seed_OpensWithTwoLevelsOfCompounds()
    {
        Project project = ProjectSerializer.Deserialize(SeedProject());
        Assert.Equal(Project.CurrentSchemaVersion, project.SchemaVersion);
        Assert.Equal(3, project.Sequences.Count);
        Assert.Equal(-3.5, project.Sequences[0].MasterGainDb);
        Assert.Equal(2, ModelValidator.CompoundDepth(project, project.Sequences[0].Id, []));
        Clip sub = project.Sequences[0].GetClip("clip-sub").Clip;
        Assert.Equal("#FFD400", sub.Generator!.Style!.Highlight!.Color);
        Assert.Equal(3, sub.Generator.Words!.Count);
    }

    /// <summary>Mutaciones dirigidas a ids, generadores, <c>sequenceId</c>, <c>style.highlight</c> y <c>masterGainDb</c>, mezcladas con otras al azar.</summary>
    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void V6Mutations_GiveControlledOutcome()
    {
        string json = SeedProject();
        var random = new Random(Seed);
        var stats = new Stats();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < Cases; i++)
        {
            JsonNode root = JsonNode.Parse(json)!;
            int edits = 1 + random.Next(3);
            var applied = new List<string>();
            for (int e = 0; e < edits; e++)
            {
                applied.Add(Mutate(root, random));
            }

            string mutated = root.ToJsonString();
            Run(Inv($"caso {i} ({string.Join(", ", applied)})"), mutated, stats);
        }

        string report = Inv($"total {stats.Total}: abrieron {stats.Opened}, validador {stats.Validator}, tipos {stats.Types}, JSON {stats.Parser}");
        output.WriteLine(report);
        Assert.True(watch.Elapsed < Budget, Inv($"Tardó {watch.Elapsed.TotalSeconds:F1} s: {report}"));
        Assert.True(stats.Opened >= Cases / 20, "Muy pocos casos abren: " + report);
        Assert.True(stats.Validator >= Cases / 4, "Muy pocos casos llegan al validador: " + report);
    }

    /// <summary>Casos escritos a mano: ciclos, autorreferencias y compuestos con medio, velocidad o generador.</summary>
    [Theory]
    [InlineData("self")]
    [InlineData("cycle")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("withMedia")]
    [InlineData("withGenerator")]
    [InlineData("withSpeed")]
    [InlineData("onSubtitleTrack")]
    [InlineData("masterNaN")]
    [InlineData("masterHuge")]
    [InlineData("highlightBad")]
    [InlineData("highlightNullColor")]
    [InlineData("wordsUnordered")]
    [InlineData("wordsNullItem")]
    [InlineData("wordWithSpace")]
    public void HandwrittenV6Cases_AreRejected(string kind)
    {
        JsonNode root = JsonNode.Parse(SeedProject())!;
        JsonObject main = Objects(root["sequences"]).First();
        JsonObject compound = Compounds(root).First(c => Objects(main["videoTracks"]).SelectMany(t => Objects(t["clips"])).Contains(c));
        JsonObject nested = Objects(root["sequences"]).First(s => (string?)s["id"] == (string?)compound["sequenceId"]);
        JsonObject innerCompound = Objects(nested["videoTracks"]).SelectMany(t => Objects(t["clips"])).First(c => c["sequenceId"] is not null);
        JsonObject sub = Clips(root).First(c => (string?)c["id"] == "clip-sub");
        JsonObject generator = sub["generator"]!.AsObject();
        switch (kind)
        {
            case "self": innerCompound["sequenceId"] = nested["id"]!.DeepClone(); break;
            case "cycle": innerCompound["sequenceId"] = main["id"]!.DeepClone(); break;
            case "missing": compound["sequenceId"] = "sequence-x"; break;
            case "empty": compound["sequenceId"] = ""; break;
            case "withMedia": compound["mediaId"] = "media-1"; break;
            case "withGenerator": compound["generator"] = generator.DeepClone(); break;
            case "withSpeed": compound["speed"] = new JsonObject { ["rate"] = new JsonObject { ["num"] = 2, ["den"] = 1 } }; compound["duration"] = compound["sourceOut"]!.GetValue<long>() / 2; break;
            case "onSubtitleTrack": sub["sequenceId"] = compound["sequenceId"]!.DeepClone(); break;
            case "masterNaN": main["masterGainDb"] = "NaN"; break;
            case "masterHuge": main["masterGainDb"] = 1e308; break;
            case "highlightBad": generator["style"]!["highlight"] = new JsonObject { ["color"] = "#GG0000" }; break;
            case "highlightNullColor": generator["style"]!["highlight"] = new JsonObject { ["color"] = null }; break;
            case "wordsUnordered": generator["words"]!.AsArray().Add(new JsonObject { ["start"] = 0L, ["end"] = 1L, ["text"] = "x" }); break;
            case "wordsNullItem": generator["words"]!.AsArray().Add(null); break;
            case "wordWithSpace": generator["words"]![0]!["text"] = "dos palabras"; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }

        string text = root.ToJsonString();
        Run(kind, text, new Stats());
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(text));
    }

    /// <summary>Un compuesto con <c>sequenceId</c> nulo y sin medio ni generador no es nada: se rechaza.</summary>
    [Fact]
    public void CompoundWithNullSequenceId_IsRejected()
    {
        JsonNode root = JsonNode.Parse(SeedProject())!;
        Compounds(root).First()["sequenceId"] = null;
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));
    }

    private static IEnumerable<JsonObject> Clips(JsonNode root) =>
        Objects(root["sequences"]).SelectMany(s => Objects(s["videoTracks"]).Concat(Objects(s["audioTracks"]))).SelectMany(t => Objects(t["clips"]));

    private static List<JsonObject> Compounds(JsonNode root) => [.. Clips(root).Where(c => c["sequenceId"] is JsonValue)];

    private static readonly JsonNode?[] Hostile =
    [
        null, 0, -1, 1, long.MaxValue, long.MinValue, 1e308, -1e308, 0.5, "", "x", "NaN", "Infinity", true, false,
        new JsonArray(), new JsonObject(), new JsonArray(1, "a", null), new JsonObject { ["a"] = 1 }, "a\u0001b", new string('z', 5000),
    ];

    private static JsonNode? Pick(Random rng, params JsonNode?[] options) => options[rng.Next(options.Length)]?.DeepClone();

    /// <summary>Aplica una mutación y dice cuál fue.</summary>
    private static string Mutate(JsonNode root, Random rng)
    {
        List<JsonObject> sequences = [.. Objects(root["sequences"])];
        List<JsonObject> clips = [.. Clips(root)];
        List<JsonObject> compounds = Compounds(root);
        List<JsonObject> generators = [.. clips.Select(c => c["generator"]).OfType<JsonObject>()];
        var withId = new List<JsonObject>(sequences);
        withId.AddRange(Objects(root["media"]));
        foreach (JsonObject s in sequences)
        {
            foreach (JsonObject t in Objects(s["videoTracks"]).Concat(Objects(s["audioTracks"])))
            {
                withId.Add(t);
                withId.AddRange(Objects(t["transitions"]));
                foreach (JsonObject c in Objects(t["clips"]))
                {
                    withId.Add(c);
                    withId.AddRange(Objects(c["effects"]));
                }
            }
        }

        string[] sequenceIds = [.. sequences.Select(s => s["id"] is JsonValue v && v.TryGetValue(out string? id) ? id : "")];
        switch (rng.Next(12))
        {
            case 0:
            {
                // Ids: repetidos entre entidades, vacíos, con control, larguísimos, de otro tipo.
                JsonObject target = withId[rng.Next(withId.Count)];
                target["id"] = rng.Next(3) == 0 ? Pick(rng, "", "a\u0000", " ", new string('i', 70_000), 5, null) : withId[rng.Next(withId.Count)]["id"]?.DeepClone();
                return "id";
            }

            case 1:
            case 2:
            {
                // sequenceId: otra secuencia (ciclos), la propia, una que no existe, vacío, nulo o de otro tipo.
                JsonObject clip = compounds.Count > 0 && rng.Next(4) != 0 ? compounds[rng.Next(compounds.Count)] : clips[rng.Next(clips.Count)];
                clip["sequenceId"] = rng.Next(4) switch
                {
                    0 => Pick(rng, "no-existe", "", null, 3, new JsonArray()),
                    _ => sequenceIds[rng.Next(sequenceIds.Length)],
                };
                return "sequenceId";
            }

            case 3:
            {
                // Un compuesto que además lleva medio, generador o velocidad, o que se estira más allá de su secuencia.
                if (compounds.Count == 0)
                {
                    return "nada";
                }

                JsonObject clip = compounds[rng.Next(compounds.Count)];
                switch (rng.Next(4))
                {
                    case 0: clip["mediaId"] = "media-1"; break;
                    case 1: clip["generator"] = new JsonObject { ["kind"] = "text", ["text"] = "x", ["style"] = new JsonObject() }; break;
                    case 2: clip["speed"] = new JsonObject { ["rate"] = new JsonObject { ["num"] = 1, ["den"] = 2 } }; break;
                    default: clip["sourceOut"] = (clip["sourceOut"] is JsonValue o && o.TryGetValue(out long end) ? end : 0) * (1 + rng.Next(1000)); break;
                }

                return "compuesto";
            }

            case 4:
            case 5:
            {
                // Generadores: tipo, texto, palabras.
                if (generators.Count == 0)
                {
                    return "nada";
                }

                JsonObject g = generators[rng.Next(generators.Count)];
                switch (rng.Next(6))
                {
                    case 0: g["kind"] = Pick(rng, "shape", "", null, 1); break;
                    case 1: g["text"] = Pick(rng, "", "a\u0007b", "x\u2028y", new string('a', 10_001), string.Concat(Enumerable.Repeat("l\n", 201)), null); break;
                    case 2: g["words"] = Pick(rng, null, new JsonArray(), new JsonObject(), "palabras"); break;
                    case 3:
                        if (g["words"] is JsonArray words && words.Count > 0)
                        {
                            JsonNode? word = words[rng.Next(words.Count)];
                            if (word is JsonObject w)
                            {
                                string key = Pick(rng, "start", "end", "text")!.GetValue<string>();
                                w[key] = key == "text" ? Pick(rng, "", "a b", "a\tb", new string('w', 101), null, 7) : Pick(rng, -1L, long.MaxValue, long.MinValue, 254_016_000_000_001L, 0L, null, "0");
                            }
                        }

                        break;
                    case 4:
                        if (g["words"] is JsonArray list)
                        {
                            for (int k = 0; k < (rng.Next(2) == 0 ? 1 : 2001); k++)
                            {
                                list.Add(new JsonObject { ["start"] = rng.Next(3) * 705_600_000L, ["end"] = rng.Next(3) * 705_600_000L, ["text"] = "w" });
                            }
                        }

                        break;
                    default:
                        g["style"] = Pick(rng, null, new JsonObject(), "estilo", new JsonArray());
                        break;
                }

                return "generador";
            }

            case 6:
            case 7:
            {
                // style.highlight y su color.
                JsonObject? style = generators.Select(g => g["style"]).OfType<JsonObject>().FirstOrDefault();
                if (style is null)
                {
                    return "nada";
                }

                style["highlight"] = rng.Next(3) == 0
                    ? Pick(rng, null, "#FF0000", 1, new JsonArray(), new JsonObject())
                    : new JsonObject { ["color"] = Pick(rng, "#FFD400", "#ffd400cc", "#FFF", "#GG0000", "", "FFD400", "#FFD40", "#FFD400FF00", null, 5, "#FFD400\u0000") };
                return "highlight";
            }

            case 8:
            case 9:
            {
                // masterGainDb.
                JsonObject s = sequences[rng.Next(sequences.Count)];
                s["masterGainDb"] = Pick(rng, -60, -60.0001, 24, 24.0001, 0, -0.0, 12.345678, 1e308, -1e308, 5e-324, "NaN", "Infinity", "3", null, true, new JsonObject(), new JsonArray());
                return "masterGainDb";
            }

            default:
            {
                // Cualquier valor de cualquier objeto o lista.
                var nodes = new List<JsonNode>();
                Collect(root, nodes, 0);
                JsonNode target = nodes[rng.Next(nodes.Count)];
                if (target is JsonObject obj && obj.Count > 0)
                {
                    string key = obj.ElementAt(rng.Next(obj.Count)).Key;
                    if (rng.Next(3) == 0)
                    {
                        obj.Remove(key);
                    }
                    else
                    {
                        obj[key] = Pick(rng, Hostile);
                    }
                }
                else if (target is JsonArray arr && arr.Count > 0)
                {
                    if (rng.Next(2) == 0)
                    {
                        arr.Add(arr[rng.Next(arr.Count)]?.DeepClone());
                    }
                    else
                    {
                        arr.RemoveAt(rng.Next(arr.Count));
                    }
                }

                return "al azar";
            }
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

    private sealed class Stats
    {
        public int Total { get; set; }

        public int Opened { get; set; }

        public int Parser { get; set; }

        public int Types { get; set; }

        public int Validator { get; set; }
    }

    /// <summary>
    /// Abre o lanza <see cref="ProjectFormatException"/> cuya causa es del lector (JSON, tipos) o del validador con
    /// problemas concretos; un fallo interno envuelto como error controlado también es un defecto.
    /// </summary>
    private static void Run(string label, string input, Stats stats)
    {
        stats.Total++;
        Project project;
        try
        {
            project = ProjectSerializer.Deserialize(input);
        }
        catch (ProjectFormatException ex)
        {
            Exception cause = ex;
            while (cause.InnerException is ProjectFormatException)
            {
                cause = cause.InnerException;
            }

            switch (cause.InnerException)
            {
                case null:
                    if (((ProjectFormatException)cause).Problems.Count > 0)
                    {
                        stats.Validator++;
                    }
                    else
                    {
                        stats.Parser++;
                    }

                    break;
                case JsonException je:
                    if (je.Path is { Length: > 1 })
                    {
                        stats.Types++;
                    }
                    else
                    {
                        stats.Parser++;
                    }

                    break;
                case Exception inner:
                    throw new Xunit.Sdk.XunitException(
                        $"{label}: el validador o el lector falló por dentro ({inner.GetType().Name}: {inner.Message})\n{Truncate(inner.ToString())}\n{Truncate(input)}");
            }

            return;
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"{label}: excepción no controlada {ex.GetType().Name}: {ex.Message}\n{Truncate(ex.ToString())}\n{Truncate(input)}");
        }

        stats.Opened++;
        Assert.True(ModelValidator.Validate(project).Count == 0, $"{label}: abrió pero no pasa el validador\n{Truncate(input)}");
        string saved = ProjectSerializer.Serialize(project);
        Assert.True(saved == ProjectSerializer.Serialize(ProjectSerializer.Deserialize(saved)), $"{label}: guardar y abrir no es estable\n{Truncate(input)}");
        foreach (Sequence sequence in project.Sequences)
        {
            Sequence flat = CompoundOps.Flatten(project, sequence);
            Assert.DoesNotContain(flat.AllTracks.SelectMany(t => t.Clips), c => c.IsCompound);
        }
    }

    private static string Truncate(string text) => text.Length <= 800 ? text : text[..800] + "…";
}
