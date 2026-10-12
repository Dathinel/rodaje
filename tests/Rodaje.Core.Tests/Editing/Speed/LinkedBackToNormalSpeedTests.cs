using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Editing.Speed;

/// <summary>
/// Ejemplos fijos de lo que cumple cada miembro de un grupo enlazado al volver a 1× (ver <c>AlignEnd</c> y
/// <c>LinkedEnd</c> en <c>TimelineEdits.Speed.cs</c>): sin velocidad, el final en un cuadro de la secuencia o en el borde
/// del archivo, y el material exacto o, según la fase de su inicio, a menos de un cuadro. A 25 fps, con video «c» en v1
/// desde el cuadro 5 y audio «ca» en a1 que empieza <c>offset</c> ticks después, los dos con el mismo tramo de m2
/// (200 cuadros).
/// </summary>
public sealed class LinkedBackToNormalSpeedTests
{
    private static readonly long FrameTicks = TrimFixture.F(1).Ticks;

    /// <summary>Con los dos inicios en un cuadro, los dos vuelven a su material y a su final.</summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(7, 10)]
    [InlineData(1, 10)]
    public void En_la_rejilla_los_dos_vuelven_exactos(int num, int den)
    {
        TrimFixture f = Pair(offset: 0, inFrame: 10, frames: 100);

        RoundTrip(f, "c", new Rational(num, den));

        foreach (string id in (string[])["c", "ca"])
        {
            AssertBackAtNormalSpeed(f, id);
            Assert.Equal(TrimFixture.F(10), f.Clip(id).SourceIn);
            Assert.Equal(TrimFixture.F(110), f.Clip(id).SourceOut);
            Assert.Equal(TrimFixture.F(105), f.Clip(id).End);
        }
    }

    /// <summary>
    /// El audio empieza un tercio de cuadro después y su final no estaba en un cuadro: vuelve con el final en el cuadro
    /// más cercano (el del video) y un tercio de cuadro menos de material. El video, editado, vuelve exacto.
    /// </summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(7, 10)]
    [InlineData(1, 10)]
    public void Fuera_de_la_rejilla_en_mitad_del_archivo_el_final_va_al_cuadro_mas_cercano(int num, int den)
    {
        long third = FrameTicks / 3;
        TrimFixture f = Pair(offset: third, inFrame: 10, frames: 100);

        RoundTrip(f, "c", new Rational(num, den));

        AssertBackAtNormalSpeed(f, "c");
        Assert.Equal(TrimFixture.F(100), f.Clip("c").SourceDuration);
        AssertBackAtNormalSpeed(f, "ca");
        Assert.Equal(TrimFixture.F(105), f.Clip("ca").End);
        Assert.Equal(TrimFixture.F(100) - Time.FromTicks(third), f.Clip("ca").SourceDuration);
    }

    /// <summary>
    /// El audio empieza un tercio de cuadro después y llega al final del archivo: conserva el borde, aunque su final no
    /// caiga en un cuadro, y con él todo su material, igual que el video.
    /// </summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, 10)]
    public void Fuera_de_la_rejilla_al_final_del_archivo_conserva_el_borde(int num, int den)
    {
        long third = FrameTicks / 3;
        TrimFixture f = Pair(offset: third, inFrame: 100, frames: 100);

        RoundTrip(f, "c", new Rational(num, den));

        foreach (string id in (string[])["c", "ca"])
        {
            AssertBackAtNormalSpeed(f, id);
            Assert.Equal(TrimFixture.F(100), f.Clip(id).SourceIn);
            Assert.Equal(TrimFixture.F(200), f.Clip(id).SourceOut);
        }

        Assert.Equal(TrimFixture.F(105) + Time.FromTicks(third), f.Clip("ca").End);
        Assert.NotEqual(f.Clip("ca").End, f.Clip("ca").End.SnapToFrame(Rational.Fps25));
    }

    /// <summary>
    /// Se edita el audio, que empieza medio cuadro después. A 0,3× el material compartido cae en la rejilla del audio
    /// (20 cuadros y medio en vez de 20) y, al volver, el video lo lleva a la suya: 21 cuadros, uno más de los que tenía
    /// aunque empezaba y terminaba en un cuadro. Es el cuadro que la propiedad admite al miembro fuera de fase con el editado.
    /// </summary>
    [Fact]
    public void El_enlazado_en_fase_distinta_del_editado_vuelve_a_un_cuadro_como_mucho()
    {
        long half = FrameTicks / 2;
        TrimFixture f = Pair(offset: half, inFrame: 10, frames: 20);

        RoundTrip(f, "ca", new Rational(3, 10));

        AssertBackAtNormalSpeed(f, "ca");
        Assert.Equal(TrimFixture.F(26), f.Clip("ca").End);
        Assert.Equal(TrimFixture.F(20) + Time.FromTicks(half), f.Clip("ca").SourceDuration);
        AssertBackAtNormalSpeed(f, "c");
        Assert.Equal(TrimFixture.F(26), f.Clip("c").End);
        Assert.Equal(TrimFixture.F(21), f.Clip("c").SourceDuration);
    }

    /// <summary>El mismo par, editando el video: el editado vuelve exacto y el audio, a menos de un cuadro.</summary>
    [Fact]
    public void Editando_el_de_la_rejilla_este_vuelve_exacto()
    {
        long half = FrameTicks / 2;
        TrimFixture f = Pair(offset: half, inFrame: 10, frames: 20);

        RoundTrip(f, "c", new Rational(3, 10));

        AssertBackAtNormalSpeed(f, "c");
        Assert.Equal(TrimFixture.F(25), f.Clip("c").End);
        Assert.Equal(TrimFixture.F(20), f.Clip("c").SourceDuration);
        AssertBackAtNormalSpeed(f, "ca");
        Assert.True(Math.Abs((f.Clip("ca").SourceDuration - TrimFixture.F(20)).Ticks) < FrameTicks);
    }

    private static TrimFixture Pair(long offset, long inFrame, long frames)
    {
        var f = new TrimFixture();
        Time sourceIn = TrimFixture.F(inFrame), sourceOut = TrimFixture.F(inFrame + frames);
        f.Track("v1").Clips.Add(new Clip { Id = "c", MediaId = "m2", SourceIn = sourceIn, SourceOut = sourceOut, Start = TrimFixture.F(5), LinkId = "L" });
        f.Track("a1").Clips.Add(new Clip { Id = "ca", MediaId = "m2", SourceIn = sourceIn, SourceOut = sourceOut, Start = TrimFixture.F(5) + Time.FromTicks(offset), LinkId = "L" });
        f.AssertValid();
        return f;
    }

    private static void RoundTrip(TrimFixture f, string clipId, Rational rate)
    {
        TimelineEdits.SetSpeed(f.Ctx, clipId, rate);
        Assert.NotNull(f.Clip(clipId).Speed);
        TimelineEdits.SetSpeed(f.Ctx, clipId, new Rational(1, 1));
        f.AssertValid();
    }

    /// <summary>Sin velocidad, con el final en un cuadro o en el borde del archivo, y sin pasar de él.</summary>
    private static void AssertBackAtNormalSpeed(TrimFixture f, string id)
    {
        Clip clip = f.Clip(id);
        Time media = f.Project.GetMedia(clip.MediaId).Duration;
        Assert.Null(clip.Speed);
        Assert.True(clip.SourceOut <= media);
        Assert.True(clip.End == clip.End.SnapToFrame(Rational.Fps25) || clip.SourceOut == media, $"«{id}» termina en {clip.End.Ticks}");
    }
}
