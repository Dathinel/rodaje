using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Model;

/// <summary>Generador <c>template</c> (D-064): copia, igualdad, reglas y formato 16.</summary>
public sealed class TemplateModelTests
{
    private static Project WithTemplate(Clip? clip = null)
    {
        Project project = ValidProject();
        project.Sequences[0].VideoTracks[1].Clips.Add(clip ?? MakeTemplate("t1", 40, 5));
        return project;
    }

    private static GeneratorTemplate TemplateOf(Project project) => project.Sequences[0].VideoTracks[1].Clips[^1].Generator!.Template!;

    [Fact]
    public void A_valid_template_passes_and_is_saved_as_16_and_reads_back_the_same()
    {
        Project project = WithTemplate();
        Assert.Empty(ModelValidator.Validate(project));

        string written = ProjectSerializer.Serialize(project);
        Project read = ProjectSerializer.Deserialize(written);

        Assert.Equal(16, (int)JsonNode.Parse(written)!["schemaVersion"]!);
        Assert.Equal(written, ProjectSerializer.Serialize(read));
        Assert.True(TemplateOf(project).ContentEquals(TemplateOf(read)));
    }

    [Fact]
    public void Without_templates_the_check_is_false()
    {
        Assert.Equal(13, ProjectSerializer.MinimumSchemaVersion(ValidProject()));
    }

    [Fact]
    public void A_clone_does_not_share_layers_values_or_styles()
    {
        ClipGenerator original = MakeTemplate("t1", 0, 5).Generator!;
        ClipGenerator copy = original.Clone();
        Assert.True(original.ContentEquals(copy));

        copy.Template!.Layers[0].Text = "Otra";
        copy.Template.Layers[1].Style.Size = 10;
        copy.Template.Values["cargo"] = JsonDocument.Parse("\"Jefa\"").RootElement;

        Assert.Equal("Ana Ruiz", original.Template!.Layers[0].Text);
        Assert.Equal(96, original.Template.Layers[1].Style.Size);
        Assert.False(original.Template.Values.ContainsKey("cargo"));
        Assert.False(original.SameContent(copy));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("layer")]
    [InlineData("x")]
    [InlineData("start")]
    [InlineData("value")]
    [InlineData("source")]
    [InlineData("min")]
    public void Same_content_sees_every_field(string field)
    {
        ClipGenerator original = MakeTemplate("t1", 0, 5).Generator!;
        ClipGenerator copy = original.Clone();
        GeneratorTemplate t = copy.Template!;
        switch (field)
        {
            case "text": t.Layers[0].Text = "B"; break;
            case "layer": t.Layers.RemoveAt(1); break;
            case "x": t.Layers[0].X += 1; break;
            case "start": t.Layers[1].Start = 0.3; break;
            case "value": t.Values["nombre"] = JsonDocument.Parse("\"Otra\"").RootElement; break;
            case "source": t.Source = null; break;
            default: t.MinSeconds = 2; break;
        }

        Assert.False(original.SameContent(copy));
    }

    [Fact]
    public void A_generator_with_null_text_is_reported_not_thrown()
    {
        Clip clip = MakeTemplate("t1", 40, 5);
        clip.Generator!.Text = null!;

        Assert.Null(Record.Exception(() => ModelValidator.Validate(WithTemplate(clip))));
    }

    public static TheoryData<string> Broken => ["kind", "template-on-text", "no-layers", "17-layers", "dup-id", "big-doc", "bad-value", "neg-start", "bad-style", "bad-min", "bad-source", "long-nested", "long-array", "long-option", "control-text"];

    [Theory]
    [MemberData(nameof(Broken))]
    public void Broken_templates_are_rejected(string what)
    {
        Clip clip = MakeTemplate("t1", 40, 5);
        ClipGenerator g = clip.Generator!;
        GeneratorTemplate t = g.Template!;
        switch (what)
        {
            case "kind": g.Kind = GeneratorKinds.Text; g.Style = FullStyle(); break;
            case "template-on-text": g.Template = null; break;
            case "no-layers": t.Layers.Clear(); break;
            case "17-layers": t.Layers = [.. Enumerable.Range(0, 17).Select(i => new TemplateLayer { Id = $"c{i}", Text = "x", Style = FullStyle() })]; break;
            case "dup-id": t.Layers[1].Id = "nombre"; break;
            case "big-doc": t.Document = new string('a', GeneratorTemplate.MaxDocumentBytes + 1); break;
            case "bad-value": t.Values["nombre"] = JsonDocument.Parse("[1,2]").RootElement; break;
            case "neg-start": t.Layers[0].Start = -0.1; break;
            case "bad-style": t.Layers[0].Style.Size = 0; break;
            case "bad-min": t.MinSeconds = 601; break;
            case "bad-source": t.Source = "no es referencia"; break;
            case "long-nested": t.Values["nombre"] = JsonSerializer.SerializeToElement(new JsonObject { ["family"] = new string('a', 10_001) }); break;
            case "long-array": t.Values["nombre"] = JsonSerializer.SerializeToElement(new JsonObject { ["features"] = new JsonArray([.. Enumerable.Range(0, 5000).Select(i => (JsonNode)"liga")]) }); break;
            case "long-option":
                clip.Effects.Add(new EffectInstance { Id = "fx", Type = BuiltInEffects.Transform, Options = { [BuiltInEffects.CommonOptions.TemplateValues] = new string('a', ModelValidator.MaxTemplateValuesLength + 1) } });
                break;
            default: t.Layers[0].Text = "a\u0001b"; break;
        }

        Assert.NotEmpty(ModelValidator.Validate(WithTemplate(clip)));
    }

    [Fact]
    public void Layer_texts_count_towards_the_project_total()
    {
        // 13 rótulos de 16 capas de 10 000 caracteres: 2 080 000, más que el tope de 2 000 000.
        Project project = ValidProject();
        string chunk = new('a', ModelValidator.MaxTextLength);
        for (int n = 0; n < 13; n++)
        {
            Clip clip = MakeTemplate($"t{n}", 40 + (n * 6), 5);
            clip.Generator!.Template!.Layers = [.. Enumerable.Range(0, 16).Select(i => new TemplateLayer { Id = $"z{i}", Text = chunk, Style = FullStyle() })];
            project.Sequences[0].VideoTracks[1].Clips.Add(clip);
        }

        Assert.Contains(ModelValidator.Validate(project), e => e.Contains("Los textos del proyecto suman", StringComparison.Ordinal));
    }
}
