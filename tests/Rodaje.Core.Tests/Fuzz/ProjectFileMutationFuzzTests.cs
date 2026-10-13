using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Tests.Fuzz;
using Xunit.Abstractions;
using CoreTime = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Fuzz;

/// <summary>
/// Fuzz de mutaciones sobre proyectos <c>.rodaje</c> de los formatos 3 a 13: bytes cambiados o truncados, tipos
/// cambiados, números enormes, cadenas de 10 MB, rutas con caracteres raros, compuestos circulares y versiones de
/// formato de la 1 a la 99. Un archivo dañado abre un proyecto válido (estable al guardar, con rutas que se resuelven y
/// compuestos que se despliegan) o lanza <see cref="ProjectFormatException"/> con un mensaje en español; nunca otra
/// excepción ni un cuelgue. Cada caso tiene su semilla (ver <see cref="FuzzRunner"/>).
/// </summary>
public class ProjectFileMutationFuzzTests(ITestOutputHelper output)
{
    private const int Seed = 94_000_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private static string Example(string name) =>
        File.ReadAllText(Path.Combine(Serialization.ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Serialization", "ejemplos", name));

    private static readonly Lazy<IReadOnlyList<(int Version, string Json)>> SeedFiles = new(BuildSeeds);

    /// <summary>
    /// Los ejemplos v3, v4 y v5 y, del 6 al 13, un proyecto con compuestos anidados, subtítulos, máscara, recorte,
    /// keyframes Bézier, inserciones de audio en orden, color y grupo de clips, transición alineada con curva y anclaje
    /// de esquinas, marcado con cada versión (del 6 al 13 las migraciones solo cambian el número, salvo el orden de las
    /// inserciones del 9 al 10).
    /// </summary>
    private static List<(int Version, string Json)> BuildSeeds()
    {
        var seeds = new List<(int, string)>
        {
            (3, Example("proyecto-v3.rodaje")), (4, Example("proyecto-v4.rodaje")), (5, Example("proyecto-v5.rodaje")),
        };
        string rich = RichProject();
        for (int version = 6; version <= Project.CurrentSchemaVersion; version++)
        {
            JsonObject root = JsonNode.Parse(rich)!.AsObject();
            root["schemaVersion"] = version;
            seeds.Add((version, root.ToJsonString()));
        }

        return seeds;
    }

    internal static string RichProject()
    {
        Project project = ProjectSerializer.Deserialize(ProjectReaderV6FuzzTests.SeedProject());
        const string seq = "sequence-main";
        var ids = new PrefixedIds();
        CoreTime S(double s) => CoreTime.FromSeconds(s);

        var transform = Edits.AddEffect(seq, "clip-c", BuiltInEffects.Transform, ids: ids);
        transform.Apply(project);
        string transformId = transform.CreatedIds[0];
        Edits.SetKeyframe(seq, "clip-c", transformId, BuiltInEffects.TransformParameters.X, S(30), -0.1, Interpolation.Linear, ids).Apply(project);
        Edits.SetKeyframe(seq, "clip-c", transformId, BuiltInEffects.TransformParameters.X, S(33), 0.1, ids: ids).Apply(project);
        Edits.SetKeyframeEase(seq, "clip-c", transformId, BuiltInEffects.TransformParameters.X, S(30), new BezierEase(0.2, 0, 0.3, 1.4), ids).Apply(project);
        Edits.AddEffect(seq, "clip-c", BuiltInEffects.CornerPin, ids: ids).Apply(project);

        Track audio = project.Sequences[0].AudioTracks[0];
        if (audio.Clips.FirstOrDefault() is { } audioClip)
        {
            Edits.AddEffect(seq, audioClip.Id, BuiltInEffects.Compressor, ids: ids).Apply(project);
            Edits.AddEffect(seq, audioClip.Id, BuiltInEffects.Equalizer, ids: ids).Apply(project);
        }

        Clip c = project.Sequences[0].GetClip("clip-c").Clip;
        c.Color = "#3A7BD5";
        c.GroupId = "group-1";
        Assert.Empty(ModelValidator.Validate(project));
        return ProjectSerializer.Serialize(project);
    }

    /// <summary>Ids que no chocan con los de la semilla v6 (que usa <see cref="SequentialIdGenerator"/>).</summary>
    private sealed class PrefixedIds : IIdGenerator
    {
        private int _next;

        public string NewId(string prefix) => string.Create(CultureInfo.InvariantCulture, $"fuzz-{prefix}-{++_next}");
    }

    [Fact]
    public void Seeds_OpenInEveryVersion()
    {
        Assert.Equal(Project.CurrentSchemaVersion - 2, SeedFiles.Value.Count);
        foreach ((int version, string json) in SeedFiles.Value)
        {
            Project project = ProjectSerializer.Deserialize(json);
            Assert.True(project.Sequences.Count > 0, $"v{version}");
        }
    }

    /// <summary>Cada semilla marcada con cada versión de la 1 a la 99 (y algunas imposibles): abre o se rechaza con claridad.</summary>
    [Fact]
    public void EveryFormatVersionFrom1To99_GivesControlledOutcome()
    {
        var stats = new Stats();
        JsonNode?[] odd = [0, -1, int.MaxValue, long.MaxValue, 1.5, 13.0, "13", true, null, new JsonArray(13), new JsonObject()];
        foreach ((int seedVersion, string json) in SeedFiles.Value)
        {
            for (int version = 1; version <= 99; version++)
            {
                JsonObject root = JsonNode.Parse(json)!.AsObject();
                root["schemaVersion"] = version;
                string text = root.ToJsonString();
                Outcome outcome = Classify($"semilla v{seedVersion} marcada v{version}", text, stats);
                if (version > Project.CurrentSchemaVersion)
                {
                    Assert.Equal(Outcome.Rejected, outcome);
                    var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(text));
                    Assert.Contains("versión más nueva", ex.Message, StringComparison.Ordinal);
                }
            }

            foreach (JsonNode? value in odd)
            {
                JsonObject root = JsonNode.Parse(json)!.AsObject();
                root["schemaVersion"] = value?.DeepClone();
                Classify($"semilla v{seedVersion} con schemaVersion {value?.ToJsonString() ?? "null"}", root.ToJsonString(), stats);
            }

            JsonObject without = JsonNode.Parse(json)!.AsObject();
            without.Remove("schemaVersion");
            Assert.Equal(Outcome.Rejected, Classify($"semilla v{seedVersion} sin schemaVersion", without.ToJsonString(), stats));
        }

        output.WriteLine(stats.ToString());
    }

    /// <summary>Mezcla de todas las mutaciones, de una a tres por caso, sobre una semilla al azar.</summary>
    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void MixedMutations_GiveControlledOutcome()
    {
        var stats = new Stats();
        int done = FuzzRunner.Run("proyectos", Seed, cases: 6000, Budget, minimumCases: 1000, (seed, rng) =>
        {
            (int version, string json) = SeedFiles.Value[rng.Next(SeedFiles.Value.Count)];
            (string text, string applied) = Mutate(json, rng);
            Classify($"semilla {seed}, v{version} ({applied})", text, stats);
        });

        output.WriteLine($"{done} casos; {stats}");
        Assert.True(stats.Opened + stats.Validator > done / 10, "Muy pocos casos pasan del analizador JSON: " + stats);
    }

    /// <summary>Bytes cambiados, insertados, borrados y truncados, leídos desde disco con <see cref="ProjectSerializer.Load"/>.</summary>
    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void ByteMutationsFromDisk_GiveControlledOutcome()
    {
        string dir = Directory.CreateTempSubdirectory("rodaje-fuzz94-").FullName;
        try
        {
            var stats = new Stats();
            FuzzRunner.Run("bytes de proyectos", Seed + 1_000_000, cases: 600, Budget, minimumCases: 200, (seed, rng) =>
            {
                (int version, string json) = SeedFiles.Value[rng.Next(SeedFiles.Value.Count)];
                byte[] bytes = MutateBytes(Encoding.UTF8.GetBytes(json), rng);
                string path = Path.Combine(dir, "caso.rodaje");
                File.WriteAllBytes(path, bytes);
                Classify($"semilla {seed}, v{version} (bytes, Load)", () => ProjectSerializer.Load(path), FuzzRunner.Truncate(Encoding.UTF8.GetString(bytes)), stats);
            });
            output.WriteLine(stats.ToString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Cadenas de 10 MB en cada clase de campo de texto (ids, nombres, rutas, tipos, colores, texto de títulos).</summary>
    [Fact]
    public void TenMegabyteStrings_GiveControlledOutcome()
    {
        string huge = new('ñ', 10 * 1024 * 1024);
        string json = SeedFiles.Value[^1].Json;
        string[] keys = ["id", "name", "path", "type", "color", "text", "mediaId", "sequenceId", "groupId", "linkId", "role", "from"];
        var stats = new Stats();
        foreach (string key in keys)
        {
            JsonNode root = JsonNode.Parse(json)!;
            List<JsonObject> owners = [.. AllObjects(root).Where(o => o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String)];
            Assert.True(owners.Count > 0 || key is "role" or "from", key);
            foreach (JsonObject owner in owners.Take(1))
            {
                owner[key] = huge;
            }

            Classify($"10 MB en «{key}»", root.ToJsonString(), stats);
        }

        JsonObject top = JsonNode.Parse(json)!.AsObject();
        top["desconocido"] = huge;
        Classify("10 MB en un campo desconocido", top.ToJsonString(), stats);
        Classify("10 MB de espacios", new string(' ', 10 * 1024 * 1024) + json, stats);
        Classify("10 MB sin cerrar", "{\"schemaVersion\":13,\"name\":\"" + huge, stats);
    }

    /// <summary>Compuestos circulares de largo 1 a 12, y uno que se contiene a sí mismo desde varias pistas.</summary>
    [Fact]
    public void CircularCompounds_AreRejected()
    {
        string json = SeedFiles.Value[^1].Json;
        var stats = new Stats();
        for (int length = 1; length <= 12; length++)
        {
            JsonObject root = JsonNode.Parse(json)!.AsObject();
            JsonArray sequences = root["sequences"]!.AsArray();
            var chain = new List<string>();
            for (int k = 0; k < length; k++)
            {
                chain.Add($"ciclo-{k}");
            }

            for (int k = 0; k < length; k++)
            {
                sequences.Add(CompoundSequence(chain[k], chain[(k + 1) % length]));
            }

            // La principal usa el primero del ciclo.
            JsonObject main = sequences[0]!.AsObject();
            main["videoTracks"]![0]!["clips"]!.AsArray().Add(CompoundClip("entrada-ciclo", chain[0], start: 7_056_000_000_000L));
            string text = root.ToJsonString();
            Assert.Equal(Outcome.Rejected, Classify($"ciclo de {length}", text, stats));
        }

        JsonObject self = JsonNode.Parse(json)!.AsObject();
        JsonObject first = self["sequences"]![0]!.AsObject();
        foreach (JsonObject track in Objects(first["videoTracks"]))
        {
            track["clips"]!.AsArray().Add(CompoundClip("auto-" + track["id"], (string)first["id"]!, start: 7_056_000_000_000L));
        }

        Assert.Equal(Outcome.Rejected, Classify("se contiene a sí misma", self.ToJsonString(), stats));
    }

    /// <summary>Rutas de medios raras: abren con la ruta resuelta o se rechazan; nunca lanzan al resolverlas.</summary>
    [Fact]
    public void OddMediaPaths_GiveControlledOutcome()
    {
        string json = SeedFiles.Value[^1].Json;
        var stats = new Stats();
        foreach (string path in OddPaths)
        {
            JsonObject root = JsonNode.Parse(json)!.AsObject();
            foreach (JsonObject media in Objects(root["media"]))
            {
                media["path"] = path;
            }

            Classify($"ruta «{FuzzRunner.Truncate(path, 80)}»", root.ToJsonString(), stats);
        }
    }

    internal static readonly string[] OddPaths =
    [
        "", " ", ".", "..", "...", "\\", "/", "//", "\\\\", "\\\\?\\", "\\\\?\\C:\\", "\\\\.\\", "\\\\.\\nul", "\\\\servidor", "\\\\servidor\\recurso",
        "C:", "C:a.mp4", "C:\\", "Z:\\no\\existe.mp4", "::", "C::\\a", "a:b:c", "a:b.mp4", "con", "CON.mp4", "nul", "aux\\a.mp4", "COM1", "lpt9.mov",
        "a.mp4.", "a.mp4 ", " a.mp4", "a*?.mp4", "a/../../../../../../etc/passwd", "..\\..\\..\\..\\Windows\\System32\\kernel32.dll",
        "file:///C:/a.mp4", "https://example.com/a.mp4", "\\??\\C:\\a.mp4", "a\u00A0b.mp4", "a\u200Bb.mp4", "a\u202Egnp.exe", "a\uFEFF.mp4",
        "😀/🎬.mp4", "日本語\\動画.mp4", "a\uFFFDb", "a\uD800b", "a\uDC00", new string('a', 300), new string('a', 33_000),
        string.Concat(Enumerable.Repeat("a\\", 20_000)), string.Concat(Enumerable.Repeat("..\\", 5000)), "a\tb", "a\u007Fb", "a\u0085b", "a\u2028b",
    ];

    // --- Clasificación ---------------------------------------------------------------------------------------------------

    private enum Outcome
    {
        Opened,
        Rejected,
    }

    private sealed class Stats
    {
        public int Opened { get; set; }

        public int Validator { get; set; }

        public int Types { get; set; }

        public int Parser { get; set; }

        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"abrieron {Opened}, validador {Validator}, tipos {Types}, analizador {Parser}");
    }

    private static Outcome Classify(string label, string json, Stats stats) =>
        Classify(label, () => ProjectSerializer.Deserialize(json), FuzzRunner.Truncate(json), stats);

    /// <summary>
    /// Abre o lanza <see cref="ProjectFormatException"/> cuya causa es del lector (JSON, tipos, UTF-8) o del validador. Un
    /// fallo interno envuelto como «no se pudieron comprobar» (NullReference, Overflow…) también es un defecto.
    /// </summary>
    private static Outcome Classify(string label, Func<Project> open, string input, Stats stats)
    {
        Project project;
        try
        {
            project = open();
        }
        catch (ProjectFormatException ex)
        {
            Exception cause = ex;
            while (cause.InnerException is ProjectFormatException)
            {
                cause = cause.InnerException;
            }

            Assert.False(string.IsNullOrWhiteSpace(ex.Message), label);
            Assert.True(
                ex.Message.Contains("proyecto", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("archivo", StringComparison.OrdinalIgnoreCase),
                $"{label}: el mensaje no explica nada: {ex.Message}");
            switch (cause.InnerException)
            {
                case null when ((ProjectFormatException)cause).Problems.Count > 0:
                    stats.Validator++;
                    break;
                case null:
                case DecoderFallbackException:
                    stats.Parser++;
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
                case InvalidOperationException when cause.Message.Contains("UTF-16", StringComparison.Ordinal):
                    stats.Parser++;
                    break;
                default:
                    throw new Xunit.Sdk.XunitException(
                        $"{label}: fallo interno envuelto como error controlado ({cause.InnerException.GetType().Name}: {cause.InnerException.Message})\n{cause.InnerException}\n{input}");
            }

            return Outcome.Rejected;
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"{label}: excepción no controlada {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n{input}");
        }

        stats.Opened++;
        CheckOpened(label, project, input);
        return Outcome.Opened;
    }

    private static void CheckOpened(string label, Project project, string input)
    {
        IReadOnlyList<string> problems = ModelValidator.Validate(project);
        Assert.True(problems.Count == 0, $"{label}: abrió pero no pasa el validador: {string.Join("; ", problems)}\n{input}");
        string saved = ProjectSerializer.Serialize(project);
        Assert.True(saved == ProjectSerializer.Serialize(ProjectSerializer.Deserialize(saved)), $"{label}: guardar y abrir no es estable\n{input}");

        // Lo que hace la app justo después de abrir: resolver las rutas contra la carpeta del proyecto.
        string folder = Path.Combine(Path.GetTempPath(), "proyecto");
        foreach (MediaRef media in project.Media)
        {
            try
            {
                _ = media.ResolvePath(folder);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"{label}: la ruta «{FuzzRunner.Truncate(media.Path, 200)}» abrió pero no se puede resolver ({ex.GetType().Name}: {ex.Message})");
            }
        }

        foreach (Sequence sequence in project.Sequences)
        {
            _ = CompoundOps.Flatten(project, sequence);
        }
    }

    // --- Mutaciones ------------------------------------------------------------------------------------------------------

    private static readonly string[] HugeNumbers =
    [
        "1e400", "-1e400", "1e308", "-1.7976931348623157e308", "4.9e-324", "1e-400", "99999999999999999999999999999", "-99999999999999999999999999999",
        "9223372036854775807", "9223372036854775808", "-9223372036854775808", "-9223372036854775809", "2147483648", "-2147483649",
        "4294967296", "18446744073709551616", "0.000000000000000000000000000001", "1E+2147483647", "123456789012345678901234567890.5", "-0", "0e0",
    ];

    private static readonly JsonNode?[] TypeSwaps =
    [
        null, true, false, 0, -1, 1, 0.5, "", "x", "0", "true", "null", "NaN", new JsonArray(), new JsonArray(null, 1, "a"), new JsonObject(),
        new JsonObject { ["num"] = 30, ["den"] = 1 }, new JsonObject { ["num"] = 0, ["den"] = 0 },
    ];

    /// <summary>Aplica de una a tres mutaciones y devuelve el texto y cuáles fueron.</summary>
    private static (string Text, string Applied) Mutate(string json, Random rng)
    {
        JsonNode root = JsonNode.Parse(json)!;
        var applied = new List<string>();
        var huge = new List<string>();
        bool bytes = false;
        int edits = 1 + rng.Next(3);
        for (int e = 0; e < edits; e++)
        {
            switch (rng.Next(16))
            {
                case 0:
                case 1:
                    applied.Add(SwapType(root, rng));
                    break;
                case 2:
                case 3:
                    applied.Add(HugeNumber(root, rng, huge));
                    break;
                case 4:
                    applied.Add(BigString(root, rng));
                    break;
                case 5:
                case 6:
                    applied.Add(OddPath(root, rng));
                    break;
                case 7:
                case 8:
                    applied.Add(Circular(root, rng));
                    break;
                case 9:
                    root["schemaVersion"] = 1 + rng.Next(99);
                    applied.Add("versión " + root["schemaVersion"]!.ToJsonString());
                    break;
                case 10:
                case 11:
                    applied.Add(Structural(root, rng));
                    break;
                case 12:
                    applied.Add(ReuseId(root, rng));
                    break;
                default:
                    bytes = true;
                    break;
            }
        }

        string text = root.ToJsonString();
        for (int i = 0; i < huge.Count; i++)
        {
            text = text.Replace($"\"§NUM{i}§\"", huge[i], StringComparison.Ordinal);
        }

        if (bytes)
        {
            applied.Add("bytes");
            byte[] mutated = MutateBytes(Encoding.UTF8.GetBytes(text), rng);
            text = Encoding.UTF8.GetString(mutated);
        }

        return (text, string.Join(", ", applied));
    }

    private static string SwapType(JsonNode root, Random rng)
    {
        List<(JsonNode Parent, string? Key, int Index)> slots = Slots(root);
        (JsonNode parent, string? key, int index) = slots[rng.Next(slots.Count)];
        JsonNode? replacement = TypeSwaps[rng.Next(TypeSwaps.Length)]?.DeepClone();
        Set(parent, key, index, replacement);
        return $"tipo {key ?? index.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string HugeNumber(JsonNode root, Random rng, List<string> huge)
    {
        List<(JsonNode Parent, string? Key, int Index)> numbers = [.. Slots(root).Where(s => Get(s) is JsonValue v && v.GetValueKind() == JsonValueKind.Number)];
        if (numbers.Count == 0)
        {
            return "nada";
        }

        (JsonNode parent, string? key, int index) = numbers[rng.Next(numbers.Count)];
        string literal = HugeNumbers[rng.Next(HugeNumbers.Length)];
        Set(parent, key, index, $"§NUM{huge.Count}§");
        huge.Add(literal);
        return $"número {key} = {literal}";
    }

    private static string BigString(JsonNode root, Random rng)
    {
        List<(JsonNode Parent, string? Key, int Index)> strings = [.. Slots(root).Where(s => Get(s) is JsonValue v && v.GetValueKind() == JsonValueKind.String)];
        (JsonNode parent, string? key, int index) = strings[rng.Next(strings.Count)];
        // Una de cada veinte, 10 MB; las demás, de 1 a 64 KiB, para que el presupuesto alcance.
        int length = rng.Next(20) == 0 ? 10 * 1024 * 1024 : 1024 << rng.Next(7);
        char fill = "a\u00F1\u4E00 \\\""[rng.Next(6)];
        Set(parent, key, index, new string(fill, length));
        return $"cadena {key} de {length}";
    }

    private static string OddPath(JsonNode root, Random rng)
    {
        List<JsonObject> media = [.. Objects(root["media"])];
        if (media.Count == 0)
        {
            return "nada";
        }

        media[rng.Next(media.Count)]["path"] = OddPaths[rng.Next(OddPaths.Length)];
        return "ruta";
    }

    private static string Circular(JsonNode root, Random rng)
    {
        List<JsonObject> sequences = [.. Objects(root["sequences"])];
        List<JsonObject> clips = [.. sequences.SelectMany(s => Objects(s["videoTracks"]).Concat(Objects(s["audioTracks"]))).SelectMany(t => Objects(t["clips"]))];
        if (sequences.Count == 0 || clips.Count == 0)
        {
            return "nada";
        }

        JsonObject clip = clips[rng.Next(clips.Count)];
        clip["sequenceId"] = sequences[rng.Next(sequences.Count)]["id"]?.DeepClone();
        if (rng.Next(2) == 0)
        {
            clip.Remove("mediaId");
            clip.Remove("generator");
        }

        return "compuesto circular";
    }

    private static string ReuseId(JsonNode root, Random rng)
    {
        List<JsonObject> withId = [.. AllObjects(root).Where(o => o["id"] is JsonValue)];
        if (withId.Count < 2)
        {
            return "nada";
        }

        withId[rng.Next(withId.Count)]["id"] = withId[rng.Next(withId.Count)]["id"]!.DeepClone();
        return "id repetido";
    }

    private static string Structural(JsonNode root, Random rng)
    {
        List<JsonNode> containers = [.. AllNodes(root).Where(n => n is JsonObject or JsonArray)];
        JsonNode target = containers[rng.Next(containers.Count)];
        switch (target)
        {
            case JsonObject obj when obj.Count > 0:
                string key = obj.ElementAt(rng.Next(obj.Count)).Key;
                obj.Remove(key);
                return "sin " + key;
            case JsonArray arr when arr.Count > 0:
                switch (rng.Next(3))
                {
                    case 0:
                        arr.RemoveAt(rng.Next(arr.Count));
                        return "elemento menos";
                    case 1:
                        int times = rng.Next(4) == 0 ? 500 : 1;
                        JsonNode? copy = arr[rng.Next(arr.Count)];
                        for (int k = 0; k < times; k++)
                        {
                            arr.Add(copy?.DeepClone());
                        }

                        return $"elemento duplicado ×{times}";
                    default:
                        JsonNode?[] items = [.. arr.Select(n => n?.DeepClone())];
                        arr.Clear();
                        foreach (JsonNode? item in items.OrderBy(_ => rng.Next()))
                        {
                            arr.Add(item);
                        }

                        return "desordenado";
                }

            default:
                return "nada";
        }
    }

    internal static byte[] MutateBytes(byte[] source, Random rng)
    {
        var bytes = new List<byte>(source);
        int edits = 1 + rng.Next(4);
        for (int i = 0; i < edits && bytes.Count > 0; i++)
        {
            int at = rng.Next(bytes.Count);
            switch (rng.Next(8))
            {
                case 0:
                    bytes.RemoveRange(at, bytes.Count - at);
                    break;
                case 1:
                    bytes.RemoveRange(at, Math.Min(bytes.Count - at, 1 + rng.Next(64)));
                    break;
                case 2:
                    bytes.Insert(at, (byte)rng.Next(256));
                    break;
                case 3:
                    bytes[at] = (byte)rng.Next(256);
                    break;
                case 4:
                    bytes[at] = (byte)"{}[],:\"\\"[rng.Next(8)];
                    break;
                case 5:
                    bytes[at] ^= (byte)(1 << rng.Next(8));
                    break;
                default:
                    for (int probe = 0; probe < 200; probe++)
                    {
                        int pos = (at + probe) % bytes.Count;
                        if (bytes[pos] is >= (byte)'0' and <= (byte)'9')
                        {
                            bytes[pos] = (byte)('0' + rng.Next(10));
                            break;
                        }
                    }

                    break;
            }
        }

        return [.. bytes];
    }

    // --- Árbol JSON ------------------------------------------------------------------------------------------------------

    private static JsonObject CompoundSequence(string id, string inner) => new()
    {
        ["id"] = id,
        ["name"] = id,
        ["frameRate"] = new JsonObject { ["num"] = 30, ["den"] = 1 },
        ["width"] = 1920,
        ["height"] = 1080,
        ["sampleRate"] = 48000,
        ["videoTracks"] = new JsonArray(new JsonObject
        {
            ["id"] = id + "-v",
            ["kind"] = "video",
            ["name"] = "V",
            ["clips"] = new JsonArray(CompoundClip(id + "-clip", inner, start: 0)),
            ["effects"] = new JsonArray(),
            ["transitions"] = new JsonArray(),
        }),
        ["audioTracks"] = new JsonArray(),
        ["markers"] = new JsonArray(),
        ["regions"] = new JsonArray(),
    };

    private static JsonObject CompoundClip(string id, string sequenceId, long start) => new()
    {
        ["id"] = id,
        ["sequenceId"] = sequenceId,
        ["start"] = start,
        ["sourceIn"] = 0L,
        ["sourceOut"] = 705_600_000L,
        ["effects"] = new JsonArray(),
    };

    private static IEnumerable<JsonObject> Objects(JsonNode? array) => array is JsonArray items ? items.OfType<JsonObject>() : [];

    private static IEnumerable<JsonNode> AllNodes(JsonNode? node, int depth = 0)
    {
        if (node is null || depth > 40)
        {
            yield break;
        }

        yield return node;
        IEnumerable<JsonNode?> children = node switch
        {
            JsonObject obj => obj.Select(p => p.Value),
            JsonArray arr => arr,
            _ => [],
        };
        foreach (JsonNode? child in children.ToList())
        {
            foreach (JsonNode inner in AllNodes(child, depth + 1))
            {
                yield return inner;
            }
        }
    }

    private static IEnumerable<JsonObject> AllObjects(JsonNode root) => AllNodes(root).OfType<JsonObject>();

    /// <summary>Todos los huecos que tienen un valor: propiedades de objetos y elementos de listas.</summary>
    private static List<(JsonNode Parent, string? Key, int Index)> Slots(JsonNode root)
    {
        var slots = new List<(JsonNode, string?, int)>();
        foreach (JsonNode node in AllNodes(root))
        {
            if (node is JsonObject obj)
            {
                slots.AddRange(obj.Select(p => ((JsonNode)obj, (string?)p.Key, -1)));
            }
            else if (node is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    slots.Add((arr, null, i));
                }
            }
        }

        return slots;
    }

    private static JsonNode? Get((JsonNode Parent, string? Key, int Index) slot) =>
        slot.Key is { } key ? slot.Parent[key] : slot.Parent[slot.Index];

    private static void Set(JsonNode parent, string? key, int index, JsonNode? value)
    {
        if (key is not null)
        {
            parent[key] = value;
        }
        else
        {
            parent[index] = value;
        }
    }
}
