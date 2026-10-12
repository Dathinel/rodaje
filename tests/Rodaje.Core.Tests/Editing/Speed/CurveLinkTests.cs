using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Editing.Speed;

/// <summary>
/// Estirar una curva y cambiarla en clips enlazados: el estiramiento llega justo a la duración pedida sin comer ni pedir
/// material, aunque el clip use el archivo entero, y los enlazados llevan la misma curva.
/// </summary>
public sealed class CurveLinkTests
{
    /// <summary>
    /// El caso de la App (<c>SpeedDragTests.EstirarUnaCurva_SaleDelInicioDelGesto_YNoDependeDelCamino</c>): video y audio
    /// de un archivo de 10 s a 30 fps, «Bala» y estirar el final un segundo. La curva estirada, con las velocidades
    /// redondeadas a centésimas, pedía unos ticks más que el archivo y el clip se quedaba en 6,595 s.
    /// </summary>
    [Theory]
    [InlineData(30, 1)]
    [InlineData(25, 1)]
    [InlineData(30000, 1001)]
    [InlineData(60, 1)]
    public void Stretching_bullet_on_a_whole_file_reaches_the_dragged_end_and_keeps_the_material(int num, int den)
    {
        var fps = new Rational(num, den);
        TrimFixture f = Linked(fps, Time.FromSeconds(10), Time.FromSeconds(10), media: Time.FromSeconds(10).SnapToFrame(fps));
        Time ten = f.Clip("c").SourceOut;

        TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.Bullet));
        Time end = f.Clip("c").End;
        TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, Time.FromSeconds(1));

        f.AssertValid();
        foreach (string id in (string[])["c", "ca"])
        {
            Clip clip = f.Clip(id);
            Assert.Equal(end + Time.FromSeconds(1), clip.End);
            Assert.Equal(Time.Zero, clip.SourceIn);
            Assert.Equal(ten, clip.SourceOut);
        }

        Assert.True(f.Clip("c").Speed!.ContentEquals(f.Clip("ca").Speed!));
    }

    [Fact]
    public void Stretching_back_and_forth_never_changes_the_material()
    {
        TrimFixture f = Linked(Rational.Fps25, Time.FromFrames(250, Rational.Fps25), Time.FromFrames(250, Rational.Fps25), media: Time.FromSeconds(40));
        TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.Hero));
        Time material = f.Clip("c").SourceDuration;
        foreach (int frames in (int[])[7, -3, 12, -20, 1, 30, -27])
        {
            Time end = f.Clip("c").End;
            TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, Time.FromFrames(frames, Rational.Fps25));
            f.AssertValid();
            Assert.Equal(end + Time.FromFrames(frames, Rational.Fps25), f.Clip("c").End);
            Assert.Equal(material, f.Clip("c").SourceDuration);
            Assert.Equal(material, f.Clip("ca").SourceDuration);
        }

        TimelineEdits.SetCurve(f.Ctx, "c", null);
        Assert.Equal(material, f.Clip("c").SourceDuration);
        Assert.Equal(material, f.Clip("c").Duration);
    }

    /// <summary>
    /// Enlazados de distinto largo: el primero ajusta la curva a su material y el otro la toma tal cual, con su final en
    /// el cuadro vecino. Antes de compartirla, cada uno ajustaba la suya y el cambio se rechazaba por velocidades distintas.
    /// </summary>
    [Fact]
    public void Linked_clips_of_different_length_share_the_curve()
    {
        TrimFixture f = Linked(Rational.Fps50, Time.FromFrames(116, Rational.Fps50), Time.FromFrames(171, Rational.Fps50), media: Time.FromSeconds(40));
        Time material = f.Clip("c").SourceDuration;
        foreach (SpeedPreset preset in SpeedPresets.All)
        {
            TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(preset));
            f.AssertValid();
            Assert.True(f.Clip("c").Speed!.ContentEquals(f.Clip("ca").Speed!));
            Assert.Equal(material, f.Clip("c").SourceDuration);
            Assert.Equal(f.Clip("ca").End, f.Clip("ca").End.SnapToFrame(Rational.Fps50));
        }

        TimelineEdits.SetCurve(f.Ctx, "c", null);
        f.AssertValid();
        Assert.Equal(material, f.Clip("c").SourceDuration);
    }

    /// <summary>Video «c» y audio «ca» enlazados, desde el inicio de un medio de <paramref name="media"/>.</summary>
    private static TrimFixture Linked(Rational fps, Time video, Time audio, Time media)
    {
        var f = new TrimFixture();
        f.Sequence.FrameRate = fps;
        f.Project.Media.Add(new MediaRef { Id = "m4", Name = "cuatro", Duration = media, HasVideo = true, HasAudio = true });
        f.Track("v1").Clips.Add(new Clip { Id = "c", MediaId = "m4", SourceIn = Time.Zero, SourceOut = video.SnapToFrame(fps), Start = Time.Zero, LinkId = "L" });
        f.Track("a1").Clips.Add(new Clip { Id = "ca", MediaId = "m4", SourceIn = Time.Zero, SourceOut = audio.SnapToFrame(fps), Start = Time.Zero, LinkId = "L" });
        f.AssertValid();
        return f;
    }
}
