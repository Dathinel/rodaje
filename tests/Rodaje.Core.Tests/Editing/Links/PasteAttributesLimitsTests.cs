using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Links;

/// <summary>«Pegar atributos»: la velocidad 1× deja tono y muestreo por defecto, y los keyframes no pasan del clip destino.</summary>
public sealed class PasteAttributesLimitsTests
{
    private static EffectInstance Transform(params (long Frame, double Value)[] keyframes) => new()
    {
        Id = "fx-s",
        Type = BuiltInEffects.Transform,
        Parameters =
        {
            ["x"] = new Parameter
            {
                Value = keyframes[0].Value,
                Keyframes = [.. keyframes.Select(k => new Keyframe(F(k.Frame), k.Value))],
            },
        },
    };

    private static List<(Time Time, double Value)> Keys(Clip clip) =>
        [.. clip.Effects.Single(e => e.Type == BuiltInEffects.Transform).Parameters["x"].Keyframes.Select(k => (k.Time, k.Value))];

    [Fact]
    public void Pasting_one_times_speed_restores_pitch_and_frame_sampling()
    {
        var f = new TrimFixture()
            .Add("v1", "s", "m1", 0, 50, 0)
            .Add("v1", "t", "m2", 0, 100, 50, link: "T").Add("a1", "ta", "m2", 0, 100, 50, link: "T");
        TimelineEdits.SetSpeed(f.Ctx, "t", new Rational(2, 1));
        TimelineEdits.SetPreservePitch(f.Ctx, "t", false);
        f.Clip("t").Speed!.FrameSampling = FrameSampling.Blend;
        f.Clip("ta").Speed!.FrameSampling = FrameSampling.Blend;

        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Speed);

        f.AssertValid();
        foreach (string id in new[] { "t", "ta" })
        {
            Clip clip = f.Clip(id);
            Assert.True(clip.Speed is null || (clip.Speed.PreservePitch && clip.Speed.FrameSampling == FrameSampling.Nearest));
            Assert.Equal(clip.SourceDuration, clip.Duration);
        }

        // Al volver a acelerarlo arranca con el tono y el muestreo por defecto, no con los que tenía.
        TimelineEdits.SetSpeed(f.Ctx, "t", new Rational(2, 1));
        Assert.True(f.Clip("t").Speed!.PreservePitch);
        Assert.Equal(FrameSampling.Nearest, f.Clip("t").Speed!.FrameSampling);
    }

    [Fact]
    public void Keyframes_past_the_end_of_a_shorter_target_are_cut_at_its_end()
    {
        // Origen de 100 cuadros con x de 0 a 80 en 80 cuadros; destino de 40: queda de 0 a 40, con el valor que tenía ahí.
        var f = new TrimFixture().Add("v1", "s", "m1", 0, 100, 0).Add("v1", "t", "m2", 0, 40, 100);
        f.Clip("s").Effects.Add(Transform((0, 0), (80, 80)));

        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Transform);

        f.AssertValid();
        List<(Time Time, double Value)> keys = Keys(f.Clip("t"));
        Assert.Equal([F(0), F(40)], keys.Select(k => k.Time));
        Assert.Equal(0, keys[0].Value, 9);
        Assert.Equal(40, keys[1].Value, 9);
    }

    [Fact]
    public void Keyframes_before_the_start_are_cut_at_the_start()
    {
        // El origen empieza en su cuadro 10 y tiene un keyframe en el 0 (10 cuadros antes de su inicio).
        var f = new TrimFixture().Add("v1", "s", "m1", 10, 60, 0).Add("v1", "t", "m2", 0, 50, 50);
        f.Clip("s").Effects.Add(Transform((0, 0), (20, 100)));

        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Transform);

        f.AssertValid();
        List<(Time Time, double Value)> keys = Keys(f.Clip("t"));
        Assert.Equal([F(0), F(10)], keys.Select(k => k.Time));
        Assert.Equal(50, keys[0].Value, 9);
        Assert.Equal(100, keys[1].Value, 9);
    }

    [Fact]
    public void Keyframes_inside_the_target_are_kept_as_they_are()
    {
        var f = new TrimFixture().Add("v1", "s", "m1", 0, 50, 0).Add("v1", "t", "m2", 0, 50, 50);
        f.Clip("s").Effects.Add(Transform((0, 3), (25, 7), (50, 1)));

        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Transform);

        Assert.Equal([(F(0), 3.0), (F(25), 7.0), (F(50), 1.0)], Keys(f.Clip("t")));
    }
}
