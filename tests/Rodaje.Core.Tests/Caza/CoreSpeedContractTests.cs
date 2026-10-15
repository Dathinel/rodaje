using System.Numerics;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete la sección «Velocidad» de <c>docs/arquitectura/nucleo.md</c>. Cada prueba cita la frase. La tabla de
/// preajustes ya la comprueba <see cref="SpeedPresetsCazaTests"/>.
/// </summary>
public sealed class CoreSpeedContractTests
{
    private static readonly SpeedCurve Peak = new()
    {
        Span = Time.FromSeconds(4),
        Points = [new(0, 100), new(5000, 800), new(10_000, 100)],
    };

    private static Clip SpeedClip(ClipSpeed speed, Time duration) => new()
    {
        Id = "c",
        MediaId = "m",
        Start = F(10),
        SourceIn = F(5),
        SourceOut = F(5) + Time.FromSeconds(100),
        Duration = duration,
        Speed = speed,
    };

    private static ClipTimeMap MapOf(ClipSpeed speed, Time duration)
    {
        Clip clip = SpeedClip(speed, duration);
        ClipTimeMap.Create(clip.Start, duration, clip.SourceIn, speed).ApplyTo(clip);
        return clip.TimeMap;
    }

    // «La velocidad es `Clip.Speed` (`ClipSpeed`) y todo tiempo pasa por `ClipTimeMap`»
    [Fact]
    public void Los_tiempos_de_un_clip_con_velocidad_salen_de_su_ClipTimeMap()
    {
        var speed = new ClipSpeed { Rate = new Rational(3, 2) };
        Clip clip = SpeedClip(speed, F(40));
        ClipTimeMap.Create(clip.Start, F(40), clip.SourceIn, speed).ApplyTo(clip);

        ClipTimeMap map = ClipTimeMap.Of(clip);
        Assert.Equal(map, clip.TimeMap);
        Assert.Equal(map.SourceOut, clip.SourceOut);
        Assert.Equal(F(5) + F(60), clip.SourceOut);
        for (long frame = 10; frame < 50; frame++)
        {
            Assert.Equal(map.ExtendedSource(F(frame)), clip.ToSourceTime(F(frame)));
            Assert.Equal(map.EffectTime(F(frame)), clip.ToEffectTime(F(frame)));
        }
    }

    // «exacto en `Int128` con `RetimeMath`»: constante, I(u) = u·num/den sin redondeo intermedio.
    [Fact]
    public void RetimeMath_constante_es_exacta_en_todo_su_rango()
    {
        RetimeMath math = RetimeMath.Constant(new Rational(999_983, 1_048_573));
        long[] samples = [1, 7, 1_048_573, RetimeMath.MaxExtent - 1, RetimeMath.MaxArgument, -RetimeMath.MaxArgument, -3];
        foreach (long u in samples)
        {
            BigInteger product = (BigInteger)u * 999_983;
            BigInteger floor = BigInteger.DivRem(product, 1_048_573, out BigInteger rem);
            if (rem < 0)
            {
                floor -= 1;
            }

            Assert.Equal((long)floor, math.Floor(u));
            Assert.Equal((long)(rem == 0 ? floor : floor + 1), math.Ceiling(u));
        }
    }

    // «exacto en `Int128` con `RetimeMath`»: en una curva, I es exacta en cada nudo (área de los trapecios).
    [Fact]
    public void RetimeMath_curva_es_exacta_en_sus_nudos_con_el_largo_maximo()
    {
        var curve = new SpeedCurve { Span = Time.FromTicks(RetimeMath.MaxExtent), Points = [new(0, 1000), new(3333, 10), new(10_000, 997)] };
        RetimeMath math = RetimeMath.FromCurve(curve);
        BigInteger twoHundredI = 0;
        long previous = 0;
        int previousH = 1000;
        foreach (SpeedPoint point in curve.Points.Skip(1))
        {
            long at = (long)((Int128)point.At * curve.Span.Ticks / SpeedCurve.AtScale);
            twoHundredI += (BigInteger)(at - previous) * (previousH + point.Hundredths);
            BigInteger floor = BigInteger.Divide(twoHundredI, 200);
            Assert.Equal((long)floor, math.Floor(at));
            Assert.Equal((long)(twoHundredI % 200 == 0 ? floor : floor + 1), math.Ceiling(at));
            Assert.True(math.Inverse(math.Floor(at)) <= at);
            Assert.True(math.Inverse(math.Floor(at) + 1) > at);
            (previous, previousH) = (at, point.Hundredths);
        }
    }

    // «`RateRange` (menor y mayor velocidad de un tramo; en una curva, en sus nudos)»
    [Fact]
    public void RateRange_da_la_menor_y_la_mayor_velocidad_y_cuenta_los_nudos()
    {
        ClipTimeMap map = MapOf(new ClipSpeed { Curve = Peak.Clone() }, Time.FromSeconds(4));

        (double min, double max) = map.RateRange(map.Start, map.End);
        Assert.Equal(1, min, 3);
        Assert.Equal(8, max, 9);

        // Sin el nudo dentro del tramo, el máximo es el de un borde y queda por debajo de 8.
        Time knot = map.Start + Time.FromSeconds(2);
        (double lowMin, double lowMax) = map.RateRange(map.Start, knot - Time.FromSeconds(1));
        Assert.InRange(lowMax, 4.4, 4.6);
        Assert.Equal(1, lowMin, 3);

        (_, double around) = map.RateRange(knot - F(1), knot + F(1));
        Assert.Equal(8, around, 9);

        ClipTimeMap constant = MapOf(new ClipSpeed { Rate = new Rational(5, 2) }, F(40));
        Assert.Equal((2.5, 2.5), constant.RateRange(constant.Start, constant.End));
    }

    // «`SameCourse` (dos mapeos muestran el mismo origen donde coinciden, como las mitades de un clip dividido)»
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameCourse_reconoce_las_mitades_de_un_clip_dividido(bool reverse)
    {
        var speed = new ClipSpeed { Curve = Peak.Clone(), Reverse = reverse };
        ClipTimeMap map = MapOf(speed, Time.FromSeconds(4));
        (ClipTimeMap left, ClipTimeMap right) = map.SplitAt(map.Start + Time.FromTicks(987_654_321));

        Assert.True(left.SameCourse(right));
        Assert.True(right.SameCourse(left));
        Assert.True(map.SameCourse(right));
        for (Time t = right.Start; t < right.End; t += F(3))
        {
            Assert.Equal(map.Source(t), right.Source(t));
        }

        for (Time t = left.Start; t < left.End; t += F(3))
        {
            Assert.Equal(map.Source(t), left.Source(t));
        }

        Assert.False(left.SameCourse(right.Slip(Time.FromTicks(1))));
        Assert.False(left.SameCourse(right.MoveTo(right.Start + F(1))));
    }

    // «`Join` (une dos mitades)»
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Join_vuelve_a_unir_las_dos_mitades(bool reverse)
    {
        ClipTimeMap map = MapOf(new ClipSpeed { Curve = Peak.Clone(), Reverse = reverse }, Time.FromSeconds(4));
        (ClipTimeMap left, ClipTimeMap right) = map.SplitAt(map.Start + F(37));

        Assert.Equal(map, left.Join(right));
        Assert.Null(right.Join(left));
        Assert.Null(left.Join(right.Slip(Time.FromTicks(1))));
    }

    // «Ediciones (en `TimelineEdits.Speed`, fábricas en `Edits.Speed`): `SetSpeed`, `SetCurve`, `ApplySpeedPreset`,
    // `RateStretch` (Alt + borde), `FreezeFrame`, `SetReverse` y `SetPreservePitch`.»
    [Fact]
    public void Las_ediciones_de_velocidad_tienen_su_fabrica()
    {
        string[] names = ["SetSpeed", "SetCurve", "ApplySpeedPreset", "RateStretch", "FreezeFrame", "SetReverse", "SetPreservePitch"];
        foreach (string name in names)
        {
            Assert.Contains(typeof(Edits).GetMethods(), m => m.Name == name && m.ReturnType == typeof(SequenceEditCommand));
        }

        // ApplySpeedPreset no tiene edición propia: es SetCurve con la forma (lo comprueba otra prueba).
        foreach (string name in names.Where(n => n != "ApplySpeedPreset"))
        {
            Assert.Contains(typeof(TimelineEdits).GetMethods(), m => m.Name == name && m.IsStatic);
        }
    }

    public static TheoryData<string> Shortening => ["SetSpeed", "SetCurve", "ApplySpeedPreset", "RateStretch"];

    public static TheoryData<string> Growing => ["SetSpeed", "SetCurve", "RateStretch"];

    private static void Shorten(TrimFixture f, string kind, RippleScope scope)
    {
        switch (kind)
        {
            case "SetSpeed":
                TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(2, 1), scope);
                break;
            case "SetCurve":
                TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.FastIn), scope);
                break;
            case "ApplySpeedPreset":
                new UndoStack(f.Project).Execute(Edits.ApplySpeedPreset("seq", "c", SpeedPreset.Montage, scope, new SequentialIdGenerator()));
                break;
            default:
                TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, -F(10), scope);
                break;
        }
    }

    private static void Grow(TrimFixture f, string kind, RippleScope scope)
    {
        switch (kind)
        {
            case "SetSpeed":
                TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(1, 2), scope);
                break;
            case "SetCurve":
                TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.Hero), scope);
                break;
            default:
                TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, F(10), scope);
                break;
        }
    }

    // «Si el clip se acorta, lo que sigue se une»
    [Theory]
    [MemberData(nameof(Shortening))]
    public void Si_el_clip_se_acorta_lo_que_sigue_se_une(string kind)
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 40, 0).Add("v1", "n", "m1", 0, 10, 40).Add("v1", "far", "m1", 20, 30, 70);

        Shorten(f, kind, RippleScope.Track);

        f.AssertValid();
        Clip clip = f.Clip("c");
        Assert.True(clip.End < F(40), $"{kind}: {clip.End}");
        Assert.Equal(clip.End, f.Clip("n").Start);
        Assert.Equal(f.Clip("n").Start + F(30), f.Clip("far").Start);
        Assert.Equal(F(0), clip.SourceIn);
        Assert.Equal(F(40), clip.SourceOut);
    }

    // «si crece, solo empuja lo que pisa»
    [Theory]
    [MemberData(nameof(Growing))]
    public void Si_el_clip_crece_solo_empuja_lo_que_pisa(string kind)
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 40, 0).Add("v1", "n", "m1", 0, 10, 44).Add("v1", "far", "m1", 20, 30, 100);

        Grow(f, kind, RippleScope.Track);

        f.AssertValid();
        Clip clip = f.Clip("c");
        Assert.True(clip.End > F(44), $"{kind}: {clip.End}");
        Assert.Equal(clip.End, f.Clip("n").Start);
        // Lo que sigue se corre lo que pisa, no lo que crece: el hueco de 4 cuadros se consume primero.
        Assert.Equal(F(100) + (clip.End - F(44)), f.Clip("far").Start);

        var g = new TrimFixture().Add("v1", "c", "m2", 0, 40, 0).Add("v1", "n", "m1", 0, 10, 400);
        Grow(g, kind, RippleScope.Track);
        Assert.Equal(F(400), g.Clip("n").Start);
    }

    // «respetan el `RippleScope` de la sesión»
    [Theory]
    [MemberData(nameof(Shortening))]
    public void Respetan_el_RippleScope_de_la_sesion(string kind)
    {
        TrimFixture Build() => new TrimFixture().Add("v1", "c", "m2", 0, 40, 0).Add("v1", "n", "m1", 0, 10, 40).Add("v2", "other", "m1", 0, 10, 60);

        TrimFixture track = Build();
        Shorten(track, kind, RippleScope.Track);
        Assert.Equal(F(60), track.Clip("other").Start);

        TrimFixture all = Build();
        Shorten(all, kind, RippleScope.AllTracks);
        all.AssertValid();
        Time delta = all.Clip("c").End - F(40);
        Assert.True(delta < Time.Zero);
        Assert.Equal(F(60) + delta, all.Clip("other").Start);
    }

    // «respetan el `RippleScope` de la sesión»: también al congelar un cuadro.
    [Fact]
    public void FreezeFrame_respeta_el_RippleScope_de_la_sesion()
    {
        TrimFixture Build() => new TrimFixture().Add("v1", "c", "m2", 0, 40, 0).Add("v2", "other", "m1", 0, 10, 60);

        TrimFixture track = Build();
        TimelineEdits.FreezeFrame(track.Ctx, "c", F(20), F(10), RippleScope.Track);
        Assert.Equal(F(60), track.Clip("other").Start);

        TrimFixture all = Build();
        TimelineEdits.FreezeFrame(all.Ctx, "c", F(20), F(10), RippleScope.AllTracks);
        all.AssertValid();
        Assert.Equal(F(70), all.Clip("other").Start);
    }

    // «`RateStretch` sobre una curva escala `Span` y `Offset` por `D′/D` y divide cada punto por lo mismo (velocidad media
    // nueva), redondeado a centésimas»
    [Fact]
    public void RateStretch_sobre_una_curva_escala_Span_y_Offset_y_redondea_cada_punto()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 10, 110, 0);
        TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.Hero));
        string right = TimelineEdits.Split(f.Ctx, "c", F(30))[0];
        Clip clip = f.Clip(right);
        Assert.NotEqual(Time.Zero, clip.Speed!.Offset);
        Time d = clip.Duration;
        Time offset = clip.Speed.Offset;
        SpeedCurve before = clip.Speed.Curve!.Clone();
        (Time sourceIn, Time sourceOut) = (clip.SourceIn, clip.SourceOut);

        // D′ = 1,5·D: 100 → 67 (no 66) y 20 → 13.
        TimelineEdits.RateStretch(f.Ctx, right, TrimEdge.Out, Time.FromTicks(d.Ticks / 2));

        f.AssertValid();
        Time d2 = clip.Duration;
        Assert.Equal(d.Ticks + (d.Ticks / 2), d2.Ticks);
        Assert.Equal([100, 100, 20, 20, 100, 100], before.Points.Select(p => p.Hundredths));
        Assert.Equal([67, 67, 13, 13, 67, 67], clip.Speed!.Curve!.Points.Select(p => p.Hundredths));
        Assert.Equal(before.Points.Select(p => p.At), clip.Speed.Curve.Points.Select(p => p.At));
        Assert.Equal((double)offset.Ticks * d2.Ticks / d.Ticks, clip.Speed.Offset.Ticks, 0.5);
        // El Span se retoca unos ticks para recorrer el mismo material con las velocidades redondeadas.
        double scaledSpan = (double)before.Span.Ticks * d2.Ticks / d.Ticks;
        Assert.InRange(clip.Speed.Curve.Span.Ticks, scaledSpan * 0.98, scaledSpan * 1.02);

        // «el material no cambia»
        Assert.Equal(sourceIn, clip.SourceIn);
        Assert.Equal(sourceOut, clip.SourceOut);
    }

    // «acotado para que ningún punto salga de 0,1×–10×; el material no cambia»
    [Fact]
    public void RateStretch_sobre_una_curva_no_saca_ningun_punto_de_rango_ni_cambia_el_material()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 100, 0);
        TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.FastIn));
        Clip clip = f.Clip("c");
        Time d = clip.Duration;

        TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, -(d - F(1)));

        f.AssertValid();
        Assert.Equal(RetimeMath.MaxHundredths, clip.Speed!.Curve!.Points.Max(p => p.Hundredths));
        Assert.All(clip.Speed.Curve.Points, p => Assert.InRange(p.Hundredths, RetimeMath.MinHundredths, RetimeMath.MaxHundredths));
        Assert.InRange(clip.Duration.Ticks, (d.Ticks * 4 / 10) - F(1).Ticks, (d.Ticks * 4 / 10) + F(1).Ticks);
        Assert.Equal(F(0), clip.SourceIn);
        Assert.Equal(F(100), clip.SourceOut);
    }

    // «`ApplySpeedPreset` es `SetCurve` con la forma, así que la duración sale de la velocidad media y el clip conserva su
    // material.»
    [Theory]
    [InlineData(SpeedPreset.Montage)]
    [InlineData(SpeedPreset.Hero)]
    [InlineData(SpeedPreset.Bullet)]
    [InlineData(SpeedPreset.FastIn)]
    [InlineData(SpeedPreset.FastOut)]
    public void ApplySpeedPreset_es_SetCurve_y_conserva_el_material(SpeedPreset preset)
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 20, 120, 0, link: "L").Add("a1", "a", "m2", 20, 120, 0, link: "L");
        var g = new TrimFixture().Add("v1", "c", "m2", 20, 120, 0, link: "L").Add("a1", "a", "m2", 20, 120, 0, link: "L");

        new UndoStack(f.Project).Execute(Edits.ApplySpeedPreset("seq", "c", preset, ids: new SequentialIdGenerator()));
        TimelineEdits.SetCurve(g.Ctx, "c", SpeedPresets.Curve(preset));

        f.AssertValid();
        Assert.Equal(ProjectSerializer.Serialize(g.Project), ProjectSerializer.Serialize(f.Project));
        foreach (string id in (string[])["c", "a"])
        {
            Clip clip = f.Clip(id);
            Assert.Equal(F(20), clip.SourceIn);
            Assert.Equal(F(120), clip.SourceOut);
            Assert.Equal(SpeedPresets.Match(clip.Speed!.Curve!), preset);
            double expected = F(100).Ticks / SpeedPresets.MeanRate(SpeedPresets.Points(preset));
            Assert.InRange(clip.Duration.Ticks, expected - F(1).Ticks, expected + F(1).Ticks);
        }
    }

    // «`FreezeFrame`»: el clip congelado muestra el cuadro de ese instante.
    [Fact]
    public void FreezeFrame_muestra_el_cuadro_del_instante()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 10, 50, 0);
        TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(2, 1));
        Time shown = f.Clip("c").TimeMap.Source(F(7));

        string id = TimelineEdits.FreezeFrame(f.Ctx, "c", F(7));

        f.AssertValid();
        Clip frozen = f.Clip(id);
        Assert.True(frozen.Speed!.Freeze);
        Assert.Equal(TimelineEdits.DefaultFreezeDuration, frozen.Duration);
        Assert.Equal(shown, frozen.TimeMap.Source(frozen.Start));
        Assert.Equal(shown, frozen.TimeMap.Source(frozen.End - Time.FromTicks(1)));
    }

    // «`SetReverse` y `SetPreservePitch`»: invertir no cambia duración ni material; el tono va a todo el grupo.
    [Fact]
    public void SetReverse_y_SetPreservePitch_cambian_el_grupo_sin_tocar_tiempos()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 10, 50, 0, link: "L").Add("a1", "a", "m2", 10, 50, 0, link: "L");
        TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(3, 2));
        Clip clip = f.Clip("c");
        (Time duration, Time sourceIn, Time sourceOut) = (clip.Duration, clip.SourceIn, clip.SourceOut);
        Time first = clip.TimeMap.Source(clip.Start);

        TimelineEdits.SetReverse(f.Ctx, "c", true);
        TimelineEdits.SetPreservePitch(f.Ctx, "c", false);

        f.AssertValid();
        foreach (string id in (string[])["c", "a"])
        {
            Clip member = f.Clip(id);
            Assert.True(member.Speed!.Reverse);
            Assert.False(member.Speed.PreservePitch);
            Assert.Equal(duration, member.Duration);
            Assert.Equal(sourceIn, member.SourceIn);
            Assert.Equal(sourceOut, member.SourceOut);
        }

        Assert.Equal(sourceIn, first);
        Assert.Equal(sourceOut - Time.FromTicks(1), clip.TimeMap.Source(clip.Start));
    }
}
