using System.Text.Json.Nodes;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Formato 3: opciones de los efectos, efectos de pista, transiciones y la migración desde 1 y 2.</summary>
public class FormatV3Tests
{
    private static readonly long S = Time.TicksPerSecond;

    private static string ExamplePath =>
        Path.Combine(ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Serialization", "ejemplos", "proyecto-v3.rodaje");

    private static string Example => File.ReadAllText(ExamplePath).ReplaceLineEndings("\n");

    // Al abrirlo se migra y se guarda en la versión actual; lo demás queda igual.
    private static string ExampleSaved => Example.Replace("\"schemaVersion\": 3", "\"schemaVersion\": 8", StringComparison.Ordinal);

    [Fact]
    public void Example_LoadsAndIsCanonical()
    {
        Project project = ProjectSerializer.Deserialize(Example);

        Assert.Equal(ExampleSaved, ProjectSerializer.Serialize(project) + "\n");
        Assert.Equal(ExampleSaved, ProjectSerializer.Serialize(project.Clone()) + "\n");
    }

    [Fact]
    public void Example_ReadsOptionsTrackEffectsAndTransitions()
    {
        Sequence sequence = ProjectSerializer.Deserialize(Example).Sequences[0];
        Track video = sequence.VideoTracks[0];

        EffectInstance color = video.Clips[0].Effects[1];
        Assert.Equal(BuiltInEffects.Color, color.Type);
        Assert.Equal("rodaje-filtros@1.0.0/calido", color.Options[BuiltInEffects.ColorOptions.Lut]);
        Assert.Equal("rodaje-filtros@1.0.0/look-calido", color.Options[BuiltInEffects.CommonOptions.Source]);
        Assert.Equal("overlay", sequence.VideoTracks[1].Clips[0].Effects[0].Options[BuiltInEffects.BlendOptions.Mode]);

        TransitionInstance wipe = Assert.Single(video.Transitions);
        Assert.Equal((BuiltInTransitions.Wipe, "clip-1", "clip-2"), (wipe.Type, wipe.From, wipe.To));
        Assert.Equal(Time.FromSeconds(0.5), wipe.Duration);
        Assert.Equal(90, wipe.Parameters[BuiltInTransitions.WipeParameters.Angle]);
        Assert.Same(wipe, video.TransitionFrom("clip-1"));
        Assert.Same(wipe, video.TransitionTo("clip-2"));
        Assert.Same(wipe, video.FindTransition("transition-1"));
        Assert.Null(video.TransitionFrom("clip-2"));
        Assert.Equal("rodaje-transiciones@1.0.0/deslizar", sequence.VideoTracks[1].Transitions[0].Options[BuiltInTransitions.PresetOptions.Preset]);

        EffectInstance trackGain = Assert.Single(sequence.AudioTracks[1].Effects);
        Assert.Equal(-60, trackGain.Parameters[BuiltInEffects.AudioParameters.GainDb].Evaluate(Time.FromSeconds(15)));
    }

    [Fact]
    public void UnknownEffectWithOptions_SurvivesByteForByte()
    {
        const string effect = """
            {
              "id": "effect-x",
              "type": "otro.programa.brillo",
              "enabled": true,
              "parameters": {
                "z": {
                  "value": 1,
                  "keyframes": []
                },
                "a": {
                  "value": 2,
                  "keyframes": []
                }
              },
              "options": {
                "zeta": "último",
                "alfa": "",
                "curva": "0,0;0.5,0.7;1,1"
              }
            }
            """;
        JsonObject root = JsonNode.Parse(ExampleSaved)!.AsObject();
        root["sequences"]![0]!["videoTracks"]![1]!["clips"]![1]!["effects"]!.AsArray().Add(JsonNode.Parse(effect));
        string json = root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).ReplaceLineEndings("\n");

        Project project = ProjectSerializer.Deserialize(json);
        string saved = ProjectSerializer.Serialize(project);

        Assert.Equal(json, saved);
        Assert.Equal(saved, ProjectSerializer.Serialize(ProjectSerializer.Deserialize(saved)));
        Assert.Contains("\"zeta\": \"último\"", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrate_V2_AddsEmptyListsAndConvertsLinearGainToDecibels()
    {
        string v2 = $$"""
            {
              "schemaVersion": 2, "id": "p",
              "media": [ { "id": "m", "duration": {{10 * S}}, "hasAudio": true } ],
              "sequences": [ { "id": "s",
                "audioTracks": [ { "id": "t", "kind": "audio", "clips": [ { "id": "c", "mediaId": "m", "sourceOut": {{5 * S}},
                  "effects": [
                    { "id": "e1", "type": "rodaje.volume", "parameters": { "gain": { "value": 0.5,
                      "keyframes": [ { "time": 0, "value": 1 }, { "time": {{S}}, "value": 2, "interpolation": "easeIn" }, { "time": {{2 * S}}, "value": 0 } ] } } },
                    { "id": "e2", "type": "rodaje.transform", "parameters": {} }
                  ] } ] } ] } ]
            }
            """;

        Project project = ProjectSerializer.Deserialize(v2);

        Track track = project.Sequences[0].AudioTracks[0];
        Assert.Empty(track.Effects);
        Assert.Empty(track.Transitions);
        EffectInstance volume = track.Clips[0].Effects[0];
        Assert.Equal(BuiltInEffects.Audio, volume.Type);
        Assert.Empty(volume.Options);
        Assert.False(volume.Parameters.ContainsKey("gain"));
        Parameter gain = volume.Parameters[BuiltInEffects.AudioParameters.GainDb];
        Assert.Equal(-6.0206, gain.Value, 4);
        Assert.Equal(new double[] { 0, 6.0206, -60 }, gain.Keyframes.Select(k => Math.Round(k.Value, 4)));
        Assert.Equal(Interpolation.EaseIn, gain.Keyframes[1].Interpolation);
        Assert.Equal(BuiltInEffects.Transform, track.Clips[0].Effects[1].Type);
        Assert.Empty(track.Clips[0].Effects[1].Options);
    }

    [Fact]
    public void Migrate_V2_KeepsExistingDecibelsAndOddShapes()
    {
        JsonObject root = JsonNode.Parse("""
            { "schemaVersion": 2, "sequences": [ { "videoTracks": [ { "clips": [ { "effects": [
              { "type": "rodaje.volume", "parameters": { "gain": { "value": 2 }, "gainDb": { "value": -3 } } },
              { "type": "rodaje.volume" },
              { "type": "rodaje.volume", "parameters": { "gain": { "value": "alto", "keyframes": [ { "value": -1 } ] } } },
              { "type": 5 },
              7
            ] }, 3 ], "effects": [ { "type": "rodaje.volume", "parameters": { "gain": { "value": 1 } } } ] } ] }, "x" ] }
            """)!.AsObject();

        Assert.Equal(2, ProjectMigrator.Default.Migrate(root));

        JsonArray effects = root["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!["effects"]!.AsArray();
        Assert.Equal(-3, (double)effects[0]!["parameters"]!["gainDb"]!["value"]!);
        Assert.Equal(2, (double)effects[0]!["parameters"]!["gain"]!["value"]!);
        Assert.Equal("rodaje.audio", (string)effects[1]!["type"]!);
        Assert.Equal("alto", (string)effects[2]!["parameters"]!["gainDb"]!["value"]!);
        Assert.Equal(-60, (double)effects[2]!["parameters"]!["gainDb"]!["keyframes"]![0]!["value"]!);
        Assert.Equal("{}", effects[3]!["options"]!.ToJsonString());
        Assert.Equal(0, (double)root["sequences"]![0]!["videoTracks"]![0]!["effects"]![0]!["parameters"]!["gainDb"]!["value"]!);
        Assert.Equal("[]", root["sequences"]![0]!["videoTracks"]![0]!["transitions"]!.ToJsonString());
    }

    [Fact]
    public void Migrate_V1_ReachesCurrent()
    {
        Project project = ProjectSerializer.Deserialize("""
            { "schemaVersion": 1, "id": "p", "sequences": [ { "id": "s", "videoTracks": [ { "id": "t", "kind": "video" } ] } ] }
            """);

        Assert.Equal(Project.CurrentSchemaVersion, project.SchemaVersion);
        Assert.Empty(project.Sequences[0].Markers);
        Assert.Empty(project.Sequences[0].VideoTracks[0].Transitions);
        Assert.Empty(project.Sequences[0].VideoTracks[0].Effects);
    }

    [Theory]
    [InlineData("options")]
    [InlineData("transitions")]
    [InlineData("trackEffects")]
    [InlineData("transitionOptions")]
    public void NullItems_AreRejected(string where)
    {
        JsonObject root = JsonNode.Parse(Example)!.AsObject();
        JsonObject track = root["sequences"]![0]!["videoTracks"]![0]!.AsObject();
        switch (where)
        {
            case "options":
                track["clips"]![0]!["effects"]![1]!["options"]!["lut"] = null;
                break;
            case "transitions":
                track["transitions"]!.AsArray().Add(null);
                break;
            case "trackEffects":
                track["effects"]!.AsArray().Add(null);
                break;
            default:
                track["transitions"]![0]!["options"]!["x"] = null;
                break;
        }

        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));
    }

    [Fact]
    public void Validator_RejectsBrokenTransitions()
    {
        Project project = ProjectSerializer.Deserialize(Example);
        Track video = project.Sequences[0].VideoTracks[0];
        video.Transitions.Add(new TransitionInstance { Id = "transition-dup", Type = BuiltInTransitions.Crossfade, From = "clip-1", To = "clip-2", Duration = Time.FromSeconds(1) });
        video.Transitions.Add(new TransitionInstance { Id = "transition-back", Type = BuiltInTransitions.Crossfade, From = "clip-2", To = "clip-1", Duration = Time.Zero });
        project.Sequences[0].VideoTracks[1].Clips[1].Start += Time.FromSeconds(1);
        project.Sequences[0].AudioTracks[0].Effects.Add(new EffectInstance { Id = "transition-1", Type = BuiltInEffects.Audio });

        IReadOnlyList<string> errors = ModelValidator.Validate(project);

        Assert.Contains(errors, e => e.Contains("«transition-dup»", StringComparison.Ordinal) && e.Contains("ya tiene otra", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("«transition-back»", StringComparison.Ordinal) && e.Contains("contiguos", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("«transition-back»", StringComparison.Ordinal) && e.Contains("duración cero", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("«transition-2»", StringComparison.Ordinal) && e.Contains("contiguos", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("«transition-1» está repetido", StringComparison.Ordinal));
    }

    [Fact]
    public void Snapshot_SeesOptionsTrackEffectsAndTransitions()
    {
        Track original = ProjectSerializer.Deserialize(Example).Sequences[0].VideoTracks[0];

        Assert.True(SequenceSnapshot.TrackContentEquals(original, original.Clone()));

        Track options = original.Clone();
        options.Clips[0].Effects[1].Options["lut"] = "otro@1.0.0/frio";
        Assert.False(SequenceSnapshot.TrackContentEquals(original, options));

        Track transition = original.Clone();
        transition.Transitions[0].Duration += Time.FromTicks(1);
        Assert.False(SequenceSnapshot.TrackContentEquals(original, transition));

        Track parameter = original.Clone();
        parameter.Transitions[0].Parameters["angle"] = 45;
        Assert.False(SequenceSnapshot.TrackContentEquals(original, parameter));

        Track effects = original.Clone();
        effects.Effects.Add(new EffectInstance { Id = "fx", Type = BuiltInEffects.Color });
        Assert.False(SequenceSnapshot.TrackContentEquals(original, effects));

        Track copied = original.Clone();
        copied.Transitions[0].Options["x"] = "y";
        Assert.Empty(original.Transitions[0].Options);
    }

    [Fact]
    public void Transition_SplitsDurationAroundTheCut()
    {
        var transition = new TransitionInstance { Duration = Time.FromTicks(11) };

        Assert.Equal(Time.FromTicks(5), transition.Before);
        Assert.Equal(Time.FromTicks(6), transition.After);
    }
}
