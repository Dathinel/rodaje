using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Editing;

/// <summary>
/// Enlazados a la misma velocidad distinta de 1×: <see cref="LinkSync.OffsetBetween"/> los comparaba por contenido, así que
/// un <see cref="ClipSpeed.Offset"/> distinto (el que deja dividir o recortar) o un <see cref="ClipSpeed.PreservePitch"/>
/// distinto (el audio con otro ajuste) los dejaba «sin medir» aunque el origen se mapeara igual.
/// </summary>
public sealed class LinkSyncSpeedTests
{
    private static readonly Time Frame = Time.FromFrames(1, Rational.Fps30);

    private static Clip At(string id, long start, long sourceIn, long duration, Rational rate, long offset = 0, bool pitch = true, bool reverse = false)
    {
        var clip = new Clip
        {
            Id = id, MediaId = "m", Start = Time.FromTicks(start), SourceIn = Time.FromTicks(sourceIn),
            Speed = new ClipSpeed { Rate = rate, Offset = Time.FromTicks(offset), PreservePitch = pitch, Reverse = reverse },
        };
        // El tramo del origen no depende del sentido: se mide hacia adelante y se ancla en la entrada.
        ClipTimeMap map = ClipTimeMap.Create(clip.Start, Time.FromTicks(duration), clip.SourceIn, new ClipSpeed { Rate = rate, Offset = Time.FromTicks(offset) });
        clip.Duration = map.Duration;
        clip.SourceOut = map.SourceOut;
        return clip;
    }

    [Fact]
    public void ADosX_ConDistintoTono_SeMide()
    {
        Clip video = At("v", 0, 0, 1_000_000, new Rational(2, 1));
        Clip audio = At("a", 0, 0, 1_000_000, new Rational(2, 1), pitch: false);

        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(video, audio));

        Clip late = At("a2", 0, Frame.Ticks, 1_000_000, new Rational(2, 1), pitch: false);
        Assert.Equal(Frame, LinkSync.OffsetBetween(video, late));
    }

    /// <summary>La segunda mitad de un clip a 2× dividido lleva el Offset corrido y muestra lo mismo que la pareja entera.</summary>
    [Fact]
    public void ADosX_ConDistintoOffset_SeMide()
    {
        const long half = 5_000_000;
        Clip whole = At("v", 0, 0, 2 * half, new Rational(2, 1));
        Clip rest = At("a", half, 2 * half, half, new Rational(2, 1), offset: half);

        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(whole, rest));
        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(rest, whole));

        Clip ahead = At("a2", half, 2 * half + Frame.Ticks, half, new Rational(2, 1), offset: half);
        Assert.Equal(Frame, LinkSync.OffsetBetween(whole, ahead));
        Assert.Equal(-Frame, LinkSync.OffsetBetween(ahead, whole));
    }

    /// <summary>
    /// A 3/2× el origen de cada mitad se redondea desde su Offset: comparar las entradas sin él daba un tick de desfase
    /// a un par que muestra exactamente lo mismo.
    /// </summary>
    [Fact]
    public void ATresMedios_ConOffsetsQueRedondeanDistinto_DaCero()
    {
        var rate = new Rational(3, 2);
        Clip reference = At("v", 0, 0, 1_000, rate, offset: 1);
        // ⌊1,5·8⌋ − ⌊1,5·1⌋ = 11: lo que avanzó el origen de la referencia en 7 ticks desde su Offset.
        Clip split = At("a", 7, 11, 500, rate, offset: 8);

        Assert.True(ClipTimeMap.Of(reference).SameCourse(ClipTimeMap.Of(split)));
        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(reference, split));
    }

    [Fact]
    public void EnReversa_ConDistintoTono_SeMide()
    {
        Clip video = At("v", 0, 0, 1_000_000, new Rational(2, 1), reverse: true);
        Clip audio = At("a", 0, 0, 1_000_000, new Rational(2, 1), pitch: false, reverse: true);

        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(video, audio));
    }

    [Fact]
    public void VelocidadesDistintas_SiguenSinMedirse()
    {
        Clip video = At("v", 0, 0, 1_000_000, new Rational(2, 1));
        Clip audio = At("a", 0, 0, 1_000_000, new Rational(3, 1));

        Assert.Null(LinkSync.OffsetBetween(video, audio));
    }
}
