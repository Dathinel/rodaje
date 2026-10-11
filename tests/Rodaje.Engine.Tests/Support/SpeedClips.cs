using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Engine.Tests.Support;

/// <summary>Clips con velocidad para las pruebas y el cuadro que deben mostrar, calculado aparte con enteros.</summary>
internal static class SpeedClips
{
    /// <summary>Los casos de seek exacto de la fase 5c; «congelado» es un cuadro fijo.</summary>
    public static readonly string[] Cases = ["0.25", "0.5", "1.37", "2", "4", "1001/1000", "100", "congelado"];

    public static Rational Parse(string name) => name switch
    {
        "0.25" => new Rational(1, 4),
        "0.5" => new Rational(1, 2),
        "1.37" => new Rational(137, 100),
        "1001/1000" => new Rational(1001, 1000),
        _ => new Rational(long.Parse(name, System.Globalization.CultureInfo.InvariantCulture), 1),
    };

    /// <summary>Pone en <paramref name="clip"/> una velocidad constante que recorre <c>[sourceIn, sourceOut)</c> desde <paramref name="offset"/>.</summary>
    public static Clip WithRate(Clip clip, Rational rate, Time sourceIn, Time sourceOut, Time offset = default)
    {
        clip.Speed = new ClipSpeed { Rate = rate, Offset = offset };
        ClipTimeMap.Fit(clip.Start, sourceIn, sourceOut, clip.Speed).ApplyTo(clip);
        return clip;
    }

    /// <summary>Congela el cuadro de <paramref name="at"/> durante <paramref name="duration"/>.</summary>
    public static Clip Frozen(Clip clip, Time at, Time duration)
    {
        clip.Speed = new ClipSpeed { Freeze = true };
        ClipTimeMap.Create(clip.Start, duration, at, clip.Speed).ApplyTo(clip);
        return clip;
    }

    /// <summary>
    /// Cuadro del origen que muestra <paramref name="clip"/> en <paramref name="timeline"/> (dentro del clip), sin pasar por
    /// <see cref="ClipTimeMap"/>: <c>SourceIn + ⌊(offset + τ)·r⌋ − ⌊offset·r⌋</c>, o <c>SourceIn</c> congelado.
    /// </summary>
    public static long Expected(Clip clip, Rational sourceRate, Time timeline)
    {
        ClipSpeed speed = clip.Speed!;
        Int128 source = clip.SourceIn.Ticks;
        if (!speed.Freeze)
        {
            Int128 tau = (Int128)timeline.Ticks - clip.Start.Ticks;
            Int128 offset = speed.Offset.Ticks;
            source += Floor((offset + tau) * speed.Rate.Num, speed.Rate.Den) - Floor(offset * speed.Rate.Num, speed.Rate.Den);
        }

        return (long)Floor(source * sourceRate.Num, (Int128)Time.TicksPerSecond * sourceRate.Den);
    }

    private static Int128 Floor(Int128 num, Int128 den)
    {
        Int128 q = Int128.DivRem(num, den).Quotient;
        return (num % den != 0) && ((num < 0) != (den < 0)) ? q - 1 : q;
    }
}
