using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete la sección «Velocidad y reversa» de <c>docs/guia/edicion.md</c> y depende del núcleo. Cada prueba cita
/// la frase. El deslizador, sus imanes y el texto de la velocidad son de la interfaz.
/// </summary>
public sealed class GuideSpeedContractTests
{
    private static SpeedCurve Curve(params SpeedPoint[] points) => new() { Span = Time.FromSeconds(4), Points = [.. points] };

    private static SpeedPoint[] Flat(int count) =>
        [.. Enumerable.Range(0, count).Select(k => new SpeedPoint(k * SpeedCurve.AtScale / (count - 1), 100))];

    // «Deslizador de 0,1× a 100×»: los dos extremos se aceptan.
    [Theory]
    [InlineData(1, 10)]
    [InlineData(100, 1)]
    public void Los_extremos_del_deslizador_son_velocidades_validas(long num, long den)
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 100, 0);

        TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(num, den));

        f.AssertValid();
        Assert.Equal(new Rational(num, den), f.Clip("c").Speed!.Rate);
    }

    // «Deslizador de 0,1× a 100×»: fuera de ese rango se rechaza sin tocar el clip.
    [Theory]
    [InlineData(9, 100)]
    [InlineData(101, 1)]
    public void Fuera_de_0_1_a_100_la_velocidad_se_rechaza(long num, long den)
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 100, 0);

        Assert.Throws<EditException>(() => TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(num, den)));

        Assert.Null(f.Clip("c").Speed);
        Assert.Equal(F(100), f.Clip("c").Duration);
    }

    // «**Mantener tono**, activo de fábrica»
    [Fact]
    public void Mantener_tono_esta_activo_al_cambiar_la_velocidad()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 100, 0, link: "L").Add("a1", "a", "m2", 0, 100, 0, link: "L");

        TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(2, 1));

        Assert.True(new ClipSpeed().PreservePitch);
        Assert.True(f.Clip("c").Speed!.PreservePitch);
        Assert.True(f.Clip("a").Speed!.PreservePitch);
    }

    // «**Congelar** (Mayús+R) mete 3 s del cuadro del cursor.»
    [Fact]
    public void Congelar_mete_3_s_del_cuadro_del_cursor()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 50, 0).Add("v1", "d", "m1", 0, 10, 50);
        Time shown = f.Clip("c").TimeMap.Source(F(20));

        string id = TimelineEdits.FreezeFrame(f.Ctx, "c", F(20));

        f.AssertValid();
        Clip frozen = f.Clip(id);
        Assert.Equal(F(20), frozen.Start);
        Assert.Equal(Time.FromSeconds(3), frozen.Duration);
        for (Time t = frozen.Start; t < frozen.End; t += F(1))
        {
            Assert.Equal(shown, frozen.TimeMap.Source(t));
        }

        // Lo que seguía se corre los 3 s: se mete, no se pisa.
        Assert.Equal(F(50) + Time.FromSeconds(3), f.Clip("d").Start);
        Assert.Equal(frozen.End, f.Track("v1").Clips.Single(c => c.Id != "c" && c.Id != "d" && c.Id != id).Start);
    }

    // «**Invertir** reproduce al revés, cuadro exacto»: a 1× y a cámara lenta, los mismos cuadros en orden inverso.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    public void Invertir_recorre_los_mismos_cuadros_al_reves(long num, long den)
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 10, 70, 0);
        TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(num, den));
        Clip clip = f.Clip("c");
        Time duration = clip.Duration;
        long frames = duration.Ticks / F(1).Ticks;
        Time[] forward = [.. Enumerable.Range(0, (int)frames).Select(k => clip.TimeMap.Source(clip.Start + F(k)))];

        TimelineEdits.SetReverse(f.Ctx, "c", true);

        f.AssertValid();
        clip = f.Clip("c");
        Assert.Equal(duration, clip.Duration);
        for (int k = 0; k < frames; k++)
        {
            Time shown = clip.TimeMap.Source(clip.Start + F(k));
            Time mirrored = forward[frames - 1 - k];
            Assert.True(
                (shown - mirrored).Ticks >= 0 && shown - mirrored < F(1),
                $"Cuadro {k}: invertido muestra {shown} y debería caer en el cuadro de {mirrored}.");
        }
    }

    // «**Curva:** Ninguna, Personalizada, Montaje, Héroe, Bala, Entrada rápida y Salida rápida.»
    [Fact]
    public void Los_preajustes_de_curva_son_los_de_la_guia_en_su_orden()
    {
        Assert.Equal(
            ["Montaje", "Héroe", "Bala", "Entrada rápida", "Salida rápida"],
            SpeedPresets.All.Select(SpeedPresets.Name));
    }

    // «Ninguna»: quitar la curva deja el clip a 1×.
    [Fact]
    public void Curva_ninguna_vuelve_a_1x()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 100, 0);
        TimelineEdits.SetCurve(f.Ctx, "c", Curve(new(0, 50), new(10_000, 400)));

        TimelineEdits.SetCurve(f.Ctx, "c", null);

        f.AssertValid();
        Clip clip = f.Clip("c");
        Assert.True(clip.Speed is null || clip.Speed.IsIdentity);
        Assert.Equal(F(100), clip.Duration);
    }

    // «En Personalizada, arrastra los puntos (doble clic añade o quita, de 2 a 64)»
    [Theory]
    [InlineData(2)]
    [InlineData(64)]
    public void Una_curva_de_2_a_64_puntos_es_valida(int count)
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 100, 0);

        TimelineEdits.SetCurve(f.Ctx, "c", Curve(Flat(count)));

        f.AssertValid();
        Assert.Equal(count, f.Clip("c").Speed!.Curve!.Points.Count);
    }

    // «de 2 a 64»: con uno o con 65 la curva no vale.
    [Theory]
    [InlineData(1)]
    [InlineData(65)]
    public void Una_curva_fuera_de_2_a_64_puntos_no_vale(int count)
    {
        SpeedPoint[] points = count == 1 ? [new(0, 100)] : Flat(count);

        Assert.NotNull(RetimeMath.CurveProblem(Curve(points)));
    }

    // «altura logarítmica de 0,1× a 10×»: los dos extremos caben en un punto.
    [Fact]
    public void Los_puntos_van_de_0_1x_a_10x()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 200, 0);

        TimelineEdits.SetCurve(f.Ctx, "c", Curve(new(0, 10), new(5000, 1000), new(10_000, 10)));

        f.AssertValid();
        List<SpeedPoint> points = f.Clip("c").Speed!.Curve!.Points;
        Assert.All(points, p => Assert.InRange(p.Hundredths, 10, 1000));
        Assert.Equal(10, points.Min(p => p.Hundredths));
        Assert.Equal(1000, points.Max(p => p.Hundredths));
    }

    // «altura logarítmica de 0,1× a 10×»: por debajo o por encima, la curva no vale.
    [Theory]
    [InlineData(9)]
    [InlineData(1001)]
    public void Un_punto_fuera_de_0_1x_a_10x_no_vale(int hundredths)
    {
        Assert.NotNull(RetimeMath.CurveProblem(Curve(new(0, 100), new(10_000, hundredths))));
    }

    // «Con velocidad distinta de 1×, los keyframes y la envolvente del clip siguen al material, no a la línea de tiempo.»
    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void Los_keyframes_y_la_envolvente_siguen_al_material(long num, long den)
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 100, 0, link: "L").Add("a1", "a", "m2", 0, 100, 0, link: "L");
        foreach (string id in (string[])["c", "a"])
        {
            f.Clip(id).Effects.Add(new EffectInstance
            {
                Id = "fx-" + id,
                Type = BuiltInEffects.Audio,
                Parameters =
                {
                    [BuiltInEffects.AudioParameters.GainDb] = new Parameter
                    {
                        Keyframes = [new Keyframe(F(0), 0), new Keyframe(F(40), -40)],
                    },
                },
            });
        }

        TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(num, den));

        f.AssertValid();
        foreach (string id in (string[])["c", "a"])
        {
            Clip clip = f.Clip(id);
            Parameter gain = clip.Effects[0].Parameters[BuiltInEffects.AudioParameters.GainDb];
            Assert.Equal([F(0), F(40)], gain.Keyframes.Select(k => k.Time));

            // El keyframe del material en 40 cuadros llega cuando la pista lo muestra: 20 cuadros a 2×, 80 a 0,5×.
            Time reached = clip.Start + F(40 * den / num);
            Assert.Equal(F(40), clip.ToEffectTime(reached));
            Assert.Equal(-40, gain.Evaluate(clip.ToEffectTime(reached)), 9);
            Assert.Equal(-20, gain.Evaluate(clip.ToEffectTime(clip.Start + F(20 * den / num))), 9);
        }
    }
}
