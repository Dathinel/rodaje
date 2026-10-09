using System.Text.Json;
using System.Text.Json.Nodes;
using FotoToma.Core.Model;
using FotoToma.Core.Serialization;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Tests.Serialization;

public class ProjectSerializerTests
{
    private static JsonObject RichNode() => JsonNode.Parse(ProjectSerializer.Serialize(SerializationSamples.Rich()))!.AsObject();

    private static JsonObject FirstClip(JsonObject root) =>
        root["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!.AsObject();

    [Fact]
    public void RichProject_IsValid()
    {
        Assert.Empty(ModelValidator.Validate(SerializationSamples.Rich()));
    }

    [Fact]
    public void RoundTrip_TextIsIdentical()
    {
        string first = ProjectSerializer.Serialize(SerializationSamples.Rich());
        string second = ProjectSerializer.Serialize(ProjectSerializer.Deserialize(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public void RoundTrip_ObjectsAreDeepEqual()
    {
        Project original = SerializationSamples.Rich();
        Project copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(original));
        SerializationSamples.AssertDeepEqual(original, copy);

        // Comprobaciones explícitas de los casos delicados.
        Assert.Null(copy.FindMedia("media-image")!.FrameRate);
        Assert.Equal(new Rational(30000, 1001), copy.Sequences[0].FrameRate);
        Assert.Equal(Rational.Ntsc24, copy.Sequences[1].FrameRate);
        Assert.Equal(0.1 + 0.2, copy.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["positionX"].Keyframes[^1].Value);
        Assert.Equal(
            [Interpolation.Hold, Interpolation.Linear, Interpolation.EaseIn, Interpolation.EaseOut, Interpolation.EaseInOut, Interpolation.Linear],
            copy.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["positionX"].Keyframes.Select(k => k.Interpolation));
        Assert.Equal(2, copy.Sequences[0].GetLinkGroup("clip-1").Count);
    }

    [Fact]
    public void Shape_UsesCamelCaseTicksRationalsAndEnumStrings()
    {
        string json = ProjectSerializer.Serialize(SerializationSamples.Rich());
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(["schemaVersion", "id", "name", "media", "sequences"], root.Select(p => p.Key));
        Assert.Equal(1, (int)root["schemaVersion"]!);

        JsonObject sequence = root["sequences"]![0]!.AsObject();
        Assert.Equal(["id", "name", "frameRate", "width", "height", "sampleRate", "videoTracks", "audioTracks"], sequence.Select(p => p.Key));
        Assert.Equal(30000, (long)sequence["frameRate"]!["num"]!);
        Assert.Equal(1001, (long)sequence["frameRate"]!["den"]!);
        Assert.Equal(["num", "den"], sequence["frameRate"]!.AsObject().Select(p => p.Key));

        JsonObject track = sequence["videoTracks"]![0]!.AsObject();
        Assert.Equal(["id", "kind", "name", "muted", "locked", "hidden", "clips"], track.Select(p => p.Key));
        Assert.Equal("video", (string)track["kind"]!);
        Assert.Equal("audio", (string)sequence["audioTracks"]![0]!["kind"]!);

        JsonObject clip = FirstClip(root);
        Assert.Equal(["id", "mediaId", "sourceIn", "sourceOut", "start", "linkId", "enabled", "effects"], clip.Select(p => p.Key));
        Assert.Equal(JsonValueKind.Number, clip["sourceIn"]!.GetValueKind());
        Assert.Equal(Time.FromFrames(30, Rational.Ntsc30).Ticks, (long)clip["sourceIn"]!);
        Assert.Equal("link-1", (string)clip["linkId"]!);

        JsonObject keyframe = clip["effects"]![0]!["parameters"]!["positionX"]!["keyframes"]![4]!.AsObject();
        Assert.Equal(["time", "value", "interpolation"], keyframe.Select(p => p.Key));
        Assert.Equal("easeInOut", (string)keyframe["interpolation"]!);
        Assert.Equal(4 * Time.TicksPerSecond, (long)keyframe["time"]!);

        JsonObject image = root["media"]![1]!.AsObject();
        Assert.True(image.ContainsKey("frameRate"));
        Assert.Null(image["frameRate"]);
        Assert.True(image.ContainsKey("duration"));

        // Ni secuencias, ni pistas, ni clips escriben propiedades calculadas.
        Assert.DoesNotContain("allTracks", json, StringComparison.Ordinal);
        Assert.False(sequence.ContainsKey("duration"));
        Assert.False(track.ContainsKey("end"));
        Assert.False(clip.ContainsKey("end"));
        Assert.False(clip.ContainsKey("duration"));

        // Texto legible: tildes sin escapar, saltos de línea LF, indentado.
        Assert.Contains("música/canción ñ.wav", json, StringComparison.Ordinal);
        Assert.Contains("Vacaciones en Cartagena — edición ñ", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.StartsWith("{\n  \"schemaVersion\": 1,", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_MissingMembers_TakeModelDefaults()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "id": "p",
              "media": [ { "id": "m", "duration": 1000, "hasVideo": true } ],
              "sequences": [
                {
                  "id": "s",
                  "videoTracks": [
                    {
                      "id": "t",
                      "kind": "video",
                      "clips": [
                        {
                          "id": "c", "mediaId": "m", "sourceOut": 500,
                          "effects": [ { "id": "e", "parameters": { "x": { "keyframes": [ { "time": 1, "value": 2 } ] } } } ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        Project project = ProjectSerializer.Deserialize(json);

        Assert.Equal("", project.Name);
        Assert.Null(project.Media[0].FrameRate);
        Sequence sequence = project.Sequences[0];
        Assert.Equal(Rational.Fps30, sequence.FrameRate);
        Assert.Equal((1920, 1080, 48000), (sequence.Width, sequence.Height, sequence.SampleRate));
        Assert.Empty(sequence.AudioTracks);
        Clip clip = sequence.VideoTracks[0].Clips[0];
        Assert.Equal((Time.Zero, Time.Zero, true, (string?)null), (clip.SourceIn, clip.Start, clip.Enabled, clip.LinkId));
        EffectInstance effect = clip.Effects[0];
        Assert.True(effect.Enabled);
        Assert.Equal(new Keyframe(Time.FromTicks(1), 2, Interpolation.Linear), effect.Parameters["x"].Keyframes[0]);
    }

    [Fact]
    public void Deserialize_IgnoresUnknownMembers()
    {
        JsonObject root = RichNode();
        root["programaExterno"] = new JsonObject { ["dato"] = 1 };
        FirstClip(root)["velocidad"] = 2;

        Project project = ProjectSerializer.Deserialize(root.ToJsonString());

        Assert.DoesNotContain("programaExterno", ProjectSerializer.Serialize(project), StringComparison.Ordinal);
        SerializationSamples.AssertDeepEqual(SerializationSamples.Rich(), project);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\": 1,")]
    [InlineData("{\"schemaVersion\": 1 \"id\": \"p\"}")]
    [InlineData("no es json")]
    public void Deserialize_MalformedJson_ThrowsFormatException(string json)
    {
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json));
        Assert.Contains("no es JSON bien formado", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
    }

    [Fact]
    public void Deserialize_DuplicateProperty_ThrowsFormatException()
    {
        var ex = Assert.Throws<ProjectFormatException>(() =>
            ProjectSerializer.Deserialize("""{"schemaVersion": 1, "id": "a", "id": "b"}"""));
        Assert.Contains("campos repetidos", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"proyecto\"")]
    public void Deserialize_RootNotObject_ThrowsFormatException(string json)
    {
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json));
        Assert.Contains("no es un objeto", ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, Action<JsonObject>> WrongValues => new()
    {
        { "sourceIn", r => FirstClip(r)["sourceIn"] = "abc" },
        { "sourceIn", r => FirstClip(r)["sourceIn"] = 1.5 },
        { "frameRate", r => r["sequences"]![0]!["frameRate"] = new JsonObject { ["num"] = 0, ["den"] = 1 } },
        { "kind", r => r["sequences"]![0]!["videoTracks"]![0]!["kind"] = 0 },
        { "kind", r => r["sequences"]![0]!["videoTracks"]![0]!["kind"] = "subtítulos" },
        { "interpolation", r => FirstClip(r)["effects"]![0]!["parameters"]!["positionX"]!["keyframes"]![0]!["interpolation"] = "cubic" },
        { "name", r => r["name"] = null },
        { "clips", r => r["sequences"]![0]!["videoTracks"]![0]!["clips"] = null },
        { "hasVideo", r => r["media"]![0]!["hasVideo"] = "sí" },
    };

    [Theory]
    [MemberData(nameof(WrongValues))]
    public void Deserialize_WrongTypeOrValue_ThrowsFormatExceptionWithPath(string expectedInPath, Action<JsonObject> corrupt)
    {
        ArgumentNullException.ThrowIfNull(corrupt);
        JsonObject root = RichNode();
        corrupt(root);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));

        Assert.Contains("tipo o un valor que no corresponde", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expectedInPath, ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
    }

    public static TheoryData<string, Action<JsonObject>> NullItems => new()
    {
        { "medios", r => r["media"]!.AsArray().Add(null) },
        { "secuencias", r => r["sequences"]!.AsArray().Add(null) },
        { "pistas de video", r => r["sequences"]![0]!["videoTracks"]!.AsArray().Add(null) },
        { "pistas de audio", r => r["sequences"]![0]!["audioTracks"]!.AsArray().Add(null) },
        { "clips", r => r["sequences"]![0]!["videoTracks"]![1]!["clips"]!.AsArray().Add(null) },
        { "efectos", r => FirstClip(r)["effects"]!.AsArray().Add(null) },
        { "parámetros", r => FirstClip(r)["effects"]![0]!["parameters"]!["nulo"] = null },
        { "keyframes", r => FirstClip(r)["effects"]![0]!["parameters"]!["positionX"]!["keyframes"]!.AsArray().Add(null) },
    };

    [Theory]
    [MemberData(nameof(NullItems))]
    public void Deserialize_NullListItem_ThrowsFormatException(string list, Action<JsonObject> corrupt)
    {
        ArgumentNullException.ThrowIfNull(corrupt);
        JsonObject root = RichNode();
        corrupt(root);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));

        Assert.Equal([$"Hay un elemento vacío (null) en la lista de {list}."], ex.Problems);
    }

    [Fact]
    public void Deserialize_InvalidModel_ListsProblems()
    {
        JsonObject root = RichNode();
        JsonObject clip2 = root["sequences"]![0]!["videoTracks"]![0]!["clips"]![1]!.AsObject();
        clip2["start"] = 0; // se solapa con clip-1
        clip2["mediaId"] = "no-existe";

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));

        Assert.StartsWith("El proyecto tiene datos inconsistentes", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, ex.Problems.Count);
        Assert.Contains(ex.Problems, p => p.Contains("se solapa", StringComparison.Ordinal));
        Assert.Contains(ex.Problems, p => p.Contains("«no-existe»", StringComparison.Ordinal));
        Assert.All(ex.Problems, p => Assert.Contains(p, ex.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Deserialize_RunsMigrationChecksFirst()
    {
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize("""{"id": "p"}"""));
        Assert.Contains("no es un proyecto de FotoToma válido", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Arguments_AreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectSerializer.Serialize(null!));
        Assert.Throws<ArgumentNullException>(() => ProjectSerializer.Deserialize(null!));
    }
}
