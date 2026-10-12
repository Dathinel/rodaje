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
        Vector2[] square = [new(-0.5f, -0.5f), new(0.5f, -0.5f), new(0.5f, 0.5f), new(-0.5f, 0.5f)];
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
}
