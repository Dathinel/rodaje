using System.Diagnostics;
using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Xunit.Abstractions;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Pruebas de serialización y formato 16 para plantillas de movimiento (D-064).</summary>
public sealed class TemplateFormatTests(ITestOutputHelper output)
{
    private static string ExamplePath =>
        Path.Combine(ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Serialization", "ejemplos", "proyecto-v16.rodaje");

    private static string ExampleV16 => File.ReadAllText(ExamplePath).ReplaceLineEndings("\n");

    [Fact]
    public void Example_v16_loads_validates_has_template_with_two_layers_and_roundtrips_canonically()
    {
        Project project = ProjectSerializer.Deserialize(ExampleV16);

        Assert.Equal(16, project.SchemaVersion);
        Assert.Empty(ModelValidator.Validate(project));

        Clip clip = Assert.Single(
            project.Sequences.SelectMany(s => s.AllTracks).SelectMany(t => t.Clips),
            c => c.Generator?.Kind == GeneratorKinds.Template);

        Assert.NotNull(clip.Generator?.Template);
        Assert.Equal(2, clip.Generator.Template.Layers.Count);

        string saved = ProjectSerializer.Serialize(project) + "\n";
        Assert.Equal(ExampleV16, saved);
    }

    [Fact]
    public void Removing_template_clip_restores_minimum_schema_version_to_13()
    {
        Project project = ProjectSerializer.Deserialize(ExampleV16);

        Track track = project.Sequences[0].VideoTracks[1];
        int removed = track.Clips.RemoveAll(c => c.Generator?.Kind == GeneratorKinds.Template);
        Assert.Equal(1, removed);

        Assert.Empty(ModelValidator.Validate(project));
        Assert.Equal(13, ProjectSerializer.MinimumSchemaVersion(project));

        string saved = ProjectSerializer.Serialize(project);
        Assert.Equal(13, (int)JsonNode.Parse(saved)!["schemaVersion"]!);
    }

    [Fact]
    public void Changing_version_to_15_migrates_to_16_and_preserves_template_intact()
    {
        JsonObject root = JsonNode.Parse(ExampleV16)!.AsObject();
        root["schemaVersion"] = 15;
        string modifiedJson = root.ToJsonString();

        Project project = ProjectSerializer.Deserialize(modifiedJson);

        Assert.Equal(16, project.SchemaVersion);
        Assert.Empty(ModelValidator.Validate(project));

        Clip clip = Assert.Single(
            project.Sequences.SelectMany(s => s.AllTracks).SelectMany(t => t.Clips),
            c => c.Generator?.Kind == GeneratorKinds.Template);

        Assert.NotNull(clip.Generator?.Template);
        Assert.Equal(2, clip.Generator.Template.Layers.Count);
    }

    [Fact]
    [Trait("Categoria", "Rendimiento")]
    public void Project_with_500_template_clips_roundtrips_under_one_second()
    {
        Project project = ValidProject();
        project.Sequences[0].VideoTracks.Clear();

        const int trackCount = 10;
        const int clipsPerTrack = 50;
        for (int t = 0; t < trackCount; t++)
        {
            var track = new Track
            {
                Id = $"vt{t}",
                Kind = TrackKind.Video,
                Name = $"Video {t + 1}",
            };
            for (int i = 0; i < clipsPerTrack; i++)
            {
                track.Clips.Add(MakeTemplate($"tpl_{t}_{i}", start: i * 5, duration: 4));
            }

            project.Sequences[0].VideoTracks.Add(track);
        }

        Assert.Empty(ModelValidator.Validate(project));

        // Calentamiento del serializador para aislar la medición del JIT.
        _ = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(ValidProject()));

        var clock = Stopwatch.StartNew();
        string json = ProjectSerializer.Serialize(project);
        Project reopened = ProjectSerializer.Deserialize(json);
        clock.Stop();

        TimeSpan elapsed = clock.Elapsed;
        output.WriteLine($"Guardar y reabrir 500 clips de plantilla: {elapsed.TotalMilliseconds:F1} ms");

        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Tardó {elapsed.TotalMilliseconds:F1} ms (presupuesto < 1 s)");
        Assert.Equal(500, reopened.Sequences[0].VideoTracks.Sum(t => t.Clips.Count));
    }
}
