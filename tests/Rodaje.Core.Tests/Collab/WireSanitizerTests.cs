using System.Text;
using System.Text.Json.Nodes;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Collab;

/// <summary>El saneador de lo que llega por la red: ningún texto hostil lanza ni se acepta (colaboracion.md §5).</summary>
public sealed class WireSanitizerTests
{
    /// <summary>Un parche que crea todos los elementos de un proyecto (versión 0).</summary>
    internal static Patch FullPatch(Project project)
    {
        SharedState state = SharedState.Capture(project);
        PatchOp[] ops = [.. state.Elements
            .OrderBy(p => p.Key.WireKind, StringComparer.Ordinal).ThenBy(p => p.Key.Id, StringComparer.Ordinal)
            .Select(p => new PatchOp(p.Key.Kind, p.Key.Id, p.Value.Parent, 0, p.Value.Content))];
        return new Patch("00000000000000000000000000000001", 0, "k:test", "Todo", ops, [new PatchOrder(PatchContainers.Media, [.. project.Media.Select(m => m.Id)])], []);
    }

    private static string ValidJson() => PatchWire.ToJson(FullPatch(SharedStateTests.Rich()));

    private static JsonNode Tree() => JsonNode.Parse(ValidJson())!;

    private static JsonObject Op(JsonNode tree, string kind) =>
        tree["ops"]!.AsArray().Select(o => o!.AsObject()).First(o => (string)o["kind"]! == kind && o["content"] is not null && (kind != "clip" || (string)o["id"]! == "clip-c"));

    [Fact]
    public void A_complete_patch_passes_and_round_trips_exactly()
    {
        string json = ValidJson();

        Assert.Null(WireSanitizer.Check(json));
        PatchParseResult parsed = PatchWire.TryParse(json);

        Assert.True(parsed.IsValid);
        Assert.Equal(json, PatchWire.ToJson(parsed.Patch!));
        Assert.Equal(FullPatch(SharedStateTests.Rich()).Ops.Count, parsed.Patch!.Ops.Count);
    }

    public static TheoryData<string> HostileMutations() => new()
    {
        "campo-desconocido-en-el-parche",
        "campo-desconocido-en-un-cambio",
        "campo-desconocido-en-el-clip",
        "campo-desconocido-anidado",
        "ruta-en-el-medio",
        "file-en-opciones",
        "file-en-mayusculas",
        "unc-en-opciones",
        "absoluta-en-opciones",
        "blob-mal-formado",
        "id-distinto-dentro",
        "clase-desconocida",
        "sin-padre",
        "padre-donde-no-toca",
        "id-con-control",
        "id-vacio",
        "id-enorme",
        "cambio-repetido",
        "version-negativa",
        "version-no-numerica",
        "contenido-no-objeto",
        "base-negativa",
        "orden-repetido",
        "orden-id-repetido",
        "orden-sin-version",
        "orden-version-negativa",
        "contenedor-desconocido",
        "blob-sin-huella",
        "texto-gigante",
    };

    [Theory]
    [MemberData(nameof(HostileMutations))]
    public void Hostile_patches_are_rejected_without_throwing(string mutation)
    {
        JsonNode tree = Tree();
        JsonObject clip = Op(tree, "clip");
        JsonObject media = Op(tree, "media");
        JsonObject first = tree["ops"]![0]!.AsObject();
        switch (mutation)
        {
            case "campo-desconocido-en-el-parche": tree["extra"] = 1; break;
            case "campo-desconocido-en-un-cambio": first["extra"] = 1; break;
            case "campo-desconocido-en-el-clip": clip["content"]!["rutaOculta"] = "x"; break;
            case "campo-desconocido-anidado": clip["content"]!["effects"]![0]!["extra"] = true; break;
            case "ruta-en-el-medio": media["content"]!["path"] = @"C:\video.mp4"; break;
            case "file-en-opciones": clip["content"]!["effects"]![0]!["options"] = new JsonObject { ["lut"] = @"file:C:/luts/a.cube" }; break;
            case "file-en-mayusculas": clip["content"]!["effects"]![0]!["options"] = new JsonObject { ["lut"] = "FILE:../a.cube" }; break;
            case "unc-en-opciones": clip["content"]!["effects"]![0]!["options"] = new JsonObject { ["lut"] = @"\\atacante\recurso\a.cube" }; break;
            case "absoluta-en-opciones": clip["content"]!["effects"]![0]!["options"] = new JsonObject { ["lut"] = "C:/Windows/a.cube" }; break;
            case "blob-mal-formado": clip["content"]!["effects"]![0]!["options"] = new JsonObject { ["lut"] = "blob:../../etc" }; break;
            case "id-distinto-dentro": clip["content"]!["id"] = "otro"; break;
            case "clase-desconocida": first["kind"] = "script"; break;
            case "sin-padre": clip.Remove("parent"); clip["parent"] = null; break;
            case "padre-donde-no-toca": media["parent"] = "s1"; break;
            case "id-con-control": clip["id"] = "a\nb"; clip["content"]!["id"] = "a\nb"; break;
            case "id-vacio": first["id"] = ""; break;
            case "id-enorme": first["id"] = new string('x', 5000); break;
            case "cambio-repetido": tree["ops"]!.AsArray().Add(first.DeepClone()); break;
            case "version-negativa": first["v"] = -1; break;
            case "version-no-numerica": first["v"] = "1"; break;
            case "contenido-no-objeto": first["content"] = "texto"; break;
            case "base-negativa": tree["base"] = -5; break;
            case "orden-repetido": tree["orders"]!.AsArray().Add(tree["orders"]![0]!.DeepClone()); break;
            case "orden-id-repetido": tree["orders"]![0]!["ids"]!.AsArray().Add("m-dup"); tree["orders"]![0]!["ids"]!.AsArray().Add("m-dup"); break;
            case "orden-sin-version": tree["orders"]![0]!.AsObject().Remove("v"); break;
            case "orden-version-negativa": tree["orders"]![0]!["v"] = -1; break;
            case "contenedor-desconocido": tree["orders"]![0]!["container"] = "../../x"; break;
            case "blob-sin-huella": tree["blobs"] = new JsonArray(new JsonObject { ["sha256"] = "123", ["size"] = 1 }); break;
            case "texto-gigante": clip["content"]!["color"] = new string('x', 200_000); break;
            default: throw new InvalidOperationException(mutation);
        }

        string json = tree.ToJsonString();
        PatchRejection? rejection = WireSanitizer.Check(json);
        PatchParseResult parsed = PatchWire.TryParse(json);

        Assert.NotNull(rejection);
        Assert.False(parsed.IsValid);
        Assert.NotNull(parsed.Rejection);
        Assert.DoesNotContain(@"C:\", rejection.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"texto\"")]
    [InlineData("{")]
    [InlineData("{\"id\":")]
    [InlineData("{\"id\":\"a\",\"id\":\"b\"}")]
    [InlineData("\uFEFF{}")]
    [InlineData("{\"id\":\"a\",\"base\":1e400,\"author\":\"x\",\"desc\":\"x\",\"ops\":[]}")]
    [InlineData("{\"id\":\"a\",\"base\":0,\"author\":\"x\",\"desc\":\"x\",\"ops\":{}}")]
    public void Broken_text_is_rejected_without_throwing(string json)
    {
        PatchRejection? rejection = WireSanitizer.Check(json);

        Assert.NotNull(rejection);
        Assert.False(PatchWire.TryParse(json).IsValid);
    }

    [Fact]
    public void Null_text_is_rejected_without_throwing() => Assert.NotNull(WireSanitizer.Check(null!));

    [Fact]
    public void Duplicate_keys_in_the_content_are_rejected()
    {
        string json = ValidJson();
        string tampered = json.Replace("\"enabled\":true", "\"enabled\":true,\"enabled\":false", StringComparison.Ordinal);

        Assert.NotEqual(json, tampered);
        Assert.NotNull(WireSanitizer.Check(tampered));
    }

    [Fact]
    public void Deep_json_is_rejected_by_depth()
    {
        string deep = "{\"id\":\"a\",\"base\":0,\"author\":\"x\",\"desc\":\"x\",\"ops\":[],\"x\":" + new string('[', 500) + new string(']', 500) + "}";

        PatchRejection? rejection = WireSanitizer.Check(deep);

        Assert.NotNull(rejection);
    }

    [Fact]
    public void A_patch_over_the_size_limit_is_too_large()
    {
        PatchRejection? rejection = WireSanitizer.Check(ValidJson(), new WireLimits { MaxPatchBytes = 1000 });

        Assert.Equal(PatchRejectionReason.TooLarge, rejection?.Reason);
    }

    [Fact]
    public void Too_many_changes_are_too_large()
    {
        PatchRejection? rejection = WireSanitizer.Check(ValidJson(), new WireLimits { MaxOps = 2 });

        Assert.Equal(PatchRejectionReason.TooLarge, rejection?.Reason);
    }

    [Fact]
    public void Deleting_needs_no_content_and_a_blob_reference_is_allowed()
    {
        var delete = new Patch("p1", 3, "k:1", "Borrar", [new PatchOp(ElementKind.Clip, "c1", "t1", 4, null)], [], [new PatchBlob(new string('A', 64), 12)]);

        Assert.Null(WireSanitizer.Check(PatchWire.ToJson(delete)));
    }

    [Fact]
    public void Unicode_and_non_latin_text_survive_the_trip()
    {
        Project project = SharedStateTests.Rich();
        project.Sequences[0].Markers.Add(new Marker { Id = "mk-uni", Name = "Ñandú 日本語 😀 \"comillas\" \\ barra", Note = "línea\nnueva" });
        Patch patch = FullPatch(project);

        PatchParseResult parsed = PatchWire.TryParse(PatchWire.ToJson(patch));

        Assert.True(parsed.IsValid);
        Assert.Equal(patch.Ops.Single(o => o.Id == "mk-uni").Content, parsed.Patch!.Ops.Single(o => o.Id == "mk-uni").Content);
        Assert.Equal(Encoding.UTF8.GetByteCount(PatchWire.ToJson(patch)), Encoding.UTF8.GetByteCount(PatchWire.ToJson(parsed.Patch)));
    }
}
