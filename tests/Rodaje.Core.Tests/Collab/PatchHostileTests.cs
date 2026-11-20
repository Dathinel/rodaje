using System.Text;
using System.Text.Json.Nodes;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Collab;

/// <summary>
/// Fase 2 del diseño (B12), capa de parches: lo que llega de otro equipo (un invitado hacia el anfitrión o un anfitrión hacia un
/// invitado) se muta al azar y nunca lanza, nunca cambia el proyecto cuando se rechaza y nunca deja pasar una ruta ni un proyecto
/// inválido cuando se acepta. La batería de la sala (marcos, ritmo, roles, reservas) vive en <c>Dathinel/sala</c>.
/// </summary>
public sealed class PatchHostileTests
{
    private static readonly object?[] Weird =
    [
        null, -1, 0, int.MaxValue, long.MaxValue, 1e308, -1e308, 0.1, true, false, "", " ", "x", "file:C:/x.cube", "FILE:../x", "\\\\atacante\\x",
        "C:\\Windows\\x", "../../x", "blob:zzz", "\u0000", "\ud800", "a\nb", new string('x', 70_000), "ñandú 😀",
    ];

    private static List<(Project Base, string Json)> Seeds()
    {
        Project rich = SharedStateTests.Rich();
        var seeds = new List<(Project, string)> { (new Project { Id = rich.Id }, PatchWire.ToJson(WireSanitizerTests.FullPatch(rich))) };

        Project edited = rich.Clone();
        SharedState before = SharedState.Capture(rich);
        Clip clip = edited.Sequences[0].GetClip("clip-c").Clip;
        clip.Enabled = !clip.Enabled;
        clip.Color = "#112233";
        edited.Sequences[0].Markers.Add(new Marker { Id = "mk-h", Name = "x" });
        Patch? step = PatchDiff.Create(before, SharedState.Capture(edited), new ElementVersions(), new PatchHeader("h1", 0, "k:a", "paso"));
        seeds.Add((rich, PatchWire.ToJson(step!)));
        return seeds;
    }

    private static JsonNode Mutate(JsonNode root, Random random)
    {
        var nodes = new List<JsonNode>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    nodes.Add(o);
                    foreach (KeyValuePair<string, JsonNode?> p in o.ToList())
                    {
                        Walk(p.Value);
                    }

                    break;
                case JsonArray a:
                    nodes.Add(a);
                    foreach (JsonNode? item in a.ToList())
                    {
                        Walk(item);
                    }

                    break;
            }
        }

        Walk(root);
        JsonNode target = nodes[random.Next(nodes.Count)];
        switch (target)
        {
            case JsonObject o when o.Count > 0:
                string key = o.ElementAt(random.Next(o.Count)).Key;
                switch (random.Next(4))
                {
                    case 0: o.Remove(key); break;
                    case 1: o[key] = Value(random); break;
                    case 2: o[key + "x"] = Value(random); break;
                    default: o[key] = o[key]?.DeepClone(); break;
                }

                break;
            case JsonArray a when a.Count > 0:
                int at = random.Next(a.Count);
                switch (random.Next(3))
                {
                    case 0: a.RemoveAt(at); break;
                    case 1: a[at] = Value(random); break;
                    default: a.Add(a[at]?.DeepClone()); break;
                }

                break;
            case JsonArray a:
                a.Add(Value(random));
                break;
        }

        return root;
    }

    private static JsonValue? Value(Random random)
    {
        object? v = Weird[random.Next(Weird.Length)];
        return v switch
        {
            null => null,
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            bool b => JsonValue.Create(b),
            string s => JsonValue.Create(s),
            _ => null,
        };
    }

    [Fact]
    public void Mutated_patches_never_throw_never_change_a_rejected_project_and_never_let_a_path_in()
    {
        List<(Project Base, string Json)> seeds = Seeds();
        int accepted = 0;
        int rejected = 0;
        for (int seed = 1; seed <= 4_000; seed++)
        {
            var random = new Random(seed);
            (Project baseProject, string json) = seeds[seed % seeds.Count];
            string text;
            if (random.Next(4) == 0)
            {
                // Texto: se corta, se pega un trozo o se cambia un carácter.
                var builder = new StringBuilder(json);
                switch (random.Next(3))
                {
                    case 0: builder.Length = random.Next(builder.Length); break;
                    case 1: builder[random.Next(builder.Length)] = (char)random.Next(1, 0x250); break;
                    default: builder.Insert(random.Next(builder.Length), json.AsSpan(random.Next(json.Length / 2), 20)); break;
                }

                text = builder.ToString();
            }
            else
            {
                JsonNode root = JsonNode.Parse(json)!;
                for (int i = random.Next(1, 4); i > 0; i--)
                {
                    Mutate(root, random);
                }

                text = root.ToJsonString();
            }

            Project target = baseProject.Clone();
            string hash = SharedState.Capture(target).Hash;
            var versions = new ElementVersions();
            PatchResult? result = null;
            try
            {
                PatchParseResult parsed = PatchWire.TryParse(text);
                if (parsed.IsValid)
                {
                    result = PatchApply.TryApply(target, versions, parsed.Patch!);
                }
            }
            catch (Exception ex)
            {
                Assert.Fail($"[semilla {seed}] lanzó {ex.GetType().Name}: {ex.Message}");
            }

            if (result is { IsAccepted: true })
            {
                accepted++;
                Assert.Empty(ModelValidator.Validate(target));
                foreach (ElementState element in SharedState.Capture(target).Elements.Values)
                {
                    Assert.Empty(PathLikeOptions(JsonNode.Parse(element.Content)));
                }
            }
            else
            {
                rejected++;
                Assert.True(hash == SharedState.Capture(target).Hash, $"[semilla {seed}] un parche rechazado cambió el proyecto.");
                Assert.Equal(0, versions.Count);
            }
        }

        Assert.True(rejected > 1_000, $"Solo se rechazaron {rejected}: la mutación es demasiado suave.");
        Assert.True(accepted > 20, $"Solo se aceptaron {accepted}: las semillas ya no se aplican.");
    }

    // Un identificador o un nombre puede parecer una ruta sin ser peligroso (nadie lo abre); una opción, sí.
    private static List<string> PathLikeOptions(JsonNode? node)
    {
        var found = new List<string>();
        void Walk(JsonNode? n, bool inOptions)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (KeyValuePair<string, JsonNode?> p in o)
                    {
                        Walk(p.Value, inOptions || p.Key == "options");
                    }

                    break;
                case JsonArray a:
                    foreach (JsonNode? item in a)
                    {
                        Walk(item, false);
                    }

                    break;
                case JsonValue v when inOptions && v.TryGetValue(out string? text) && text is not null
                    && (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || text.StartsWith('\\') || text.StartsWith('/') || text.Contains("..", StringComparison.Ordinal)):
                    found.Add(text);
                    break;
            }
        }

        Walk(node, false);
        return found;
    }

    [Fact]
    public void Mutated_initial_copies_never_throw()
    {
        string json = ProjectWire.ToJson(SharedStateTests.Rich());
        for (int seed = 1; seed <= 1_500; seed++)
        {
            var random = new Random(seed);
            JsonNode root = JsonNode.Parse(json)!;
            Mutate(root, random);
            ProjectParseResult parsed = ProjectWire.TryParse(root.ToJsonString());
            if (parsed.IsValid)
            {
                Assert.Empty(ModelValidator.Validate(parsed.Project!));
                Assert.All(parsed.Project!.Media, m => Assert.Equal("", m.Path));
            }
            else
            {
                Assert.NotNull(parsed.Rejection);
            }
        }
    }

    [Fact]
    public void A_giant_initial_copy_is_rejected_by_size_without_parsing_it()
    {
        string giant = "{\"project\":\"p\",\"versions\":{},\"patch\":\"" + new string('a', ProjectWire.MaxBytes + 10) + "\"}";

        ProjectParseResult parsed = ProjectWire.TryParse(giant);

        Assert.Equal(PatchRejectionReason.TooLarge, parsed.Rejection?.Reason);
    }

    [Fact]
    public void A_patch_with_foreign_or_repeated_ids_is_rejected_whole()
    {
        Project project = SharedStateTests.Rich();
        string hash = SharedState.Capture(project).Hash;
        SharedState state = SharedState.Capture(project);
        ElementState clip = state.Elements[new ElementKey(ElementKind.Clip, "clip-c")];

        // Un marcador que usa el identificador de un clip: los identificadores son únicos en todo el proyecto.
        var clash = new Patch("p", 0, "k", "d",
            [new PatchOp(ElementKind.Marker, "mk-x", project.Sequences[0].Id, 0, "{\"id\":\"clip-c\",\"name\":\"x\",\"time\":0}")], [], []);
        // El mismo elemento dos veces.
        var twice = new Patch("p", 0, "k", "d",
            [new PatchOp(ElementKind.Clip, "clip-c", clip.Parent, 0, clip.Content), new PatchOp(ElementKind.Clip, "clip-c", clip.Parent, 0, clip.Content)], [], []);
        // Un clip nuevo con los mismos efectos (mismos ids) que otro.
        var copy = new Patch("p", 0, "k", "d",
            [new PatchOp(ElementKind.Clip, "clip-c-2", clip.Parent, 0, clip.Content.Replace("\"id\":\"clip-c\"", "\"id\":\"clip-c-2\"", StringComparison.Ordinal))], [], []);

        foreach (Patch patch in new[] { clash, twice, copy })
        {
            PatchResult result = PatchApply.TryApply(project, new ElementVersions(), patch);
            Assert.False(result.IsAccepted);
            Assert.Equal(hash, SharedState.Capture(project).Hash);
        }
    }

    [Fact]
    public void A_media_with_a_made_up_content_identity_in_the_wrong_format_is_rejected()
    {
        Project project = SharedStateTests.Rich();
        string hash = SharedState.Capture(project).Hash;
        ElementState media = SharedState.Capture(project).Elements[new ElementKey(ElementKind.Media, project.Media[0].Id)];
        JsonObject content = JsonNode.Parse(media.Content)!.AsObject();
        content["contentHash"] = "no-es-una-huella";
        content["contentKey"] = "tampoco";
        var patch = new Patch("p", 0, "k", "d", [new PatchOp(ElementKind.Media, project.Media[0].Id, null, 0, content.ToJsonString())], [], []);

        PatchResult result = PatchApply.TryApply(project, new ElementVersions(), patch);

        Assert.Equal(PatchRejectionReason.Invalid, result.Rejection?.Reason);
        Assert.Equal(hash, SharedState.Capture(project).Hash);
    }

    [Fact]
    public void Version_overflow_and_replays_are_refused()
    {
        Project project = SharedStateTests.Rich();
        var versions = new ElementVersions();
        versions.Set(new ElementKey(ElementKind.Marker, "mk-v"), int.MaxValue);
        var patch = new Patch("p", 0, "k", "d",
            [new PatchOp(ElementKind.Marker, "mk-v", project.Sequences[0].Id, int.MaxValue, "{\"id\":\"mk-v\",\"name\":\"v\",\"time\":0}")], [], []);

        PatchResult result = PatchApply.TryApply(project, versions, patch);

        Assert.False(result.IsAccepted);
        Assert.DoesNotContain(project.Sequences[0].Markers, m => m.Id == "mk-v");
    }
}
