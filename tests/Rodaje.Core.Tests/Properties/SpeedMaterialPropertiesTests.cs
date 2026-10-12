using CsCheck;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit.Abstractions;

namespace Rodaje.Core.Tests.Properties;

/// <summary>
/// Sobre 10 000 proyectos al azar (los de <see cref="EditPropertiesTests"/>): encadenar preajustes, curvas y velocidades
/// lentas en un clip a 1× y volver a 1× le devuelve exactamente su material, a él y a sus enlazados (si el final de
/// alguno del grupo no estaba en un cuadro o le quedaba menos de un cuadro de archivo detrás, a lo sumo un cuadro distinto).
/// </summary>
/// <remarks>Para reproducir un fallo, pasar la semilla que reporta CsCheck en <c>seed</c> o en <c>CsCheck_Seed</c>.</remarks>
public class SpeedMaterialPropertiesTests(ITestOutputHelper output)
{
    private const string Seq = BuiltProject.SequenceId;

    /// <summary>
    /// Por debajo de estos cuadros una curva rápida dura apenas unos cuadros y su forma no deja ajustar el material al
    /// tick: puede volver con un cuadro de diferencia, una vez.
    /// </summary>
    private const int ShortClipFrames = 16;

    internal static readonly Gen<(ProjectSpec Project, int Clip, int[] Steps)> GenChain =
        Gen.Select(RandomTimeline.GenProject, Gen.Int[0, 1000], Gen.Int[0, 100_000].Array[1, 6]);

    [Fact]
    public void EncadenarCurvasYVolverA1x_DevuelveElMaterial()
    {
        int checkedChains = 0;
        GenChain.Sample(
            s =>
            {
                if (RunChain(s.Project, s.Clip, s.Steps))
                {
                    Interlocked.Increment(ref checkedChains);
                }
            },
            writeLine: output.WriteLine,
            iter: 10000,
            print: s => $"{s.Project}\nClip {s.Clip}; pasos: {string.Join(" ", s.Steps)}");

        output.WriteLine($"cadenas comprobadas: {checkedChains}");
        Assert.True(checkedChains > 2500, $"solo {checkedChains} cadenas comprobadas");
    }

    /// <summary>Verdadero si el clip elegido estaba a 1×, se le aplicó algún cambio y volvió a 1×.</summary>
    internal static bool RunChain(ProjectSpec spec, int clipIndex, int[] steps)
    {
        BuiltProject built = RandomTimeline.Build(spec);
        List<Clip> clips = [.. built.Sequence.AllTracks.Where(t => !t.Locked).SelectMany(t => t.Clips)];
        if (clips.Count == 0)
        {
            return false;
        }

        string clipId = clips[clipIndex % clips.Count].Id;
        List<Clip> group = Group(built, clipId);
        // Solo clips de un medio a 1× en pistas libres (un título no tiene medio ni velocidad).
        if (group.Exists(c => c.Speed is not null || built.Sequence.FindClip(c.Id)!.Value.Track.Locked
            || !built.Project.Media.Exists(m => m.Id == c.MediaId)))
        {
            return false;
        }

        // Enlazados con material distinto comparten la curva del primero, que solo puede ajustarse a uno: los demás
        // llevan su final al cuadro vecino (como en main), así que no vuelven exactos. Eso se prueba en CurveLinkTests.
        if (group.Exists(c => Material(c) != Material(group[0])))
        {
            return false;
        }

        Dictionary<string, long> original = group.ToDictionary(c => c.Id, Material);
        // El grupo comparte material: si un enlazado no puede volver exacto (empieza fuera de un cuadro, por ejemplo), el
        // redondeo a su rejilla lo arrastran los demás, y al volver a 1× cada uno va al punto más cercano de la suya.
        long groupTolerance = group.Max(c => Tolerance(built, c));
        Dictionary<string, long> tolerance = group.ToDictionary(c => c.Id, _ => groupTolerance);
        bool applied = false;
        foreach (int step in steps)
        {
            SequenceEditCommand command = (step % 3) switch
            {
                0 => Edits.ApplySpeedPreset(Seq, clipId, SpeedPresets.All[step / 3 % SpeedPresets.All.Count], ids: built.Ids),
                1 => Edits.SetCurve(Seq, clipId, Curve(step / 3), ids: built.Ids),
                _ => Edits.SetSpeed(Seq, clipId, new Rational(10 + (step / 3 % 91), 100), ids: built.Ids),
            };

            try
            {
                command.Apply(built.Project);
                applied = true;
                if (Group(built, clipId).Exists(c => c.Duration <= Time.FromFrames(1, built.Rate)))
                {
                    // A esa velocidad el clip no llega a un cuadro: dura uno y recorre el material que quepa en él.
                    return false;
                }
            }
            catch (EditException)
            {
            }
        }

        if (!applied)
        {
            return false;
        }

        try
        {
            Edits.SetSpeed(Seq, clipId, new Rational(1, 1), ids: built.Ids).Apply(built.Project);
        }
        catch (EditException)
        {
            // Al acortarse no puede correr lo que sigue (una pista bloqueada, por ejemplo): la edición se rechaza entera.
            return false;
        }

        foreach (Clip clip in Group(built, clipId))
        {
            Assert.Null(clip.Speed);
            Assert.True(Math.Abs(original[clip.Id] - Material(clip)) <= tolerance[clip.Id], $"«{clip.Id}»: material {Material(clip)}, antes {original[clip.Id]}");
        }

        return true;
    }

    /// <summary>
    /// Exacto, salvo si el final no estaba en un cuadro o al clip le quedaba menos de un cuadro de archivo detrás: una
    /// velocidad lenta no puede pedir más material del que hay y queda hasta un cuadro corta, así que puede volver en el
    /// cuadro anterior (un cuadro más lo que su final pasaba del cuadro). Una vez, no se acumula.
    /// </summary>
    private static long Tolerance(BuiltProject built, Clip clip)
    {
        Time floor = Time.FromFrames(clip.End.ToFrames(built.Rate), built.Rate);
        Time frame = Time.FromFrames(1, built.Rate);
        bool exact = clip.End == floor && clip.SourceOut + frame <= built.Project.GetMedia(clip.MediaId).Duration
            && clip.Duration >= Time.FromFrames(ShortClipFrames, built.Rate);
        return exact ? 0 : Time.FromFrames(1, built.Rate).Ticks + (clip.End - floor).Ticks;
    }

    /// <summary>Una curva de tres puntos que termina a 1× o menos.</summary>
    private static SpeedCurve Curve(int n) => new()
    {
        Span = Time.FromSeconds(1),
        Points = [new(0, 10 + (n % 991)), new(1 + (n / 7 % 9998), 10 + (n / 3 % 991)), new(SpeedCurve.AtScale, 10 + (n / 11 % 91))],
    };

    private static List<Clip> Group(BuiltProject built, string clipId)
    {
        Clip clip = built.Sequence.FindClip(clipId)!.Value.Clip;
        return clip.LinkId is null ? [clip] : [.. built.Sequence.AllTracks.SelectMany(t => t.Clips).Where(c => c.LinkId == clip.LinkId)];
    }

    private static long Material(Clip clip) => (clip.SourceOut - clip.SourceIn).Ticks;
}
