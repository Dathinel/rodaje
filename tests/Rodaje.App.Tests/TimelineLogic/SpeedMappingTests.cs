using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>Keyframes, envolvente, miniaturas y onda de la línea de tiempo con el mapeo de velocidad.</summary>
public class SpeedMappingTests
{
    private static readonly Time Tick = Time.FromTicks(1);

    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static Clip Retimed(Time start, Time sourceIn, Time sourceOut, ClipSpeed speed, params Keyframe[] gain)
    {
        var clip = new Clip { Id = "c", MediaId = "m", Start = start, SourceIn = sourceIn, SourceOut = sourceOut, Speed = speed };
        if (gain.Length > 0)
        {
            clip.Effects.Add(new EffectInstance
            {
                Id = "vol",
                Type = ClipDecorations.VolumeEffect,
                Parameters = new() { [ClipDecorations.GainParameter] = new Parameter { Keyframes = [.. gain] } },
            });
        }

        if (!speed.Freeze)
        {
            ClipTimeMap.Fit(start, sourceIn, sourceOut, speed).ApplyTo(clip);
        }

        return clip;
    }

    [Fact]
    public void A2x_LosKeyframesYLaEnvolventeCaenALaMitad()
    {
        Clip clip = Retimed(S(10), S(0), S(10), new ClipSpeed { Rate = new Rational(2, 1) }, new Keyframe(S(2), -12), new Keyframe(S(6), 0), new Keyframe(S(10), 3));
        Assert.Equal(S(5), clip.Duration);

        Assert.Equal([S(11), S(13), S(15)], ClipDecorations.KeyframeTimes(clip));
        Assert.Equal([(S(11), -12.0), (S(13), 0.0), (S(15), 3.0)], ClipDecorations.GainPoints(clip));
        Assert.Equal(-6, ClipDecorations.GainDbAt(clip, S(12)), 6);
    }

    [Fact]
    public void EnReversa_LosPuntosVanAlRevesEnLaLineaDeTiempo()
    {
        Clip clip = Retimed(S(0), S(0), S(10), new ClipSpeed { Reverse = true }, new Keyframe(S(2), -12), new Keyframe(S(6), 0));

        List<(Time Time, double GainDb)> points = ClipDecorations.GainPoints(clip);
        Assert.Equal(2, points.Count);
        Assert.InRange(points[0].Time.Ticks, (S(4) - Tick).Ticks, S(4).Ticks);
        Assert.Equal(0, points[0].GainDb);
        Assert.InRange(points[1].Time.Ticks, (S(8) - Tick).Ticks, S(8).Ticks);
        Assert.Equal(-12, points[1].GainDb);
        Assert.Equal(-6, ClipDecorations.GainDbAt(clip, S(6)), 3);
    }

    [Fact]
    public void EnUnCongelado_LosKeyframesUsanElTiempoVirtual()
    {
        var speed = new ClipSpeed { Freeze = true };
        Clip clip = Retimed(S(2), S(5), S(5) + Tick, speed, new Keyframe(S(6), -12), new Keyframe(S(9), 0));
        clip.Duration = S(3);

        ClipTimeMap map = clip.TimeMap;
        Assert.Equal((S(5), S(8)), ClipMapping.EffectRange(map));
        Assert.Equal([S(3)], ClipDecorations.KeyframeTimes(clip));
        Assert.Null(ClipMapping.TimelineOfEffect(map, S(9)));
        Assert.Equal(S(2), ClipMapping.SourceSpacing(map, S(2)));
    }

    [Fact]
    public void LaTiraYLaOndaSiguenAlMaterial()
    {
        Clip fast = Retimed(S(0), S(0), S(8), new ClipSpeed { Rate = new Rational(4, 1) });
        ClipTimeMap map = fast.TimeMap;
        Assert.Equal(S(4), ClipMapping.SourceSpacing(map, S(1)));
        Assert.Equal(S(1), ClipMapping.SourceSpacing(new Clip { SourceOut = S(8) }.TimeMap, S(1)));

        (Time low, Time high) = ClipMapping.VisibleSource(map, S(0), S(2));
        Assert.Equal(S(0), low);
        Assert.InRange(high.Ticks, (S(8) - S(0.001)).Ticks, S(8).Ticks);
        Assert.Equal(0, ClipMapping.BucketOf(map, S(0), low, high, 11));
        Assert.Equal(5, ClipMapping.BucketOf(map, S(1), low, high, 11));

        Clip back = Retimed(S(0), S(0), S(8), new ClipSpeed { Reverse = true });
        ClipTimeMap reverse = back.TimeMap;
        (low, high) = ClipMapping.VisibleSource(reverse, S(0), S(8));
        Assert.Equal(10, ClipMapping.BucketOf(reverse, S(0), low, high, 11));
        Assert.Equal(0, ClipMapping.BucketOf(reverse, S(8) - Tick, low, high, 11));
        Assert.Equal(0, ClipMapping.BucketOf(reverse, S(1), low, high, 1));
    }

    [Fact]
    public void LaEnvolventeSeEditaA2xYSeQuitaPorSuLugar() => UiThread.Run(async () =>
    {
        var prober = new FakeProber().Audio("m.mp3", 30);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.ImportMediaAsync([Path.GetFullPath("m.mp3")]);
        Track audio = session.Sequence.AudioTracks[0];
        string media = session.Project.Media[0].Id;
        Assert.True(session.Execute(Rodaje.Core.Commands.Edits.Overwrite(session.SequenceId, media, S(1), S(21), audio.Id, S(0))));
        Clip clip = audio.Clips[0];
        clip.Speed = new ClipSpeed { Rate = new Rational(2, 1) };
        ClipTimeMap.Fit(clip.Start, clip.SourceIn, clip.SourceOut, clip.Speed).ApplyTo(clip);

        Assert.True(session.Execute(EnvelopeEdits.SetPoint(session.SequenceId, clip.Id, null, S(2) + Tick, -6)));
        Keyframe key = Assert.Single(clip.Effects[0].Parameters[BuiltInEffects.AudioParameters.GainDb].Keyframes);
        Assert.Equal(S(5) + Tick + Tick, key.Time);
        Time drawn = Assert.Single(ClipDecorations.GainPoints(clip)).Time;
        Assert.Equal(S(2) + Tick, drawn);

        Assert.True(session.Execute(EnvelopeEdits.SetPoint(session.SequenceId, clip.Id, drawn, S(30), 0)));
        Assert.Equal(S(21), Assert.Single(clip.Effects[0].Parameters[BuiltInEffects.AudioParameters.GainDb].Keyframes).Time);
        Assert.True(session.Execute(EnvelopeEdits.RemovePoint(session.SequenceId, clip.Id, ClipDecorations.GainPoints(clip)[0].Time)));
        Assert.False(ClipDecorations.HasAnimatedGain(clip));
    });

    [Fact]
    public void ElMargenDeUnBordeSeMideConLaVelocidad()
    {
        Clip fast = Retimed(S(0), S(2), S(12), new ClipSpeed { Rate = new Rational(2, 1) });
        Assert.InRange(ClipDrag.Room(fast, Rodaje.Core.Editing.TrimEdge.In, S(20)).Ticks, S(1).Ticks - 1, S(1).Ticks);
        Assert.InRange(ClipDrag.Room(fast, Rodaje.Core.Editing.TrimEdge.Out, S(20)).Ticks, S(4).Ticks - 1, S(4).Ticks);

        var plain = new Clip { SourceIn = S(2), SourceOut = S(12) };
        Assert.Equal(S(2), ClipDrag.Room(plain, Rodaje.Core.Editing.TrimEdge.In, S(20)));
        Assert.Equal(S(8), ClipDrag.Room(plain, Rodaje.Core.Editing.TrimEdge.Out, S(20)));

        Clip frozen = Retimed(S(0), S(5), S(5) + Tick, new ClipSpeed { Freeze = true });
        frozen.Duration = S(3);
        Assert.True(ClipDrag.Room(frozen, Rodaje.Core.Editing.TrimEdge.Out, S(6)) > S(3600));
    }

    [Theory]
    [InlineData(2, 1, false, "2,0×")]
    [InlineData(1, 4, false, "0,25×")]
    [InlineData(137, 100, false, "1,37×")]
    [InlineData(100, 1, false, "100,0×")]
    [InlineData(1, 2, true, "◀ 0,5×")]
    [InlineData(1, 1, true, "◀")]
    public void ElIndicadorDiceLaVelocidad(long num, long den, bool reverse, string expected) =>
        Assert.Equal(expected, SpeedBadge.Text(new Clip { Speed = new ClipSpeed { Rate = new Rational(num, den), Reverse = reverse } }));

    [Fact]
    public void ElIndicadorCallaA1xYAvisaDelCongelado()
    {
        Assert.Null(SpeedBadge.Text(new Clip()));
        Assert.Null(SpeedBadge.Text(new Clip { Speed = new ClipSpeed() }));
        Assert.Equal("❚❚ Congelado", SpeedBadge.Text(new Clip { Speed = new ClipSpeed { Freeze = true } }));
        Assert.Equal("Curva", SpeedBadge.Text(new Clip { Speed = new ClipSpeed { Curve = new SpeedCurve() } }));
    }
}
