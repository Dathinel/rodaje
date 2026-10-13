using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Formato 5: clips generadores (títulos), rol de pista, migración desde 4 y rechazo del 9.</summary>
public class FormatV5Tests
{
    private static string Ejemplo(string name) =>
        File.ReadAllText(Path.Combine(ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Serialization", "ejemplos", name))
            .ReplaceLineEndings("\n");

    private static string Example => Ejemplo("proyecto-v5.rodaje");

    private static Clip ClipOf(Project p, string id) => p.Sequences[0].GetClip(id).Clip;

    [Fact]
    public void Example_LoadsAndIsCanonical()
    {
        Project project = ProjectSerializer.Deserialize(Example);

        // Al guardar se escribe la versión actual; el resto del archivo no cambia.
        string saved = Example.Replace("\"schemaVersion\": 5", "\"schemaVersion\": 10", StringComparison.Ordinal);
        Assert.Equal(saved, ProjectSerializer.Serialize(project) + "\n");
        Assert.Equal(saved, ProjectSerializer.Serialize(project.Clone()) + "\n");

        Track subtitles = project.Sequences[0].VideoTracks[1];
        Assert.Equal(TrackRoles.Subtitles, subtitles.Role);
        Assert.Null(project.Sequences[0].VideoTracks[0].Role);

        Clip title = ClipOf(project, "clip-2");
        Assert.Equal("", title.MediaId);
        Assert.Equal(Time.FromTicks(-352_800_000), title.SourceIn);
        Assert.Equal(Time.FromSeconds(2), title.Duration);
        Assert.Equal("Hola\nmundo, ñandú", title.Generator!.Text);
        Assert.Equal(StrokeJoin.Round, title.Generator.Style!.Stroke!.Join);
        Assert.Equal(Interpolation.EaseOut, title.Generator.Style.In!.Curves["opacity"][1].Interp);
        Assert.Equal([24.0, 12.0], title.Generator.Style.Background!.Padding);
        Assert.False(title.Generator.Edges.Out);

        TextStyle plain = ClipOf(project, "clip-3").Generator!.Style!;
        Assert.Equal(TextAlign.Left, plain.Align);
        Assert.Null(plain.Stroke);
        Assert.Null(plain.In);
        Assert.Null(plain.Source);
        Assert.Equal("solid", ClipOf(project, "clip-4").Generator!.Kind);
    }

    [Fact]
    public void Generator_RoundTripsWithoutMediaId_AndMediaClipsKeepIt()
    {
        Project p = ValidProject();
        p.Sequences[0].VideoTracks[1].Clips.Add(MakeTitle("t1", 10, -1, 2));
        p.Sequences[0].VideoTracks[1].Role = TrackRoles.Subtitles;
        Assert.Empty(ModelValidator.Validate(p));

        string json = ProjectSerializer.Serialize(p);
        JsonObject title = JsonNode.Parse(json)!["sequences"]![0]!["videoTracks"]![1]!["clips"]![1]!.AsObject();
        Assert.False(title.ContainsKey("mediaId"));
        Assert.Equal(["id", "sourceIn", "sourceOut", "start", "linkId", "enabled", "generator", "effects"], title.Select(n => n.Key));
        Assert.Equal(["kind", "text", "style", "edges"], title["generator"]!.AsObject().Select(n => n.Key));

        Project back = ProjectSerializer.Deserialize(json);
        Assert.Equal(json, ProjectSerializer.Serialize(back));
        Assert.True(ClipOf(back, "t1").Generator!.ContentEquals(ClipOf(p, "t1").Generator!));
        Assert.Equal("m1", ClipOf(back, "c1").MediaId);
        Assert.DoesNotContain("\"role\": null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"generator\": null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LoneSurrogate_InAnyText_IsAFormatError_NotAnInvalidOperation()
    {
        string broken = Example.Replace("Hola\\nmundo", "Hola\\" + "ud800mundo", StringComparison.Ordinal);
        Assert.NotEqual(Example, broken);
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(broken));
    }

    [Fact]
    public void Migrate_V4_OnlyBumpsTheVersion()
    {
        JsonObject root = JsonNode.Parse(Ejemplo("proyecto-v4.rodaje"))!.AsObject();
        JsonObject expected = root.DeepClone().AsObject();
        expected["schemaVersion"] = 5;

        ProjectMigrator.AllowGenerators(root);

        Assert.True(JsonNode.DeepEquals(expected, root));
    }

    [Fact]
    public void V4Example_OpensWithoutLoss()
    {
        string v4 = Ejemplo("proyecto-v4.rodaje");
        Project project = ProjectSerializer.Deserialize(v4);

        Assert.All(project.Sequences[0].AllTracks.SelectMany(t => t.Clips), c => Assert.Null(c.Generator));
        Assert.All(project.Sequences[0].AllTracks, t => Assert.Null(t.Role));
        Assert.Equal(v4.Replace("\"schemaVersion\": 4", "\"schemaVersion\": 10", StringComparison.Ordinal), ProjectSerializer.Serialize(project) + "\n");
    }

    [Fact]
    public void NewerFormat_AsksToUpdate()
    {
        string newer = Example.Replace("\"schemaVersion\": 5", "\"schemaVersion\": 11", StringComparison.Ordinal);
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(newer));
        Assert.Contains("formato 11", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Actualiza Rodaje", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullInsideStyleLists_IsRejectedBeforeValidation()
    {
        JsonObject root = JsonNode.Parse(Example)!.AsObject();
        JsonNode style = root["sequences"]![0]!["videoTracks"]![1]!["clips"]![0]!["generator"]!["style"]!;
        style["font"]!["fallback"]!.AsArray().Add(null);
        style["in"]!["curves"]!["opacity"]!.AsArray().Add(null);
        style["out"]!["curves"]!["x"] = null;

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));
        Assert.Contains("fuentes de respaldo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("puntos de la animación", ex.Message, StringComparison.Ordinal);
        Assert.Contains("curvas de la animación", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEnumValueInStyle_IsAFormatError()
    {
        string broken = Example.Replace("\"align\": \"left\"", "\"align\": \"justify\"", StringComparison.Ordinal);
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(broken));
        Assert.Contains("align", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        Clip original = MakeTitle("t", 0, 0, 1);
        original.Generator!.Box = new TextFrame { Width = 800 };
        Clip copy = original.Clone();

        copy.Generator!.Text = "otro";
        copy.Generator.Edges.In = false;
        copy.Generator.Box!.Width = 10;
        copy.Generator.Style!.Font.Fallback.Add("Otra");
        copy.Generator.Style.Background!.Padding[0] = 1;
        copy.Generator.Style.In!.Curves["opacity"].Add(new StyleKey(1, 1));

        Assert.Equal("Título", original.Generator.Text);
        Assert.True(original.Generator.Edges.In);
        Assert.Equal(800, original.Generator.Box.Width);
        Assert.Equal(2, original.Generator.Style!.Font.Fallback.Count);
        Assert.Equal(24, original.Generator.Style.Background!.Padding[0]);
        Assert.Equal(2, original.Generator.Style.In!.Curves["opacity"].Count);
        Assert.True(original.Clone().Generator!.ContentEquals(original.Generator));
    }

    public static TheoryData<Action<ClipGenerator>> GeneratorChanges => new()
    {
        g => g.Kind = "solid",
        g => g.Text = "x",
        g => g.Edges.In = false,
        g => g.Edges.Out = false,
        g => g.Box = new TextFrame(),
        g => g.Style = null,
        g => g.Style!.Source = null,
        g => g.Style!.Name = "Otro",
        g => g.Style!.Font.Family = "Arial",
        g => g.Style!.Font.Weight = 400,
        g => g.Style!.Font.Italic = true,
        g => g.Style!.Font.Source = null,
        g => g.Style!.Font.Fallback.RemoveAt(1),
        g => g.Style!.Size = 95,
        g => g.Style!.Reference.Width = 1080,
        g => g.Style!.Reference.Height = 1920,
        g => g.Style!.LineHeight = 1,
        g => g.Style!.LetterSpacing = 1,
        g => g.Style!.Align = TextAlign.Right,
        g => g.Style!.Fill.Color = "#000000",
        g => g.Style!.Stroke = null,
        g => g.Style!.Stroke!.Join = StrokeJoin.Bevel,
        g => g.Style!.Stroke!.Width = 1,
        g => g.Style!.Stroke!.Color = "#FFFFFF",
        g => g.Style!.Shadow!.Blur = 1,
        g => g.Style!.Shadow!.Dx = 1,
        g => g.Style!.Shadow!.Dy = 1,
        g => g.Style!.Shadow!.Color = "#FFFFFF",
        g => g.Style!.Shadow = null,
        g => g.Style!.Background!.Padding[1] = 0,
        g => g.Style!.Background!.Radius = 0,
        g => g.Style!.Background!.Color = "#FFFFFF",
        g => g.Style!.Background = null,
        g => g.Style!.In!.DurationSeconds = 1,
        g => g.Style!.In!.Curves.Remove("y"),
        g => g.Style!.In!.Curves["y"] = [new StyleKey(0, 40)],
        g => g.Style!.In!.Curves["x"] = g.Style!.In!.Curves["y"],
        g => g.Style!.Out!.Curves["opacity"][1] = new StyleKey(0.3, 0, Interpolation.Hold),
        g => g.Style!.Out = null,
    };

    [Theory]
    [MemberData(nameof(GeneratorChanges))]
    public void ContentEquals_SeesEveryField(Action<ClipGenerator> change)
    {
        ClipGenerator a = MakeTitle("t", 0, 0, 1).Generator!;
        a.Box = new TextFrame { Width = 800, WidthFraction = 0.5 };
        ClipGenerator b = a.Clone();
        Assert.True(a.ContentEquals(b));

        change(b);

        Assert.False(a.ContentEquals(b));
        Assert.False(b.ContentEquals(a));
    }

    [Fact]
    public void SameContent_IgnoresOnlyEdges()
    {
        ClipGenerator a = MakeTitle("t", 0, 0, 1).Generator!;
        ClipGenerator b = a.Clone();
        b.Edges.Out = false;
        Assert.True(a.SameContent(b));
        Assert.False(a.ContentEquals(b));
    }
}
