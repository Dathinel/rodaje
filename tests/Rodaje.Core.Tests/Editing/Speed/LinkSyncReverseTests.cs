using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Speed;

/// <summary>
/// <see cref="LinkSync.OffsetBetween"/> con reversa o curva: solo reconocía el par idéntico y daba «no comparable» a todo
/// lo demás, así que dos enlazados en reversa corridos 10 cuadros no salían en <see cref="LinkSync.Find"/> y Q/W y el
/// borrado ripple por pista (D-041, D-044) los desfasaban sin rechazo. Ahora la reversa a velocidad constante se mide
/// exacta (origen <c>SourceOut − 1 − p(τ)</c>) y con curva se compara el recorrido de cada uno.
/// </summary>
public sealed class LinkSyncReverseTests
{
    private static Clip Make(string id, long sourceIn, long sourceOut, long start, ClipSpeed speed, long duration) => new()
    {
        Id = id, MediaId = "m", LinkId = "l", Start = Time.FromTicks(start), SourceIn = Time.FromTicks(sourceIn),
        SourceOut = Time.FromTicks(sourceOut), Speed = speed, Duration = Time.FromTicks(duration),
    };

    private static ClipSpeed Reverse(Rational rate, long offset = 0) => new() { Rate = rate, Reverse = true, Offset = Time.FromTicks(offset) };

    /// <summary>Diferencia del origen que muestran los dos en cada instante común (cada 1/50 del tramo).</summary>
    private static HashSet<Time> Differences(Clip reference, Clip clip)
    {
        ClipTimeMap r = ClipTimeMap.Of(reference), c = ClipTimeMap.Of(clip);
        Time from = r.Start > c.Start ? r.Start : c.Start;
        Time to = r.End < c.End ? r.End : c.End;
        var result = new HashSet<Time>();
        for (int i = 0; i < 50; i++)
        {
            Time t = from + Time.FromTicks((to - from).Ticks * i / 50);
            result.Add(c.Source(t) - r.Source(t));
        }

        return result;
    }

    [Fact]
    public void Reversa_a1x_compensada_da_cero()
    {
        // En reversa a 1× se ve SourceOut − 1 − (t − Start): empezar 10 cuadros después con el final 10 cuadros antes
        // muestra lo mismo.
        Clip video = Make("v", 0, F(100).Ticks, 0, Reverse(new Rational(1, 1)), F(100).Ticks);
        Clip audio = Make("a", F(20).Ticks, F(90).Ticks, F(10).Ticks, Reverse(new Rational(1, 1)), F(70).Ticks);

        Assert.Equal([Time.Zero], Differences(video, audio));
        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(video, audio));
        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(audio, video));
    }

    [Fact]
    public void Reversa_a1x_corrida_10_cuadros_da_el_valor_exacto()
    {
        // El mismo final y 10 cuadros más tarde: en cada instante muestra material 10 cuadros más adelante.
        Clip video = Make("v", 0, F(100).Ticks, 0, Reverse(new Rational(1, 1)), F(100).Ticks);
        Clip audio = Make("a", F(10).Ticks, F(100).Ticks, F(10).Ticks, Reverse(new Rational(1, 1)), F(90).Ticks);

        Assert.Equal([F(10)], Differences(video, audio));
        Assert.Equal(F(10), LinkSync.OffsetBetween(video, audio));
        Assert.Equal(-F(10), LinkSync.OffsetBetween(audio, video));

        var sequence = new Sequence { Id = "s" };
        sequence.VideoTracks.Add(new Track { Id = "v1", Clips = { video } });
        sequence.AudioTracks.Add(new Track { Id = "a1", Clips = { audio } });
        SyncOffset sync = Assert.Single(LinkSync.Find(sequence));
        Assert.Equal(("a", "v", F(10)), (sync.ClipId, sync.PartnerId, sync.Offset));
    }

    [Fact]
    public void Reversa_a2x_con_distinto_Offset_se_mide_exacta()
    {
        // A 2× en reversa un recorte de la entrada mueve el Offset; el audio recortado 7 cuadros y corrido 3 en el
        // origen va 3 cuadros por delante (en reversa, «por delante» es material anterior: −3 cuadros).
        Clip video = Make("v", 0, F(200).Ticks, F(10).Ticks, Reverse(new Rational(2, 1)), F(100).Ticks);
        ClipTimeMap trimmed = ClipTimeMap.Of(video).TrimIn(F(17)).Slip(-F(3));
        Clip audio = Make("a", 0, 0, 0, Reverse(new Rational(2, 1)), 1);
        trimmed.ApplyTo(audio);

        Assert.Equal([-F(3)], Differences(video, audio));
        Assert.Equal(-F(3), LinkSync.OffsetBetween(video, audio));
        Assert.Equal(F(3), LinkSync.OffsetBetween(audio, video));
    }

    private static SpeedCurve Ramp() =>
        new() { Span = F(100), Points = [new SpeedPoint(0, 100), new SpeedPoint(SpeedCurve.AtScale / 2, 300), new SpeedPoint(SpeedCurve.AtScale, 100)] };

    [Fact]
    public void Curva_en_la_misma_fase_da_el_desfase_exacto_en_todo_el_tramo()
    {
        foreach (bool reverse in (bool[])[false, true])
        {
            Clip video = Make("v", 0, 0, F(10).Ticks, new ClipSpeed { Curve = Ramp(), Reverse = reverse }, F(100).Ticks);
            ClipTimeMap.Create(video.Start, video.Duration, reverse ? F(400) : F(5), video.Speed).ApplyTo(video);
            // Recortado como lo dejan recortar los dos (misma fase) y deslizado 4 cuadros.
            Clip audio = Make("a", 0, 0, 0, new ClipSpeed { Curve = Ramp(), Reverse = reverse, PreservePitch = false }, 1);
            ClipTimeMap.Of(video).TrimIn(F(23)).TrimOut(F(90)).Slip(F(4)).ApplyTo(audio);

            Assert.Equal([F(4)], Differences(video, audio));
            Assert.Equal(F(4), LinkSync.OffsetBetween(video, audio));
            Assert.Equal(-F(4), LinkSync.OffsetBetween(audio, video));
        }
    }

    [Fact]
    public void Curva_en_otra_fase_no_se_da_por_sincronizada_aunque_empiecen_en_el_mismo_cuadro()
    {
        foreach (bool reverse in (bool[])[false, true])
        {
            Clip video = Make("v", 0, 0, F(10).Ticks, new ClipSpeed { Curve = Ramp(), Reverse = reverse }, F(100).Ticks);
            ClipTimeMap.Create(video.Start, video.Duration, reverse ? F(400) : F(5), video.Speed).ApplyTo(video);
            // El audio corrido 6 cuadros en la línea de tiempo y deslizado para que su primer cuadro coincida: sigue la curva
            // con otra fase, así que se separa del video en cuanto la velocidad cambia.
            ClipTimeMap shifted = ClipTimeMap.Of(video).MoveTo(F(16));
            Clip audio = Make("a", 0, 0, 0, new ClipSpeed { Curve = Ramp(), Reverse = reverse }, 1);
            shifted.Slip(ClipTimeMap.Of(video).Source(F(16)) - shifted.Source(F(16))).ApplyTo(audio);

            HashSet<Time> differences = Differences(video, audio);
            Assert.Contains(Time.Zero, differences);
            Assert.True(differences.Count > 1);
            Time? offset = LinkSync.OffsetBetween(video, audio);
            Assert.NotNull(offset);
            Assert.NotEqual(Time.Zero, offset);
            Assert.Equal(-offset, LinkSync.OffsetBetween(audio, video));
        }
    }

    /// <summary>
    /// Con otra fase se da la mayor diferencia en los instantes en que alguno pasa por un nudo de la curva. Aquí los dos
    /// empiezan antes de la curva (Offset negativo) y terminan después, así que esos seis instantes caen dentro de los dos
    /// clips y se comprueba el valor con el mapeo.
    /// </summary>
    [Fact]
    public void Curva_en_otra_fase_da_la_mayor_diferencia_en_los_nudos()
    {
        foreach (bool reverse in (bool[])[false, true])
        {
            var speed = new ClipSpeed { Curve = Ramp(), Reverse = reverse, Offset = -F(20) };
            Clip video = Make("v", 0, 0, 0, speed, 1);
            ClipTimeMap.Create(F(10), F(160), reverse ? F(800) : F(5), speed).ApplyTo(video);
            Clip audio = Make("a", 0, 0, 0, new ClipSpeed { Curve = Ramp(), Reverse = reverse }, 1);
            ClipTimeMap.Of(video).MoveTo(F(16)).Slip(F(2)).ApplyTo(audio);

            // Nudos en 0, 50 y 100 cuadros de la curva: el video pasa por ellos en 30, 80 y 130; el audio, 6 después.
            ClipTimeMap v = ClipTimeMap.Of(video), a = ClipTimeMap.Of(audio);
            Time expected = Time.Zero;
            foreach (long frame in (long[])[30, 36, 80, 86, 130, 136])
            {
                Time difference = a.Source(F(frame)) - v.Source(F(frame));
                if ((difference < Time.Zero ? -difference : difference) > (expected < Time.Zero ? -expected : expected))
                {
                    expected = difference;
                }
            }

            Assert.NotEqual(Time.Zero, expected);
            Assert.Equal(expected, LinkSync.OffsetBetween(video, audio));
            Assert.Equal(-expected, LinkSync.OffsetBetween(audio, video));
        }
    }

    /// <summary>
    /// Curva de dos nudos (0 y 100 cuadros) de <paramref name="from"/> a <paramref name="to"/> centésimas. El audio va 6
    /// cuadros detrás del video en la línea de tiempo (otra fase) y se desliza para que las diferencias extremas, en el
    /// cuadro 30 (el video pasa por el primer nudo) y en el 136 (el audio pasa por el último), valgan +d y −d.
    /// </summary>
    private static (Clip Video, Clip Audio, Time Early, Time Late) TiedPair(int from, int to, bool reverse)
    {
        SpeedCurve Curve() => new() { Span = F(100), Points = [new SpeedPoint(0, from), new SpeedPoint(SpeedCurve.AtScale, to)] };
        var speed = new ClipSpeed { Curve = Curve(), Reverse = reverse, Offset = -F(20) };
        Clip video = Make("v", 0, 0, 0, speed, 1);
        ClipTimeMap.Create(F(10), F(160), reverse ? F(800) : F(5), speed).ApplyTo(video);
        ClipTimeMap v = ClipTimeMap.Of(video);
        ClipTimeMap moved = v.MoveTo(F(16));
        Time early = moved.Source(F(30)) - v.Source(F(30)), late = moved.Source(F(136)) - v.Source(F(136));
        Clip audio = Make("a", 0, 0, 0, new ClipSpeed { Curve = Curve(), Reverse = reverse }, 1);
        moved.Slip(Time.FromTicks(-(early + late).Ticks / 2)).ApplyTo(audio);

        ClipTimeMap a = ClipTimeMap.Of(audio);
        return (video, audio, a.Source(F(30)) - v.Source(F(30)), a.Source(F(136)) - v.Source(F(136)));
    }

    /// <summary>
    /// Con otra fase, dos nudos cuyas diferencias tienen el mismo tamaño y distinto signo: se da la del instante más
    /// temprano (la regla de <c>OffsetAtKnots</c>). Invertir el desempate no hacía caer ninguna prueba.
    /// </summary>
    [Fact]
    public void Curva_en_otra_fase_con_empate_de_tamano_da_el_nudo_mas_temprano()
    {
        foreach (bool reverse in (bool[])[false, true])
        {
            // Acelera de 1× a 3×: el audio, rezagado, recorre 6 cuadros menos al entrar y 18 menos al salir.
            (Clip video, Clip audio, Time early, Time late) = TiedPair(100, 300, reverse);

            Assert.Equal(reverse ? -F(6) : F(6), early);
            Assert.Equal(-early, late);
            Assert.Equal(early, LinkSync.OffsetBetween(video, audio));
            Assert.Equal(-early, LinkSync.OffsetBetween(audio, video));
        }
    }

    [Fact]
    public void Curva_en_otra_fase_con_empate_de_tamano_y_signo_cambiado_da_el_nudo_mas_temprano()
    {
        foreach (bool reverse in (bool[])[false, true])
        {
            // Frena de 3× a 1×: el mismo empate con los signos cambiados.
            (Clip video, Clip audio, Time early, Time late) = TiedPair(300, 100, reverse);

            Assert.Equal(reverse ? F(6) : -F(6), early);
            Assert.Equal(-early, late);
            Assert.Equal(early, LinkSync.OffsetBetween(video, audio));
            Assert.Equal(-early, LinkSync.OffsetBetween(audio, video));
        }
    }

    // --- Ediciones que lo desfasaban sin rechazo ----------------------------------------------------------------------

    /// <summary>Pone en reversa a 1× los clips dados, con el mismo tramo del origen.</summary>
    private static TrimFixture Reversed(TrimFixture f, params string[] ids)
    {
        foreach (string id in ids)
        {
            Clip clip = f.Clip(id);
            clip.Speed = new ClipSpeed { Reverse = true };
            clip.Duration = clip.SourceOut - clip.SourceIn;
        }

        f.AssertValid();
        return f;
    }

    [Fact]
    public void Q_con_ripple_de_pista_sobre_una_pareja_en_reversa_se_rechaza()
    {
        // En reversa a 1× la sincronía es SourceOut + Start igual: 50 + 0 = 40 + 10. Cada pista cerraría un tramo distinto.
        TrimFixture f = Reversed(new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 20, 40, 10, link: "L1")
            .Add("v1", "c2", "m2", 0, 10, 50), "c1", "s1");
        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(f.Clip("c1"), f.Clip("s1")));
        string before = Dump(f);

        AssertFails(() => new UndoStack(f.Project).Execute(Edits.DeleteToPoint(f.Sequence.Id, ["c1"], F(20), TrimEdge.In, ripple: true)), "desincronizaría");

        Assert.Equal(before, Dump(f));
    }

    [Fact]
    public void Q_con_todas_las_pistas_sobre_una_pareja_en_reversa_la_deja_en_sincronia()
    {
        TrimFixture f = Reversed(new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 20, 40, 10, link: "L1")
            .Add("v1", "c2", "m2", 0, 10, 50), "c1", "s1");

        TimelineEdits.DeleteToPoint(f.Ctx, ["c1"], F(20), TrimEdge.In, ripple: true, RippleScope.AllTracks);

        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(f.Clip("c1"), f.Clip("s1")));
        Assert.Empty(LinkSync.Find(f.Sequence));
        f.AssertValid();
    }

    [Fact]
    public void W_con_ripple_de_pista_que_desfasaria_una_pareja_en_reversa_se_rechaza()
    {
        // A1 cierra 20 cuadros y arrastra a V2 por s3, pero c3 empieza antes del tramo y no se corre.
        TrimFixture f = Reversed(new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 40, 0, link: "L1")
            .Add("a1", "s3", "m2", 40, 50, 45, link: "L3")
            .Add("v2", "c3", "m2", 80, 90, 5, link: "L3"), "s3", "c3");
        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(f.Clip("s3"), f.Clip("c3")));
        string before = Dump(f);

        AssertFails(() => new UndoStack(f.Project).Execute(Edits.DeleteToPoint(f.Sequence.Id, ["c1"], F(20), TrimEdge.Out, ripple: true)), "desincronizaría");

        Assert.Equal(before, Dump(f));
    }

    [Fact]
    public void RippleDelete_por_pista_que_desfasaria_una_pareja_en_reversa_se_rechaza()
    {
        // El video se corre 10 cuadros al cerrar el hueco de x; su audio empieza antes del tramo y se queda.
        TrimFixture f = Reversed(new TrimFixture()
            .Add("v1", "x", "m2", 0, 10, 10)
            .Add("v1", "v", "m1", 40, 60, 20, link: "L")
            .Add("a1", "a", "m1", 75, 80, 0, link: "L"), "v", "a");
        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(f.Clip("v"), f.Clip("a")));
        string before = Dump(f);

        AssertFails(() => new UndoStack(f.Project).Execute(Edits.RippleDelete(f.Sequence.Id, ["x"])), "desincronizaría");

        Assert.Equal(before, Dump(f));
    }

    private static string Dump(TrimFixture f) =>
        string.Join(";", f.Sequence.AllTracks.SelectMany(t => t.Clips.Select(c =>
            $"{t.Id}:{c.Id}@{c.Start.Ticks}[{c.SourceIn.Ticks},{c.SourceOut.Ticks}){c.Speed?.Offset.Ticks}")));
}
