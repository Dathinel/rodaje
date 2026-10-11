using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Trimming;

/// <summary>Las ediciones de borde pasan por <see cref="ClipTimeMap"/>: con velocidad, ningún instante cambia de cuadro.</summary>
public sealed class SpeedTrimTests
{
    public static TheoryData<string> Speeds => [.. SpeedSamples.Names];

    [Theory]
    [MemberData(nameof(Speeds))]
    public void Split_keeps_every_source_instant(string speed)
    {
        var f = new TrimFixture();
        SpeedSamples.Add(f, "v1", "c", "m2", 10, 40, speed);
        Dictionary<Time, Time> before = SpeedSamples.Sources(f.Clip("c"));

        IReadOnlyList<string> ids = TimelineEdits.Split(f.Ctx, "c", F(27) + Time.FromTicks(12_345));

        f.AssertValid();
        Assert.Equal(speed == "1×" ? null : f.Clip("c").Speed!.Rate, f.Clip(ids[0]).Speed?.Rate);
        SpeedSamples.AssertSameSources(f.Track("v1"), before);
    }

    [Theory]
    [MemberData(nameof(Speeds))]
    public void Trims_keep_the_instants_that_remain(string speed)
    {
        var f = new TrimFixture();
        SpeedSamples.Add(f, "v1", "c", "m2", 10, 40, speed);
        Dictionary<Time, Time> before = SpeedSamples.Sources(f.Clip("c"));

        TimelineEdits.Trim(f.Ctx, "c", TrimEdge.In, F(5) + Time.FromTicks(777));
        TimelineEdits.Trim(f.Ctx, "c", TrimEdge.Out, -F(3) - Time.FromTicks(91));
        TimelineEdits.Trim(f.Ctx, "c", TrimEdge.In, -F(2));

        f.AssertValid();
        Assert.Equal(F(13) + Time.FromTicks(777), f.Clip("c").Start);
        Assert.Equal(F(47) - Time.FromTicks(91), f.Clip("c").End);
        SpeedSamples.AssertSameSources(f.Track("v1"), before);
    }

    [Theory]
    [MemberData(nameof(Speeds))]
    public void Ripple_trim_in_keeps_the_start_and_the_source_of_the_remaining_material(string speed)
    {
        var f = new TrimFixture();
        SpeedSamples.Add(f, "v1", "c", "m2", 10, 40, speed);
        f.Add("v1", "n", "m1", 0, 10, 50);
        Clip clip = f.Clip("c");
        Time sourceAt20 = clip.ToSourceTime(F(20));

        TimelineEdits.RippleTrim(f.Ctx, "c", TrimEdge.In, F(10), RippleScope.Track);

        f.AssertValid();
        Assert.Equal(F(10), clip.Start);
        Assert.Equal(F(30), clip.Duration);
        Assert.Equal(sourceAt20, clip.ToSourceTime(F(10)));
        Assert.Equal(F(40), f.Clip("n").Start);
    }

    [Theory]
    [MemberData(nameof(Speeds))]
    public void Roll_and_slide_keep_the_sources_of_both_sides(string speed)
    {
        var f = new TrimFixture();
        SpeedSamples.Add(f, "v1", "a", "m2", 0, 30, speed);
        SpeedSamples.Add(f, "v1", "b", "m2", 30, 30, speed);
        SpeedSamples.Add(f, "v1", "c", "m2", 60, 30, speed);
        var before = new Dictionary<Time, Time>();
        foreach (Clip c in f.Track("v1").Clips)
        {
            foreach ((Time t, Time s) in SpeedSamples.Sources(c))
            {
                before[t] = s;
            }
        }

        TimelineEdits.Roll(f.Ctx, "a", -F(4));
        TimelineEdits.Slide(f.Ctx, "b", F(2));

        f.AssertValid();
        Assert.Equal(F(28), f.Clip("a").End);
        Assert.Equal(F(28), f.Clip("b").Start);
        Assert.Equal(F(62), f.Clip("c").Start);
        SpeedSamples.AssertSameSources(f.Track("v1"), before, skip: c => c.Id == "b");
    }

    [Theory]
    [MemberData(nameof(Speeds))]
    public void Slip_moves_all_the_material_by_the_same_source_delta(string speed)
    {
        var f = new TrimFixture();
        SpeedSamples.Add(f, "v1", "c", "m2", 10, 40, speed);
        Clip clip = f.Clip("c");
        Dictionary<Time, Time> before = SpeedSamples.Sources(clip);

        TimelineEdits.Slip(f.Ctx, "c", F(3));

        f.AssertValid();
        foreach ((Time t, Time s) in before)
        {
            Assert.Equal(s + F(3), clip.ToSourceTime(t));
        }
    }

    [Theory]
    [MemberData(nameof(Speeds))]
    public void Overwrite_in_the_middle_keeps_both_sides(string speed)
    {
        var f = new TrimFixture();
        SpeedSamples.Add(f, "v1", "c", "m2", 10, 40, speed);
        Dictionary<Time, Time> before = SpeedSamples.Sources(f.Clip("c"));

        TimelineEdits.InsertMedia(f.Ctx, "m1", F(0), F(5), "v1", F(20) + Time.FromTicks(3), PlacementMode.Overwrite);

        f.AssertValid();
        Assert.Equal(3, f.Track("v1").Clips.Count);
        SpeedSamples.AssertSameSources(f.Track("v1"), before, skip: c => c.MediaId == "m1");
    }

    [Fact]
    public void A_trim_past_the_maximum_length_of_a_speed_clip_is_refused()
    {
        var f = new TrimFixture();
        f.Project.Media[1].Duration = Time.FromTicks(RetimeMath.MaxExtent * 2);
        SpeedSamples.Add(f, "v1", "c", "m2", 0, 40, "2×");

        AssertFails(() => TimelineEdits.Trim(f.Ctx, "c", TrimEdge.Out, Time.FromTicks(RetimeMath.MaxExtent)), "largo máximo");
    }

    [Fact]
    public void A_keyframe_of_a_frozen_clip_goes_in_its_virtual_time()
    {
        var f = new TrimFixture();
        SpeedSamples.Add(f, "v1", "c", "m2", 10, 40, "congelado");
        Clip clip = f.Clip("c");
        string fx = EffectEdits.AddEffect(f.Ctx, "c", BuiltInEffects.Transform);
        Time last = clip.ToEffectTime(clip.End - Time.FromTicks(1));

        EffectEdits.SetKeyframe(f.Ctx, "c", fx, BuiltInEffects.TransformParameters.Scale, last, 1.5);

        Assert.True(last > clip.SourceOut);
        AssertFails(() => EffectEdits.SetKeyframe(f.Ctx, "c", fx, BuiltInEffects.TransformParameters.Scale, last + F(1), 1), "dentro del clip");
    }
}

/// <summary>Clips de prueba con velocidad y muestreo de qué origen se ve en cada instante.</summary>
internal static class SpeedSamples
{
    public static readonly string[] Names = ["1×", "2×", "0,37×", "1001/1000", "reversa 3/2", "curva", "congelado"];

    public static ClipSpeed? Speed(string name) => name switch
    {
        "1×" => null,
        "2×" => new ClipSpeed { Rate = new Rational(2, 1) },
        "0,37×" => new ClipSpeed { Rate = new Rational(37, 100) },
        "1001/1000" => new ClipSpeed { Rate = new Rational(1001, 1000) },
        "reversa 3/2" => new ClipSpeed { Rate = new Rational(3, 2), Reverse = true },
        "curva" => new ClipSpeed
        {
            Curve = new SpeedCurve { Span = F(40), Points = [new(0, 50), new(4000, 300), new(10_000, 80)] },
        },
        "congelado" => new ClipSpeed { Freeze = true },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>Clip de <paramref name="frames"/> cuadros en la pista; su origen empieza en el cuadro 20 (o termina en el 180 en reversa).</summary>
    public static void Add(TrimFixture f, string trackId, string clipId, string mediaId, long startFrame, long frames, string speed, string? link = null)
    {
        ClipSpeed? s = Speed(speed);
        var clip = new Clip { Id = clipId, MediaId = mediaId, Speed = s, LinkId = link };
        Time anchor = s is { Reverse: true } ? F(180) : F(20);
        ClipTimeMap.Create(F(startFrame), F(frames), anchor, s).ApplyTo(clip);
        f.Track(trackId).Clips.Add(clip);
        f.Track(trackId).SortClips();
    }

    /// <summary>Origen visible en unos 300 instantes del clip, incluidos sus bordes.</summary>
    public static Dictionary<Time, Time> Sources(Clip clip)
    {
        var result = new Dictionary<Time, Time>();
        long step = Math.Max(1, clip.Duration.Ticks / 300) + 13;
        for (long tau = 0; tau < clip.Duration.Ticks; tau += step)
        {
            result[clip.Start + Time.FromTicks(tau)] = clip.ToSourceTime(clip.Start + Time.FromTicks(tau));
        }

        result[clip.End - Time.FromTicks(1)] = clip.ToSourceTime(clip.End - Time.FromTicks(1));
        return result;
    }

    /// <summary>Cada instante que sigue cubierto por un clip muestra el mismo origen que antes.</summary>
    public static void AssertSameSources(Track track, Dictionary<Time, Time> before, Func<Clip, bool>? skip = null)
    {
        int checkedCount = 0;
        foreach ((Time t, Time s) in before)
        {
            Clip? clip = track.Clips.Find(c => c.Start <= t && t < c.End);
            if (clip is null || (skip is not null && skip(clip)))
            {
                continue;
            }

            Assert.True(s == clip.ToSourceTime(t), $"{t} en «{clip.Id}»: antes {s}, ahora {clip.ToSourceTime(t)}");
            checkedCount++;
        }

        Assert.True(checkedCount > 0);
    }
}
