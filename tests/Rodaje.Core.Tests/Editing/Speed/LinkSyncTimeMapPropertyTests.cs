using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit.Abstractions;

namespace Rodaje.Core.Tests.Editing.Speed;

/// <summary>
/// <see cref="LinkSync.OffsetBetween"/> contra <see cref="ClipTimeMap"/>, la única puerta del origen: decenas de miles de
/// parejas al azar (semilla fija) a la misma velocidad constante, hacia adelante y en reversa, con velocidades racionales
/// variadas y Offset distintos. Con velocidad constante <c>r</c> la diferencia del origen en un instante es
/// <c>ΔA + ⌊r(t + φ₁)⌋ − ⌊r(t + φ₀)⌋</c>, que toma uno o dos valores; en cualquier tramo de al menos <c>den</c> ticks
/// salen todos. Así que, recorriendo tick a tick un tramo común así de largo, el desfase medido tiene que ser uno de los
/// valores vistos, y el único si solo hay uno (en particular, cero solo si muestran lo mismo en todo instante).
/// </summary>
public sealed class LinkSyncTimeMapPropertyTests(ITestOutputHelper output)
{
    private const int Seed = 144_2026;
    private const int Cases = 40_000;

    private static readonly Rational[] Rates =
    [
        new(1, 1), new(2, 1), new(1, 2), new(3, 2), new(2, 3), new(5, 4), new(4, 5), new(7, 3), new(3, 7), new(10, 1),
        new(1, 10), new(11, 12), new(12, 11), new(100, 1), new(9, 7), new(25, 24), new(24, 25), new(1001, 1000),
    ];

    [Fact]
    public void Con_velocidad_constante_el_desfase_es_el_que_muestra_el_mapeo()
    {
        var random = new Random(Seed);
        int checkedCases = 0, inSync = 0, twoValues = 0, reversed = 0;
        var failures = new List<string>();
        for (int n = 0; n < Cases && failures.Count < 10; n++)
        {
            Rational rate = Rates[random.Next(Rates.Length)];
            bool reverse = random.Next(2) == 0;
            (Clip reference, Clip clip) = MakePair(random, rate, reverse);
            ClipTimeMap r = ClipTimeMap.Of(reference), c = ClipTimeMap.Of(clip);
            Time from = r.Start > c.Start ? r.Start : c.Start;
            Time to = r.End < c.End ? r.End : c.End;
            if ((to - from).Ticks < 2 * rate.Den)
            {
                continue;
            }

            // Tick a tick un tramo de al menos dos periodos, y unos instantes sueltos del resto.
            var seen = new SortedSet<long>();
            long window = Math.Min((to - from).Ticks, Math.Max(4 * rate.Den, 64));
            for (long i = 0; i < window; i++)
            {
                Time t = from + Time.FromTicks(i);
                seen.Add((c.Source(t) - r.Source(t)).Ticks);
            }

            for (int i = 0; i < 16; i++)
            {
                Time t = from + Time.FromTicks(random.NextInt64((to - from).Ticks));
                seen.Add((c.Source(t) - r.Source(t)).Ticks);
            }

            checkedCases++;
            reversed += reverse ? 1 : 0;
            inSync += seen.Count == 1 && seen.Min == 0 ? 1 : 0;
            twoValues += seen.Count == 2 ? 1 : 0;
            Time? offset = LinkSync.OffsetBetween(reference, clip);
            Time? back = LinkSync.OffsetBetween(clip, reference);
            string Describe() =>
                $"{rate} {(reverse ? "reversa" : "adelante")}: ref {r} Offset {reference.Speed!.Offset.Ticks}, clip {c} Offset {clip.Speed!.Offset.Ticks}; "
                + $"visto [{string.Join(", ", seen)}], medido {(offset is { } o ? $"{o.Ticks}" : "null")}, al revés {(back is { } b ? $"{b.Ticks}" : "null")}";
            if (offset is not { } value || seen.Count > 2 || (seen.Count == 2 && seen.Max - seen.Min != 1)
                || !seen.Contains(value.Ticks) || back != -value)
            {
                failures.Add(Describe());
            }
        }

        output.WriteLine($"parejas comprobadas: {checkedCases} (en reversa {reversed}); en sincronía {inSync}; con dos valores {twoValues}");
        Assert.True(failures.Count == 0, $"{failures.Count} discrepancias:\n{string.Join("\n", failures)}");
        // Que el generador cubra los tres casos: sincronía exacta, desfase entero y desfase que alterna.
        Assert.True(checkedCases > Cases * 3 / 4, $"solo {checkedCases} parejas comprobadas");
        Assert.True(inSync > checkedCases / 10, $"solo {inSync} parejas en sincronía");
        Assert.True(twoValues > checkedCases / 20, $"solo {twoValues} parejas con dos valores");
        Assert.True(reversed > checkedCases / 3, $"solo {reversed} parejas en reversa");
    }

    /// <summary>
    /// Con la misma curva: si el clip sale de la referencia recortando, dividiendo o deslizando (misma fase), la diferencia
    /// es la misma en todo instante y el desfase medido es exactamente esa; si además se corre en la línea de tiempo (otra
    /// fase), se mide igual (no «no comparable») y al invertir el par solo cambia el signo.
    /// </summary>
    [Fact]
    public void Con_la_misma_curva_el_desfase_es_el_que_muestra_el_mapeo()
    {
        var random = new Random(Seed);
        int samePhase = 0, otherPhase = 0;
        var failures = new List<string>();
        for (int n = 0; n < 4000 && failures.Count < 10; n++)
        {
            bool reverse = random.Next(2) == 0;
            var curve = new SpeedCurve
            {
                Span = Time.FromTicks(random.Next(200, 4000)),
                Points = [new SpeedPoint(0, random.Next(10, 1001)), new SpeedPoint(random.Next(1, SpeedCurve.AtScale), random.Next(10, 1001)), new SpeedPoint(SpeedCurve.AtScale, random.Next(10, 1001))],
            };
            var referenceSpeed = new ClipSpeed { Curve = curve, Reverse = reverse, Offset = Time.FromTicks(random.Next(-500, 2000)) };
            ClipTimeMap reference = ClipTimeMap.Create(Time.FromTicks(random.Next(0, 5000)), Time.FromTicks(random.Next(100, 3000)),
                Time.FromTicks(reverse ? 50_000_000 : random.Next(1_000_000)), referenceSpeed);
            ClipTimeMap clip = reference;
            if (random.Next(2) == 0)
            {
                clip = clip.TrimIn(clip.Start + Time.FromTicks(1 + random.NextInt64(clip.Duration.Ticks / 2)));
            }

            if (random.Next(2) == 0)
            {
                clip = clip.TrimOut(clip.End - Time.FromTicks(random.NextInt64(clip.Duration.Ticks / 2)));
            }

            clip = clip.Slip(Time.FromTicks(random.Next(3) == 0 ? 0 : random.Next(-2000, 2000)));
            bool moved = random.Next(2) == 0;
            if (moved)
            {
                clip = clip.MoveTo(clip.Start + Time.FromTicks(random.Next(2) == 0 ? random.Next(1, 4) : random.Next(5, 1500)));
            }

            var clipSpeed = new ClipSpeed { Curve = curve.Clone(), Reverse = reverse, PreservePitch = false };
            Clip r = Build("r", reference, referenceSpeed), c = Build("c", clip, clipSpeed);
            Time? offset = LinkSync.OffsetBetween(r, c);
            Time? back = LinkSync.OffsetBetween(c, r);
            var seen = new SortedSet<long>();
            for (long t = clip.Start.Ticks; t < clip.End.Ticks; t += 1 + (clip.Duration.Ticks / 200))
            {
                seen.Add((ClipTimeMap.Of(c).Source(Time.FromTicks(t)) - ClipTimeMap.Of(r).ExtendedSource(Time.FromTicks(t))).Ticks);
            }

            bool ok = offset is { } value && back == -value && (moved || (seen.Count == 1 && seen.Min == value.Ticks));
            if (!ok)
            {
                failures.Add($"{(reverse ? "reversa" : "adelante")} movido={moved}: ref {reference} clip {clip}; visto [{string.Join(", ", seen)}], medido {(offset is { } o ? $"{o.Ticks}" : "null")}");
            }

            samePhase += moved ? 0 : 1;
            otherPhase += moved ? 1 : 0;
        }

        output.WriteLine($"misma fase: {samePhase}; otra fase: {otherPhase}");
        Assert.True(failures.Count == 0, $"{failures.Count} discrepancias:\n{string.Join("\n", failures)}");
    }

    /// <summary>
    /// Una pareja a la misma velocidad: la mitad de las veces el clip sale de la referencia por operaciones que conservan el
    /// recorrido (recortar y dividir; con Offset distinto) y luego, quizás, se corre o se desliza unos ticks o cuadros; la
    /// otra mitad, todo al azar.
    /// </summary>
    private static (Clip Reference, Clip Clip) MakePair(Random random, Rational rate, bool reverse)
    {
        ClipSpeed Speed() => new()
        {
            Rate = rate, Reverse = reverse, Offset = Time.FromTicks(random.Next(-3000, 3000)),
            PreservePitch = random.Next(2) == 0,
        };

        long Anchor() => reverse ? 50_000_000 + random.Next(1_000_000) : random.Next(1_000_000);
        ClipSpeed referenceSpeed = Speed();
        ClipTimeMap reference = ClipTimeMap.Create(Time.FromTicks(random.Next(0, 5000)), Time.FromTicks(random.Next(100, 3000)),
            Time.FromTicks(Anchor()), referenceSpeed);
        ClipTimeMap clip;
        if (random.Next(2) == 0)
        {
            clip = reference;
            long length = reference.Duration.Ticks;
            if (length > 2 && random.Next(2) == 0)
            {
                clip = clip.TrimIn(clip.Start + Time.FromTicks(1 + random.NextInt64(length / 2)));
            }

            if (clip.Duration.Ticks > 2 && random.Next(2) == 0)
            {
                clip = clip.TrimOut(clip.End - Time.FromTicks(random.NextInt64(clip.Duration.Ticks / 2)));
            }

            if (clip.Duration.Ticks > 2 && random.Next(3) == 0)
            {
                (ClipTimeMap left, ClipTimeMap right) = clip.SplitAt(clip.Start + Time.FromTicks(1 + random.NextInt64(clip.Duration.Ticks - 1)));
                clip = random.Next(2) == 0 ? left : right;
            }

            long Small() => random.Next(3) switch { 0 => random.Next(-3, 4), 1 => random.Next(-50, 51), _ => random.Next(-2, 3) * 1000 };
            switch (random.Next(4))
            {
                case 0:
                    clip = clip.MoveTo(clip.Start + Time.FromTicks(Small()));
                    break;
                case 1:
                    clip = clip.Slip(Time.FromTicks(Small()));
                    break;
                case 2:
                    clip = clip.MoveTo(clip.Start + Time.FromTicks(Small())).Slip(Time.FromTicks(Small()));
                    break;
            }
        }
        else
        {
            clip = ClipTimeMap.Create(reference.Start + Time.FromTicks(random.Next(-1500, 1500)), Time.FromTicks(random.Next(100, 3000)),
                Time.FromTicks(Anchor()), Speed());
        }

        ClipSpeed clipSpeed = Speed();
        clipSpeed.Offset = clip.Offset;
        return (Build("r", reference, referenceSpeed), Build("c", clip, clipSpeed));
    }

    private static Clip Build(string id, ClipTimeMap map, ClipSpeed speed)
    {
        var clip = new Clip { Id = id, MediaId = "m", LinkId = "l", Speed = speed };
        map.ApplyTo(clip);
        return clip;
    }
}
