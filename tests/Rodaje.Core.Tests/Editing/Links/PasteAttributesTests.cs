using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Links;

/// <summary>«Copiar atributos» y «Pegar atributos»: efectos, transformación, velocidad y volumen, en un paso.</summary>
public sealed class PasteAttributesTests
{
    private static EffectInstance Effect(string id, string type, string? parameter = null, params (long Frame, double Value)[] keyframes)
    {
        var effect = new EffectInstance { Id = id, Type = type };
        if (parameter is not null)
        {
            effect.Parameters[parameter] = new Parameter
            {
                Value = keyframes.Length == 0 ? 0 : keyframes[0].Value,
                Keyframes = [.. keyframes.Select(k => new Keyframe(F(k.Frame), k.Value))],
            };
        }

        return effect;
    }

    [Fact]
    public void Transform_keyframes_land_at_the_same_place_of_the_target_clip()
    {
        // El origen empieza en su cuadro 10 y el destino en el 50: un keyframe a 5 cuadros del inicio sigue a 5.
        var f = new TrimFixture().Add("v1", "s", "m1", 10, 60, 0).Add("v1", "t", "m2", 50, 100, 60);
        f.Clip("s").Effects.Add(Effect("fx-s", BuiltInEffects.Transform, "x", (15, 0), (30, 100)));
        f.Clip("t").Effects.Add(Effect("fx-c", BuiltInEffects.Color, "exposure", (50, 1)));

        ClipAttributes copied = ClipAttributes.Capture(f.Sequence, "s");
        ClipAttributeEdits.Paste(f.Ctx, ["t"], copied, ClipAttributeKinds.Transform);

        f.AssertValid();
        Clip target = f.Clip("t");
        Assert.Equal([BuiltInEffects.Color, BuiltInEffects.Transform], target.Effects.Select(e => e.Type));
        EffectInstance transform = target.Effects[1];
        Assert.NotEqual("fx-s", transform.Id);
        Assert.Equal([F(55), F(70)], transform.Parameters["x"].Keyframes.Select(k => k.Time));
        Assert.Equal("fx-c", target.Effects[0].Id);
        Assert.Equal([F(15), F(30)], f.Clip("s").Effects[0].Parameters["x"].Keyframes.Select(k => k.Time));
    }

    [Fact]
    public void Each_category_replaces_the_one_of_the_target_where_it_was()
    {
        var f = new TrimFixture().Add("v1", "s", "m1", 0, 50, 0).Add("v1", "t", "m1", 0, 50, 50);
        f.Clip("s").Effects.Add(Effect("fx-v", BuiltInEffects.Vignette));
        f.Clip("s").Effects.Add(Effect("fx-b", BuiltInEffects.Blur));
        Clip target = f.Clip("t");
        target.Effects.Add(Effect("t-tr", BuiltInEffects.Transform));
        target.Effects.Add(Effect("t-co", BuiltInEffects.Color));
        target.Effects.Add(Effect("t-bl", BuiltInEffects.Blend));

        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Effects);

        f.AssertValid();
        Assert.Equal(
            [BuiltInEffects.Transform, BuiltInEffects.Vignette, BuiltInEffects.Blur, BuiltInEffects.Blend],
            target.Effects.Select(e => e.Type));

        // El origen no tiene transformación: pegarla deja el destino como recién importado.
        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Transform);
        Assert.Equal([BuiltInEffects.Vignette, BuiltInEffects.Blur], target.Effects.Select(e => e.Type));
    }

    [Fact]
    public void Picture_goes_to_video_clips_and_volume_to_their_linked_audio()
    {
        var f = new TrimFixture()
            .Add("v1", "sv", "m1", 0, 50, 0, link: "S").Add("a1", "sa", "m1", 0, 50, 0, link: "S")
            .Add("v1", "tv", "m2", 0, 50, 50, link: "T").Add("a1", "ta", "m2", 0, 50, 50, link: "T");
        f.Clip("sv").Effects.Add(Effect("fx-t", BuiltInEffects.Transform, "scale", (0, 2)));
        f.Clip("sa").Effects.Add(Effect("fx-a", BuiltInEffects.Audio, "gainDb", (10, -6)));

        // Copiado desde el audio, pegado eligiendo solo el video: el grupo entero entra.
        ClipAttributes copied = ClipAttributes.Capture(f.Sequence, "sa");
        Assert.Equal(ClipAttributeKinds.All, copied.Available);
        ClipAttributeEdits.Paste(f.Ctx, ["tv"], copied, ClipAttributeKinds.All);

        f.AssertValid();
        Assert.Equal([BuiltInEffects.Transform], f.Clip("tv").Effects.Select(e => e.Type));
        Assert.Equal([BuiltInEffects.Audio], f.Clip("ta").Effects.Select(e => e.Type));
        Assert.Equal([F(10)], f.Clip("ta").Effects[0].Parameters["gainDb"].Keyframes.Select(k => k.Time));
    }

    [Fact]
    public void Speed_is_pasted_once_per_group_and_what_follows_joins()
    {
        var f = new TrimFixture()
            .Add("v1", "s", "m2", 0, 100, 0)
            .Add("v1", "t", "m1", 0, 50, 100, link: "T").Add("a1", "ta", "m1", 0, 50, 100, link: "T")
            .Add("v1", "n", "m1", 50, 60, 150);
        TimelineEdits.SetSpeed(f.Ctx, "s", new Rational(2, 1));
        TimelineEdits.SetReverse(f.Ctx, "s", true);
        TimelineEdits.SetPreservePitch(f.Ctx, "s", false);
        Time targetStart = f.Clip("t").Start;

        ClipAttributeEdits.Paste(f.Ctx, ["t", "ta"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Speed);

        f.AssertValid();
        Clip target = f.Clip("t");
        Assert.Equal(new Rational(2, 1), target.Speed!.Rate);
        Assert.True(target.Speed.Reverse);
        Assert.False(target.Speed.PreservePitch);
        Assert.Equal(target.Speed.Rate, f.Clip("ta").Speed!.Rate);
        Assert.Equal(F(25), target.Duration);
        Assert.Equal(target.End, f.Clip("n").Start);
        Assert.Equal(targetStart, target.Start);
    }

    [Fact]
    public void Pasting_one_times_speed_takes_away_speed_reverse_and_curve()
    {
        var f = new TrimFixture().Add("v1", "s", "m1", 0, 50, 0).Add("v1", "t", "m2", 0, 100, 50);
        TimelineEdits.SetCurve(f.Ctx, "t", SpeedPresets.Curve(SpeedPreset.Hero));
        TimelineEdits.SetReverse(f.Ctx, "t", true);

        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Speed);

        f.AssertValid();
        Clip target = f.Clip("t");
        Assert.Null(target.Speed);
        Assert.Equal(target.SourceDuration, target.Duration);
    }

    [Fact]
    public void A_curve_is_fitted_to_each_target()
    {
        var f = new TrimFixture().Add("v1", "s", "m2", 0, 100, 0).Add("v2", "t", "m1", 0, 40, 0);
        TimelineEdits.SetCurve(f.Ctx, "s", SpeedPresets.Curve(SpeedPreset.Bullet));

        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Speed);

        f.AssertValid();
        Assert.Equal(f.Clip("s").Speed!.Curve!.Points, f.Clip("t").Speed!.Curve!.Points);
        // El material se conserva salvo el final, que la curva alinea al cuadro de la secuencia.
        Assert.Equal(Time.Zero, f.Clip("t").SourceIn);
        Assert.InRange(f.Clip("t").SourceOut.Ticks, F(38).Ticks, F(40).Ticks);
    }

    [Fact]
    public void Keyframes_follow_the_timeline_when_speeds_differ()
    {
        // A 2× el origen avanza el doble: un keyframe a 20 cuadros de origen está a 10 de línea de tiempo.
        var f = new TrimFixture().Add("v1", "s", "m2", 0, 100, 0).Add("v2", "t", "m1", 0, 100, 0);
        f.Clip("s").Effects.Add(Effect("fx", BuiltInEffects.Transform, "opacity", (0, 0), (20, 1)));
        TimelineEdits.SetSpeed(f.Ctx, "s", new Rational(2, 1));

        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Transform);

        f.AssertValid();
        Assert.Equal([F(0), F(10)], f.Clip("t").Effects[0].Parameters["opacity"].Keyframes.Select(k => k.Time));
    }

    [Fact]
    public void Titles_take_picture_but_not_speed()
    {
        var f = new TrimFixture().Add("v1", "s", "m1", 0, 50, 0).AddTitle("v2", "t", 0, 25, 0);
        f.Clip("s").Effects.Add(Effect("fx", BuiltInEffects.Transform, "rotation", (0, 45)));
        TimelineEdits.SetSpeed(f.Ctx, "s", new Rational(1, 2));
        ClipAttributes copied = ClipAttributes.Capture(f.Sequence, "s");

        AssertFails(() => ClipAttributeEdits.Paste(f.Ctx, ["t"], copied, ClipAttributeKinds.Speed), "no admiten");
        ClipAttributeEdits.Paste(f.Ctx, ["t"], copied, ClipAttributeKinds.All);

        f.AssertValid();
        Assert.Null(f.Clip("t").Speed);
        Assert.Contains(f.Clip("t").Effects, e => e.Type == BuiltInEffects.Transform);
        Assert.Equal(ClipAttributeKinds.Effects | ClipAttributeKinds.Transform, ClipAttributes.Capture(f.Sequence, "t").Available);
    }

    [Fact]
    public void Volume_without_an_audio_source_is_not_available()
    {
        var f = new TrimFixture().Add("v1", "s", "m1", 0, 50, 0).Add("a1", "t", "m3", 0, 20, 0);

        ClipAttributes copied = ClipAttributes.Capture(f.Sequence, "s");

        Assert.Equal(ClipAttributeKinds.Effects | ClipAttributeKinds.Transform | ClipAttributeKinds.Speed, copied.Available);
        AssertFails(() => ClipAttributeEdits.Paste(f.Ctx, ["t"], copied, ClipAttributeKinds.Volume), "No hay atributos");
        AssertFails(() => ClipAttributeEdits.Paste(f.Ctx, ["t"], copied, ClipAttributeKinds.Transform), "no admiten");
        AssertFails(() => ClipAttributes.Capture(f.Sequence, "falta"), "No existe");
    }

    [Fact]
    public void Pasting_on_a_locked_track_is_rejected_and_leaves_everything_as_it_was()
    {
        var f = new TrimFixture().Add("v1", "s", "m1", 0, 50, 0).Add("v2", "t", "m1", 0, 50, 0).Add("v1", "u", "m1", 0, 50, 50).Lock("v2");
        f.Clip("s").Effects.Add(Effect("fx", BuiltInEffects.Blur));
        string original = ProjectSerializer.Serialize(f.Project);
        var stack = new UndoStack(f.Project);

        Assert.Throws<EditException>(() =>
            stack.Execute(Edits.PasteAttributes("seq", ["u", "t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.All)));

        Assert.Equal(original, ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    public void Pasting_on_several_clips_is_one_step_of_the_history()
    {
        var f = new TrimFixture().Add("v1", "s", "m1", 0, 50, 0).Add("v1", "t", "m1", 0, 50, 50).Add("v2", "u", "m2", 0, 50, 0);
        f.Clip("s").Effects.Add(Effect("fx", BuiltInEffects.Color, "saturation", (10, 0.5)));
        TimelineEdits.SetSpeed(f.Ctx, "s", new Rational(1, 2));
        string original = ProjectSerializer.Serialize(f.Project);
        var stack = new UndoStack(f.Project);

        stack.Execute(Edits.PasteAttributes("seq", ["t", "u"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.All, ids: new SequentialIdGenerator()));

        f.AssertValid();
        Assert.Equal(1, stack.Count);
        Assert.Equal(F(100), f.Clip("u").Duration);
        Assert.Equal(f.Clip("s").End, f.Clip("t").Start);
        Assert.Single(f.Clip("t").Effects);
        Assert.Equal("Pegar atributos", stack.UndoDescription);
        stack.Undo();
        Assert.Equal(original, ProjectSerializer.Serialize(f.Project));
    }
}
