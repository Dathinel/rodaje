using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using Xunit.Abstractions;

namespace Rodaje.Core.Tests.Editing.Speed;

/// <summary>
/// Cadenas aleatorias de preajustes, curvas y velocidades constantes, y vuelta a 1×: el material vuelve a ser el original.
/// Con velocidad constante por encima de 1× un cuadro de la línea de tiempo abarca más de un cuadro de origen, así que
/// ese cambio puede mover el material hasta medio cuadro de la línea de tiempo (<c>r/2</c> cuadros de origen); lo
/// demás no mueve nada.
/// </summary>
public sealed class CurveMaterialPropertyTests(ITestOutputHelper output)
{
    private static readonly Rational[] Rates = [Rational.Fps25, Rational.Ntsc30, Rational.Fps24, Rational.Ntsc24, Rational.Fps60, Rational.Fps50];

    [Fact]
    public void Chains_of_curves_and_slow_speeds_come_back_to_the_original_material()
    {
        int chains = 0, steps = 0;
        for (int seed = 0; seed < 3000; seed++)
        {
            var random = new Random(seed);
            (TrimFixture f, UndoStack stack, Rational fps, long original) = Build(random);
            int length = random.Next(1, 7);
            for (int i = 0; i < length; i++)
            {
                steps += Apply(f, stack, random, maxRate: new Rational(1, 1)) ? 1 : 0;
                AssertKeeps(f, original, fps, seed, exact: false);
            }

            Back(f, stack, random);
            AssertKeeps(f, original, fps, seed, exact: true);
            chains++;
        }

        output.WriteLine($"cadenas: {chains}; cambios aplicados: {steps}");
        Assert.True(steps > 6000, $"solo {steps} cambios aplicados");
    }

    [Fact]
    public void A_fast_speed_moves_the_material_by_at_most_half_a_timeline_frame_and_curves_add_nothing()
    {
        for (int seed = 0; seed < 3000; seed++)
        {
            var random = new Random(seed);
            (TrimFixture f, UndoStack stack, Rational fps, long original) = Build(random);
            for (int i = random.Next(0, 3); i > 0; i--)
            {
                Apply(f, stack, random, maxRate: new Rational(1, 1));
            }

            var rate = new Rational(random.Next(11, 1001), 10);
            try
            {
                stack.Execute(Edits.SetSpeed("seq", "c", rate));
            }
            catch (EditException)
            {
                continue;
            }

            long frame = Time.FromFrames(1, fps).Ticks;
            if (original < rate.ToDouble() * frame)
            {
                // Más corto que un cuadro a esa velocidad: el clip dura al menos un cuadro, así que recorre más material.
                continue;
            }

            Assert.True(Math.Abs(Material(f.Clip("c")) - original) <= (long)Math.Ceiling(rate.ToDouble() * frame / 2) + 1, $"semilla {seed}: {rate}, material {Material(f.Clip("c"))}, original {original}, cuadro {frame}");

            // Lo que sigue (curvas, velocidades lentas, 1×) ya no mueve más el material: vuelve a la rejilla de 1×.
            long moved = SnapMaterial(f.Clip("c"), fps);
            for (int i = random.Next(0, 4); i > 0; i--)
            {
                Apply(f, stack, random, maxRate: new Rational(1, 1));
            }

            Back(f, stack, random);
            Assert.Equal(moved, Material(f.Clip("c")));
        }
    }

    [Fact]
    public void Repeating_a_preset_or_a_curve_never_changes_the_material()
    {
        foreach (Rational fps in Rates)
        {
            foreach (SpeedPreset preset in SpeedPresets.All)
            {
                var random = new Random(preset.GetHashCode());
                (TrimFixture f, UndoStack stack, _, long original) = Build(random, fps);
                for (int i = 0; i < 20; i++)
                {
                    stack.Execute(Edits.ApplySpeedPreset("seq", "c", i % 2 == 0 ? preset : SpeedPreset.Hero));
                    Assert.Equal(original, Material(f.Clip("c")));
                    Assert.Equal(f.Clip("c").End, f.Clip("c").End.SnapToFrame(fps));
                }
            }
        }
    }

    /// <summary>Un clip enlazado (video y audio) de 1 a 40 s en un medio largo, con su secuencia a una cadencia al azar.</summary>
    private static (TrimFixture F, UndoStack Stack, Rational Fps, long Original) Build(Random random, Rational? rate = null)
    {
        Rational fps = rate ?? Rates[random.Next(Rates.Length)];
        var f = new TrimFixture();
        f.Sequence.FrameRate = fps;
        long frames = (long)(random.Next(1, 41) * fps.ToDouble());
        long sourceIn = random.Next(0, 200);
        long start = random.Next(0, 200);
        f.Project.Media.Add(new MediaRef { Id = "m4", Name = "largo", Duration = Time.FromFrames(sourceIn + frames + 8000, fps), HasVideo = true, HasAudio = true });
        foreach ((string track, string id) in (ReadOnlySpan<(string, string)>)[("v1", "c"), ("a1", "ca")])
        {
            f.Track(track).Clips.Add(new Clip
            {
                Id = id,
                MediaId = "m4",
                SourceIn = Time.FromFrames(sourceIn, fps),
                SourceOut = Time.FromFrames(sourceIn + frames, fps),
                Start = Time.FromFrames(start, fps),
                LinkId = "L",
            });
        }

        f.AssertValid();
        return (f, new UndoStack(f.Project), fps, Material(f.Clip("c")));
    }

    /// <summary>Un preajuste, una curva al azar o una velocidad constante hasta <paramref name="maxRate"/>.</summary>
    private static bool Apply(TrimFixture f, UndoStack stack, Random random, Rational maxRate)
    {
        ICommand command = random.Next(3) switch
        {
            0 => Edits.ApplySpeedPreset("seq", "c", SpeedPresets.All[random.Next(SpeedPresets.All.Count)]),
            1 => Edits.SetCurve("seq", "c", RandomCurve(random, (int)(maxRate.ToDouble() * 100))),
            _ => Edits.SetSpeed("seq", "c", new Rational(random.Next(1, (int)(maxRate.ToDouble() * 100) + 1), 100)),
        };

        try
        {
            stack.Execute(command);
            stack.BreakMerge();
            f.AssertValid();
            return true;
        }
        catch (EditException)
        {
            return false;
        }
    }

    private static void Back(TrimFixture f, UndoStack stack, Random random)
    {
        stack.Execute(random.Next(2) == 0 ? Edits.SetSpeed("seq", "c", new Rational(1, 1)) : Edits.SetCurve("seq", "c", null));
        f.AssertValid();
        Assert.Null(f.Clip("c").Speed);
    }

    /// <summary>Una curva al azar de 2 a 6 puntos que termina a no más de <paramref name="maxEnd"/> centésimas.</summary>
    private static SpeedCurve RandomCurve(Random random, int maxEnd)
    {
        int count = random.Next(2, 7);
        var at = new SortedSet<int> { 0, SpeedCurve.AtScale };
        while (at.Count < count)
        {
            at.Add(random.Next(1, SpeedCurve.AtScale));
        }

        return new SpeedCurve
        {
            Span = Time.FromSeconds(1),
            Points = [.. at.Select(a => new SpeedPoint(a, random.Next(RetimeMath.MinHundredths,
                (a == SpeedCurve.AtScale ? maxEnd : RetimeMath.MaxHundredths) + 1)))],
        };
    }

    /// <summary>
    /// El material de video y audio es el mismo; a 1× es el original (exacto) y con velocidad, el original salvo unos
    /// ticks (una curva que cambia más de un tick de origen por tick) o, a velocidad constante lenta, menos de medio cuadro.
    /// </summary>
    private static void AssertKeeps(TrimFixture f, long original, Rational fps, int seed, bool exact)
    {
        Clip video = f.Clip("c");
        Clip audio = f.Clip("ca");
        Assert.Equal(Material(video), Material(audio));
        long tolerance = exact ? 0 : Time.FromFrames(1, fps).Ticks / 2;
        Assert.True(Math.Abs(Material(video) - original) <= tolerance,
            $"semilla {seed}: material {Material(video)} y el original {original} ({video.Speed?.Rate}, curva {video.Speed?.Curve is not null})");
        Assert.Equal(video.End, video.End.SnapToFrame(fps));
    }

    private static long Material(Clip clip) => (clip.SourceOut - clip.SourceIn).Ticks;

    /// <summary>El material del clip con su final de 1× al cuadro más cercano.</summary>
    private static long SnapMaterial(Clip clip, Rational fps) =>
        ((clip.Start + Time.FromTicks(Material(clip))).SnapToFrame(fps) - clip.Start).Ticks;
}
