using System.Text.Json.Nodes;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;
using O = Rodaje.Core.Model.BuiltInEffects.MaskOptions;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Formato 8: máscaras con modo, vértices, forma animada y destino; las de antes se siguen multiplicando.</summary>
public sealed class MaskFormatTests
{
    [Fact]
    public void A_format_7_mask_opens_as_an_intersection_and_saves_as_format_8()
    {
        TrimFixture f = new TrimFixture().Add("v1", "v", "m2", 0, 200, 0);
        new UndoStack(f.Project).Execute(Edits.AddMask("seq", "v", MaskShape.Ellipse));
        JsonObject root = JsonNode.Parse(ProjectSerializer.Serialize(f.Project))!.AsObject();
        root["schemaVersion"] = 7;
        JsonObject options = root["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!["effects"]![0]!["options"]!.AsObject();
        options.Remove(O.Mode);
        options.Remove(O.Name);
        options.Remove(O.Color);

        Project project = ProjectSerializer.Deserialize(root.ToJsonString());

        Assert.Equal(Project.CurrentSchemaVersion, project.SchemaVersion);
        EffectInstance mask = project.Sequences[0].GetClip("v").Clip.Effects[0];
        Assert.Equal(MaskMode.Intersect, MaskGeometry.Evaluate(mask, F(0)).Mode);
        Assert.Contains("\"schemaVersion\": 10", ProjectSerializer.Serialize(project), StringComparison.Ordinal);
    }

    [Fact]
    public void Vertices_shape_keys_and_targets_round_trip()
    {
        TrimFixture f = new TrimFixture().Add("v1", "v", "m2", 0, 200, 0);
        var undo = new UndoStack(f.Project);
        var zone = Edits.AddMaskedZone("seq", "v", ZoneEffect.Blur);
        undo.Execute(zone);
        undo.Execute(Edits.ConvertMaskToBezier("seq", "v", zone.CreatedIds[1], F(0), 16.0 / 9));
        undo.Execute(Edits.SetMaskShapeKey("seq", "v", zone.CreatedIds[1], F(10)));

        Project read = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(f.Project));
        EffectInstance before = f.Clip("v").Effects[1];
        EffectInstance after = read.Sequences[0].GetClip("v").Clip.Effects[1];
        Assert.Equal(before.Options, after.Options);
        Assert.Empty(ModelValidator.Validate(read));
    }

    [Fact]
    public void Mask_slots_follow_the_registry()
    {
        EffectDefinition mask = EffectRegistry.Find(BuiltInEffects.Mask)!;
        int choices = mask.Options.Count(o => o.Kind == OptionKind.Choice);
        Assert.Equal(MaskGeometry.ShapeSlot, mask.Parameters.Length);
        Assert.Equal(O.Shape, mask.Options[MaskGeometry.ShapeSlot - mask.Parameters.Length].Name);
        Assert.Equal(O.Invert, mask.Options[MaskGeometry.InvertSlot - mask.Parameters.Length].Name);
        Assert.Equal(O.Mode, mask.Options[MaskGeometry.ModeSlot - mask.Parameters.Length].Name);
        Assert.Equal(MaskGeometry.MatteTimeSlot, mask.Parameters.Length + choices);
        Assert.True(MaskGeometry.TargetSlot < EffectDefinition.MaxParameters);
    }
}
