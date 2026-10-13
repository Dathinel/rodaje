using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Editing.Speed;

/// <summary>
/// <see cref="LinkSync.OffsetBetween"/> con dos enlazados a la misma velocidad distinta de 1×: comparaba las velocidades
/// por contenido (<c>ContentEquals</c>), así que un distinto <c>Offset</c> o <c>PreservePitch</c>, que no cambian dónde cae
/// el origen a velocidad constante, dejaba el par sin medir y su desfase no se veía. Ahora cuenta solo lo que mueve el
/// origen.
/// </summary>
public sealed class LinkSyncSameRateTests
{
    private const long Frame = 28_224_000;

    private static Clip Clip(string id, long start, long sourceIn, long sourceOut, long duration, ClipSpeed speed) => new()
    {
        Id = id, MediaId = "m", LinkId = "l", Start = Time.FromTicks(start), SourceIn = Time.FromTicks(sourceIn),
        SourceOut = Time.FromTicks(sourceOut), Speed = speed, Duration = Time.FromTicks(duration),
    };

    private static ClipSpeed At2x(long offset = 0, bool preservePitch = true) =>
        new() { Rate = new Rational(2, 1), Offset = Time.FromTicks(offset), PreservePitch = preservePitch };

    /// <summary>El origen que muestra cada clip en unos instantes comunes, para comprobar que «se mapea igual».</summary>
    private static void AssertSameSource(Clip a, Clip b, Time shift)
    {
        ClipTimeMap ma = ClipTimeMap.Of(a), mb = ClipTimeMap.Of(b);
        Time from = a.Start > b.Start ? a.Start : b.Start;
        Time to = a.End < b.End ? a.End : b.End;
        for (int i = 0; i < 10; i++)
        {
            Time t = from + Time.FromTicks((to - from).Ticks * i / 10);
            Assert.Equal(ma.Source(t) + shift, mb.Source(t));
        }
    }

    [Fact]
    public void A2x_ConDistintoOffset_EnSincronia_DaCero()
    {
        Clip video = Clip("v", 10 * Frame, 0, 200 * Frame, 100 * Frame, At2x(offset: 0));
        Clip audio = Clip("a", 10 * Frame, 0, 200 * Frame, 100 * Frame, At2x(offset: 7 * Frame));

        AssertSameSource(video, audio, Time.Zero);
        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(video, audio));
    }

    [Fact]
    public void A2x_ConDistintoPreservePitch_MideElDesfase()
    {
        Clip video = Clip("v", 10 * Frame, 0, 200 * Frame, 100 * Frame, At2x());
        Clip audio = Clip("a", 10 * Frame, 6 * Frame, 206 * Frame, 100 * Frame, At2x(preservePitch: false));

        AssertSameSource(video, audio, Time.FromTicks(6 * Frame));
        Assert.Equal(Time.FromTicks(6 * Frame), LinkSync.OffsetBetween(video, audio));
        Assert.Equal(Time.FromTicks(-6 * Frame), LinkSync.OffsetBetween(audio, video));

        var sequence = new Sequence { Id = "s" };
        sequence.VideoTracks.Add(new Track { Id = "v1", Clips = { video } });
        sequence.AudioTracks.Add(new Track { Id = "a1", Clips = { audio } });
        SyncOffset sync = Assert.Single(LinkSync.Find(sequence));
        Assert.Equal(("a", "v", Time.FromTicks(6 * Frame)), (sync.ClipId, sync.PartnerId, sync.Offset));
    }

    /// <summary>
    /// A 3/2 el Offset sí entra en el redondeo (<c>⌊I(Offset + τ)⌋ − ⌊I(Offset)⌋</c>): el audio recortado un tick por delante,
    /// como lo deja recortar los dos, sigue en sincronía con el video aunque sus Offset difieran.
    /// </summary>
    [Fact]
    public void A3Medios_RecortadoUnTick_SigueEnSincronia()
    {
        var rate = new Rational(3, 2);
        Clip video = Clip("v", 1000, 0, 3000, 2000, new ClipSpeed { Rate = rate, Offset = Time.FromTicks(1) });
        // ⌊1,5·2⌋ − ⌊1,5·1⌋ = 2 ticks más de origen por un tick de línea de tiempo.
        Clip audio = Clip("a", 1001, 2, 3000, 1999, new ClipSpeed { Rate = rate, Offset = Time.FromTicks(2), PreservePitch = false });

        AssertSameSource(video, audio, Time.Zero);
        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(video, audio));
        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(audio, video));
    }

    /// <summary>En reversa el ancla es el final del origen: el mismo SourceOut con distinto SourceIn está en sincronía.</summary>
    [Fact]
    public void EnReversa_SeComparaElFinalDelOrigen()
    {
        ClipSpeed Reverse(long offset) => new() { Rate = new Rational(2, 1), Reverse = true, Offset = Time.FromTicks(offset) };
        Clip video = Clip("v", 10 * Frame, 0, 200 * Frame, 100 * Frame, Reverse(0));
        Clip shorter = Clip("a", 10 * Frame, 100 * Frame, 200 * Frame, 50 * Frame, Reverse(0));
        Clip shifted = Clip("b", 10 * Frame, 0, 190 * Frame, 95 * Frame, Reverse(0));

        AssertSameSource(video, shorter, Time.Zero);
        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(video, shorter));
        // Mismo SourceIn y otro SourceOut: va diez cuadros por detrás y no puede darse por sincronizado.
        Assert.NotEqual(Time.Zero, LinkSync.OffsetBetween(video, shifted));
    }

    /// <summary>Con curva, el Offset es la posición en la curva y sí mueve el origen: dos Offset distintos no se miden.</summary>
    [Fact]
    public void ConCurva_ElOffsetCuenta()
    {
        ClipSpeed Curve(long offset) => new()
        {
            Offset = Time.FromTicks(offset),
            Curve = new SpeedCurve { Span = Time.FromTicks(100 * Frame), Points = [new SpeedPoint(0, 100), new SpeedPoint(SpeedCurve.AtScale, 300)] },
        };
        Clip video = Clip("v", 10 * Frame, 0, 200 * Frame, 50 * Frame, Curve(0));
        Clip audio = Clip("a", 10 * Frame, 0, 200 * Frame, 50 * Frame, Curve(20 * Frame));
        Clip same = Clip("b", 10 * Frame, 0, 200 * Frame, 50 * Frame, Curve(0));
        same.Speed!.PreservePitch = false;

        Assert.Null(LinkSync.OffsetBetween(video, audio));
        Assert.Equal(Time.Zero, LinkSync.OffsetBetween(video, same));
    }
}
