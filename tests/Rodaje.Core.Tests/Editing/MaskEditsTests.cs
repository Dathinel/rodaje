using System.Numerics;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;
using O = Rodaje.Core.Model.BuiltInEffects.MaskOptions;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Core.Tests.Editing;

public sealed class MaskEditsTests
{
    private static TrimFixture Clip() => new TrimFixture().Add("v1", "v", "m2", 0, 200, 0);

    [Fact]
    public void Adding_a_mask_is_one_undo_step_and_the_project_stays_valid()
    {
        TrimFixture f = Clip();
        var undo = new UndoStack(f.Project);
        Vector2[] triangle = [new(0, -0.5f), new(0.5f, 0.5f), new(-0.5f, 0.5f)];
        undo.Execute(Edits.AddMask("seq", "v", MaskShape.Path, 0.3, 0.4, 0.2, 0.3, triangle));

        EffectInstance mask = Assert.Single(f.Clip("v").Effects);
        Assert.Equal(BuiltInEffects.Mask, mask.Type);
        Assert.Equal("path", mask.Options[O.Shape]);
        Assert.Equal(triangle, MaskGeometry.ParsePath(mask.Options[O.Path]));
        Assert.Equal(0.3, mask.Parameters[M.X].Value);
        Assert.Empty(ModelValidator.Validate(f.Project));

        undo.Undo();
        Assert.Empty(f.Clip("v").Effects);
    }

    [Fact]
    public void A_mask_with_keyframes_evaluates_between_them()
    {
        TrimFixture f = Clip();
        var undo = new UndoStack(f.Project);
        undo.Execute(Edits.AddMask("seq", "v", MaskShape.Ellipse));
        string id = f.Clip("v").Effects[0].Id;
        undo.Execute(Edits.SetKeyframe("seq", "v", id, M.X, F(0), 0.2));
        undo.Execute(Edits.SetKeyframe("seq", "v", id, M.X, F(100), 0.8));
        undo.Execute(Edits.SetEffectOption("seq", "v", id, O.Invert, "on"));

        MaskSpec spec = MaskGeometry.Evaluate(f.Clip("v").Effects[0], F(50));
        Assert.Equal(0.5, spec.X, 6);
        Assert.Equal(MaskShape.Ellipse, spec.Shape);
        Assert.True(spec.Invert);
        Assert.Empty(ModelValidator.Validate(f.Project));
    }

    [Fact]
    public void The_image_mask_replaces_the_previous_one()
    {
        TrimFixture f = Clip();
        var undo = new UndoStack(f.Project);
        undo.Execute(Edits.SetImageMask("seq", "v", "file:proyecto.medios/fondo-1.mkv", F(10)));
        undo.Execute(Edits.SetImageMask("seq", "v", "file:proyecto.medios/fondo-2.mkv", F(0)));

        EffectInstance mask = Assert.Single(f.Clip("v").Effects);
        Assert.Equal("image", mask.Options[O.Shape]);
        Assert.Equal("file:proyecto.medios/fondo-2.mkv", mask.Options[O.Matte]);
        Assert.Equal("0", mask.Options[O.MatteStart]);
        Assert.Throws<EditException>(() => undo.Execute(Edits.SetImageMask("seq", "v", "proyecto.medios/x.mkv", F(0))));
    }

    [Fact]
    public void Reframing_keys_replace_position_and_scale_in_one_step_and_drop_keys_outside_the_clip()
    {
        TrimFixture f = Clip();
        var undo = new UndoStack(f.Project);
        undo.Execute(Edits.SetTransformKeys("seq", "v", [new(F(0), 10, 0, 1.2), new(F(100), -50, 5, 1.5), new(F(500), 99, 99, 9)]));

        EffectInstance transform = Assert.Single(f.Clip("v").Effects);
        Assert.Equal([F(0), F(100)], transform.Parameters[T.X].Keyframes.Select(k => k.Time));
        Assert.Equal(-20, transform.Parameters[T.X].Evaluate(F(50)), 6);
        Assert.Equal(1.35, transform.Parameters[T.Scale].Evaluate(F(50)), 6);

        undo.Undo();
        Assert.Empty(f.Clip("v").Effects);
    }

    [Fact]
    public void A_path_round_trips_and_bad_text_is_ignored()
    {
        Vector2[] points = [new(-0.5f, -0.25f), new(0.125f, 0.5f), new(0.5f, -0.5f)];
        Assert.Equal(points, MaskGeometry.ParsePath(MaskGeometry.FormatPath(points)));
        Assert.Empty(MaskGeometry.ParsePath("0,0;1,1"));
        Assert.Empty(MaskGeometry.ParsePath("0,0;1;2,2"));
        Assert.Empty(MaskGeometry.ParsePath("a,b;c,d;e,f"));
    }

    [Fact]
    public void A_drawn_stroke_becomes_a_box_and_a_short_path()
    {
        // Un círculo de 2000 puntos en (0,3; 0,6) con radio 0,1.
        Vector2[] stroke = [.. Enumerable.Range(0, 2000).Select(i =>
            new Vector2(0.3f + (0.1f * MathF.Cos(i * MathF.Tau / 2000)), 0.6f + (0.1f * MathF.Sin(i * MathF.Tau / 2000))))];
        (double x, double y, double width, double height, Vector2[] points) = MaskGeometry.FromStroke(stroke);

        Assert.Equal(0.3, x, 3);
        Assert.Equal(0.6, y, 3);
        Assert.Equal(0.2, width, 3);
        Assert.Equal(0.2, height, 3);
        Assert.InRange(points.Length, 12, MaskGeometry.MaxPathPoints);
        Assert.All(points, p => Assert.InRange(p.Length(), 0.49f, 0.501f));
    }

    [Theory]
    [InlineData(MaskShape.Rectangle)]
    [InlineData(MaskShape.Ellipse)]
    [InlineData(MaskShape.Path)]
    public void Coverage_is_full_inside_empty_outside_and_half_on_the_edge(MaskShape shape)
    {
        Vector3[] square = [new(-0.5f, -0.5f, 1), new(0.5f, -0.5f, 1), new(0.5f, 0.5f, 1), new(-0.5f, 0.5f, 1)];
        var spec = new MaskSpec(shape, 0.5, 0.5, 0.5, 0.5, 0, 0.1, 1, false);
        Assert.Equal(1, MaskGeometry.Factor(spec, 100, 50, 200, 100, square), 6);
        Assert.Equal(0, MaskGeometry.Factor(spec, 2, 2, 200, 100, square), 6);
        Assert.Equal(0.5, MaskGeometry.Factor(spec, 150, 50, 200, 100, square), 2);
        Assert.Equal(0, MaskGeometry.Factor(spec with { Invert = true }, 100, 50, 200, 100, square), 6);
        Assert.Equal(0.4, MaskGeometry.Factor(spec with { Opacity = 0.4 }, 100, 50, 200, 100, square), 6);
    }

    [Fact]
    public void The_registry_rejects_control_characters_in_text_options()
    {
        OptionDefinition path = EffectRegistry.Find(BuiltInEffects.Mask)!.FindOption(O.Path)!;
        Assert.True(path.Accepts("0,0;1,0;0,1"));
        Assert.False(path.Accepts("0,0\n1,0"));
        Assert.False(path.Accepts(new string('1', OptionDefinition.MaxTextLength + 1)));
    }

    [Fact]
    public void A_bezier_mask_adds_to_the_others_and_keeps_its_name_and_color()
    {
        TrimFixture f = Clip();
        var undo = new UndoStack(f.Project);
        undo.Execute(Edits.AddMask("seq", "v", MaskShape.Ellipse));
        undo.Execute(Edits.AddVertexMask("seq", "v", MaskShape.Bezier, 0.4, 0.5, 0.3, 0.3, MaskPath.Star(5)));

        EffectInstance bezier = f.Clip("v").Effects[1];
        Assert.Equal("bezier", bezier.Options[O.Shape]);
        Assert.Equal("add", bezier.Options[O.Mode]);
        Assert.Equal("Máscara 2", bezier.Options[O.Name]);
        Assert.Equal(MaskEdits.Palette[1], bezier.Options[O.Color]);
        Assert.Equal(10, MaskGeometry.Vertices(bezier, F(0)).Length);
        Assert.Empty(ModelValidator.Validate(f.Project));
        Assert.Throws<EditException>(() => undo.Execute(Edits.AddVertexMask("seq", "v", MaskShape.Bezier, 0.5, 0.5, 0.2, 0.2, MaskPath.Rectangle()[..2])));
    }

    [Fact]
    public void Shape_keys_animate_the_vertices_and_editing_writes_the_key_under_the_playhead()
    {
        TrimFixture f = Clip();
        var undo = new UndoStack(f.Project);
        undo.Execute(Edits.AddVertexMask("seq", "v", MaskShape.Bezier, 0.5, 0.5, 0.5, 0.5, MaskPath.Rectangle()));
        string id = f.Clip("v").Effects[0].Id;
        undo.Execute(Edits.SetMaskShapeKey("seq", "v", id, F(0)));
        MaskVertex[] bigger = [.. MaskPath.Rectangle().Select(v => v with { Point = v.Point * 1.5f })];
        undo.Execute(Edits.SetMaskVertices("seq", "v", id, bigger, F(100)));

        EffectInstance mask = f.Clip("v").Effects[0];
        Assert.True(MaskGeometry.IsShapeAnimated(mask));
        Assert.Equal(-0.625f, MaskGeometry.Vertices(mask, F(50))[0].Point.X, 4);
        Assert.Empty(ModelValidator.Validate(f.Project));

        undo.Execute(Edits.RemoveMaskShapeKey("seq", "v", id, F(0)));
        undo.Execute(Edits.RemoveMaskShapeKey("seq", "v", id, F(100)));
        mask = f.Clip("v").Effects[0];
        Assert.False(MaskGeometry.IsShapeAnimated(mask));
        Assert.Equal(-0.75f, MaskGeometry.Vertices(mask, F(0))[0].Point.X, 4);
        Assert.Throws<EditException>(() => undo.Execute(Edits.SetMaskShapeKey("seq", "v", id, F(500))));
    }

    [Theory]
    [InlineData(MaskShape.Rectangle, 0)]
    [InlineData(MaskShape.Rectangle, 0.6)]
    [InlineData(MaskShape.Ellipse, 0)]
    public void Converting_to_bezier_keeps_the_same_coverage(MaskShape shape, double roundness)
    {
        TrimFixture f = Clip();
        var undo = new UndoStack(f.Project);
        undo.Execute(Edits.AddMask("seq", "v", shape, 0.45, 0.55, 0.4, 0.5));
        string id = f.Clip("v").Effects[0].Id;
        undo.Execute(Edits.SetParameter("seq", "v", id, M.Rotation, 20, F(0)));
        undo.Execute(Edits.SetParameter("seq", "v", id, M.Feather, 0.02, F(0)));
        undo.Execute(Edits.SetParameter("seq", "v", id, M.Roundness, roundness, F(0)));
        MaskSpec before = MaskGeometry.Evaluate(f.Clip("v").Effects[0], F(0));

        undo.Execute(Edits.ConvertMaskToBezier("seq", "v", id, F(0), 320.0 / 180));
        EffectInstance mask = f.Clip("v").Effects[0];
        MaskSpec after = MaskGeometry.Evaluate(mask, F(0));
        Assert.Equal(MaskShape.Bezier, after.Shape);
        Vector3[] polyline = MaskGeometry.Polyline(MaskShape.Bezier, MaskGeometry.Vertices(mask, F(0)), (float)(after.Width * 320), (float)(after.Height * 180),
            (float)(after.Feather * 180));
        double worst = 0;
        for (int y = 0; y < 180; y += 3)
        {
            for (int x = 0; x < 320; x += 3)
            {
                worst = Math.Max(worst, Math.Abs(MaskGeometry.Factor(before, x + 0.5, y + 0.5, 320, 180)
                    - MaskGeometry.Factor(after, x + 0.5, y + 0.5, 320, 180, polyline)));
            }
        }

        Assert.InRange(worst, 0, 0.12);
    }

    [Fact]
    public void A_mask_can_limit_an_effect_and_the_zone_preset_wires_it()
    {
        TrimFixture f = Clip();
        var undo = new UndoStack(f.Project);
        var zone = Edits.AddMaskedZone("seq", "v", ZoneEffect.Pixelate);
        undo.Execute(zone);
        (string effectId, string maskId) = (zone.CreatedIds[0], zone.CreatedIds[1]);
        Clip clip = f.Clip("v");
        Assert.Equal(BuiltInEffects.Pixelate, clip.Effects.Single(e => e.Id == effectId).Type);
        Assert.Equal(effectId, clip.Effects.Single(e => e.Id == maskId).Options[O.Target]);

        undo.Execute(Edits.SetMaskTarget("seq", "v", maskId, ""));
        Assert.False(f.Clip("v").Effects.Single(e => e.Id == maskId).Options.ContainsKey(O.Target));
        undo.Execute(Edits.AddEffect("seq", "v", BuiltInEffects.Transform));
        string transform = f.Clip("v").Effects[^1].Id;
        Assert.Throws<EditException>(() => undo.Execute(Edits.SetMaskTarget("seq", "v", maskId, transform)));
        Assert.Throws<EditException>(() => undo.Execute(Edits.SetMaskTarget("seq", "v", maskId, "no-existe")));
        Assert.Empty(ModelValidator.Validate(f.Project));
    }

    [Fact]
    public void Splitting_and_pasting_attributes_keep_masks_on_their_effects_and_shape_keys_in_place()
    {
        TrimFixture f = Clip().Add("v2", "w", "m2", 50, 150, 0);
        var undo = new UndoStack(f.Project);
        var zone = Edits.AddMaskedZone("seq", "v", ZoneEffect.Blur);
        undo.Execute(zone);
        string maskId = zone.CreatedIds[1];
        undo.Execute(Edits.ConvertMaskToBezier("seq", "v", maskId, F(0), 16.0 / 9));
        undo.Execute(Edits.SetMaskShapeKey("seq", "v", maskId, F(20)));

        ClipAttributes copied = ClipAttributes.Capture(f.Sequence, "v");
        undo.Execute(Edits.PasteAttributes("seq", ["w"], copied, ClipAttributeKinds.Effects));
        Clip w = f.Clip("w");
        EffectInstance pastedMask = w.Effects.Single(e => e.Type == BuiltInEffects.Mask);
        Assert.Equal(w.Effects.Single(e => e.Type == BuiltInEffects.Blur).Id, pastedMask.Options[O.Target]);
        // El keyframe estaba 20 cuadros después del inicio de «v»; en «w» (que empieza en el cuadro 50 del origen) cae en el 70.
        Assert.Equal(F(70), MaskShapeTrack.Parse(pastedMask.Options[O.ShapeKeys])!.TimeAt(0));

        Clip right = TrackOps.SplitOff(f.Ctx, f.Clip("v"), F(100));
        EffectInstance splitMask = right.Effects.Single(e => e.Type == BuiltInEffects.Mask);
        Assert.Equal(right.Effects.Single(e => e.Type == BuiltInEffects.Blur).Id, splitMask.Options[O.Target]);
        Assert.Empty(ModelValidator.Validate(f.Project));
    }

    [Fact]
    public void Tracking_replaces_the_keys_in_its_range_and_keeps_the_rest()
    {
        TrimFixture f = Clip();
        var undo = new UndoStack(f.Project);
        undo.Execute(Edits.AddVertexMask("seq", "v", MaskShape.Bezier, 0.5, 0.5, 0.2, 0.2, MaskPath.Rectangle()));
        string id = f.Clip("v").Effects[0].Id;
        undo.Execute(Edits.SetKeyframe("seq", "v", id, M.X, F(150), 0.9));
        MaskTrackKey[] keys = [.. Enumerable.Range(10, 21).Select(i =>
            new MaskTrackKey(F(i), 0.5 + (0.01 * (i - 10)), 0.5, 0.2, 0.2, i - 10,
                [.. MaskPath.Rectangle().Select(v => v with { Point = v.Point + new Vector2(0.001f * (i - 10), 0) })]))];
        undo.Execute(Edits.ApplyMaskTrack("seq", "v", id, keys, MaskTrackParts.Position | MaskTrackParts.Rotation | MaskTrackParts.Shape));

        EffectInstance mask = f.Clip("v").Effects[0];
        Assert.Equal(22, mask.Parameters[M.X].Keyframes.Count);
        Assert.Equal(0.9, mask.Parameters[M.X].Evaluate(F(150)), 9);
        Assert.Equal(0.65, mask.Parameters[M.X].Evaluate(F(25)), 9);
        Assert.Equal(15, mask.Parameters[M.Rotation].Evaluate(F(25)), 9);
        Assert.False(mask.Parameters.TryGetValue(M.Width, out Parameter? width) && width.Keyframes.Count > 0);
        // Los vértices se mueven en línea recta: el seguimiento por vértice se reduce a sus dos extremos.
        MaskShapeTrack track = MaskShapeTrack.Parse(mask.Options[O.ShapeKeys])!;
        Assert.Equal(2, track.Count);
        Assert.Equal(-0.495f, track.Evaluate(F(15))[0].Point.X, 4);
        Assert.Empty(ModelValidator.Validate(f.Project));
    }
}
