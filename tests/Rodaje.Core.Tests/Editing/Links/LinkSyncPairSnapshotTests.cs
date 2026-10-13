using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Editing.Links;

/// <summary>
/// <see cref="LinkSync.Snapshot"/> guardaba solo los tamaños de los desfases y <see cref="LinkSync.EnsureNoNewDesync"/>
/// aceptaba cualquier desfase nuevo del mismo tamaño que uno previo, aunque fuera de otro par o con el signo cambiado.
/// Ahora se compara par a par.
/// </summary>
public sealed class LinkSyncPairSnapshotTests
{
    private const long Frame = 28_224_000;

    private static Time F(long frames) => Time.FromTicks(frames * Frame);

    private static Clip Plain(string id, long sourceIn, long start) => new()
    {
        Id = id, MediaId = "m", LinkId = "l", SourceIn = F(sourceIn), SourceOut = F(sourceIn + 50), Start = F(start),
    };

    private static Sequence Of(params Clip[] clips)
    {
        var sequence = new Sequence { Id = "s" };
        sequence.VideoTracks.Add(new Track { Id = "v1", Clips = { clips[0] } });
        for (int i = 1; i < clips.Length; i++)
        {
            sequence.AudioTracks.Add(new Track { Id = $"a{i}", Clips = { clips[i] } });
        }

        return sequence;
    }

    [Fact]
    public void Un_desfase_nuevo_del_tamano_de_otro_par_se_rechaza()
    {
        // B y C van 5 cuadros por delante de A (y en sincronía entre ellos). C pasa a 5 por detrás: contra A sigue
        // midiendo 5, pero B y C pasan de 0 a 10.
        Clip a = Plain("a", 10, 0), b = Plain("b", 15, 0), c = Plain("c", 15, 0);
        Sequence sequence = Of(a, b, c);
        LinkSync.SyncSnapshot before = LinkSync.Snapshot(sequence);

        c.Start = F(10);

        EditException ex = Assert.Throws<EditException>(() => LinkSync.EnsureNoNewDesync(sequence, before, "Probar"));
        Assert.Contains("desincronizaría", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Un_par_que_cambia_de_signo_se_rechaza()
    {
        Clip a = Plain("a", 10, 0), b = Plain("b", 15, 0);
        Sequence sequence = Of(a, b);
        LinkSync.SyncSnapshot before = LinkSync.Snapshot(sequence);

        b.Start = F(10);

        Assert.Equal(-F(5), LinkSync.OffsetBetween(a, b));
        Assert.Throws<EditException>(() => LinkSync.EnsureNoNewDesync(sequence, before, "Probar"));
    }

    [Fact]
    public void Un_desfase_que_ya_estaba_o_que_se_arregla_pasa()
    {
        Clip a = Plain("a", 10, 0), b = Plain("b", 15, 0), c = Plain("c", 10, 0);
        Sequence sequence = Of(a, b, c);
        LinkSync.SyncSnapshot before = LinkSync.Snapshot(sequence);

        // Todos corridos juntos: nada cambia entre ellos.
        foreach (Clip clip in (Clip[])[a, b, c])
        {
            clip.Start += F(7);
        }

        LinkSync.EnsureNoNewDesync(sequence, before, "Probar");

        // B vuelve a la sincronía: tampoco es un desfase nuevo.
        b.Start += F(5);
        Assert.Empty(LinkSync.Find(sequence));
        LinkSync.EnsureNoNewDesync(sequence, before, "Probar");
    }

    /// <summary>
    /// Con curva en distinta fase el desfase medido cambia según dónde estén recortados, pero lo que se compara antes y
    /// después (el ancla y la fase de cada uno) no: recortar los dos igual no es un desfase nuevo, y correr uno sí.
    /// </summary>
    [Fact]
    public void Con_curva_en_otra_fase_recortar_no_cuenta_y_correr_si()
    {
        var curve = new SpeedCurve { Span = F(100), Points = [new SpeedPoint(0, 100), new SpeedPoint(SpeedCurve.AtScale, 400)] };
        Clip Curved(string id, long start, long offset)
        {
            var clip = new Clip { Id = id, MediaId = "m", LinkId = "l", Speed = new ClipSpeed { Curve = curve.Clone(), Offset = F(offset) } };
            ClipTimeMap.Create(F(start), F(60), F(10), clip.Speed).ApplyTo(clip);
            return clip;
        }

        Clip a = Curved("a", 0, 0), b = Curved("b", 0, 20);
        Sequence sequence = Of(a, b);
        Assert.NotEqual(Time.Zero, LinkSync.OffsetBetween(a, b));
        LinkSync.SyncSnapshot before = LinkSync.Snapshot(sequence);

        ClipTimeMap.Of(a).TrimIn(F(15)).ApplyTo(a);
        ClipTimeMap.Of(b).TrimIn(F(15)).TrimOut(F(40)).ApplyTo(b);
        LinkSync.EnsureNoNewDesync(sequence, before, "Probar");

        ClipTimeMap.Of(b).MoveTo(b.Start - F(3)).ApplyTo(b);
        Assert.Throws<EditException>(() => LinkSync.EnsureNoNewDesync(sequence, before, "Probar"));
    }
}
