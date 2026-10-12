namespace Rodaje.Analysis.Ducking;

/// <summary>Ajustes del ducking. Los tiempos van en segundos.</summary>
public sealed record DuckingOptions
{
    /// <summary>Lo que baja la música bajo la voz, en dB (negativo).</summary>
    public double ReductionDb { get; init; } = -12;

    /// <summary>La música llega a la reducción completa este tiempo antes de la voz; nunca menos de 30 ms.</summary>
    public double Anticipation { get; init; } = 0.1;

    /// <summary>Duración de la bajada.</summary>
    public double Attack { get; init; } = 0.15;

    /// <summary>Tiempo que la música sigue baja tras la voz.</summary>
    public double Hold { get; init; } = 0.15;

    /// <summary>Duración de la subida (relajación).</summary>
    public double Release { get; init; } = 0.5;

    public const double MinimumAnticipation = 0.03;
}

/// <summary>Punto de la envolvente de ganancia: desplazamiento en dB respecto del volumen que ya tenía la música.</summary>
public readonly record struct GainPoint(double Time, double OffsetDb);

/// <summary>Convierte los tramos de voz en la envolvente de la música: bajada anticipada, retención y relajación.</summary>
public static class DuckingPlanner
{
    public static List<GainPoint> Plan(IEnumerable<TimeRange> voice, DuckingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(voice);
        options ??= new DuckingOptions();
        double reduction = Math.Min(0, options.ReductionDb);
        double anticipation = Math.Max(DuckingOptions.MinimumAnticipation, options.Anticipation);
        double attack = Math.Max(0.001, options.Attack);
        double hold = Math.Max(0, options.Hold);
        double release = Math.Max(0.001, options.Release);
        var points = new List<GainPoint>();
        if (reduction == 0)
        {
            return points;
        }

        // Dos voces tan juntas que la música no alcanzaría a subir y bajar se tratan como una sola.
        List<TimeRange> ducked = TimeRange.Merge(
            voice.Select(v => new TimeRange(v.Start - anticipation, v.End + hold)),
            attack + release);
        foreach (TimeRange d in ducked)
        {
            points.Add(new(d.Start - attack, 0));
            points.Add(new(d.Start, reduction));
            points.Add(new(d.End, reduction));
            points.Add(new(d.End + release, 0));
        }

        return points;
    }

    /// <summary>Desplazamiento en <paramref name="time"/> de la envolvente (lineal entre puntos, 0 fuera).</summary>
    public static double OffsetAt(IReadOnlyList<GainPoint> points, double time)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0 || time <= points[0].Time || time >= points[^1].Time)
        {
            return 0;
        }

        for (int i = 1; i < points.Count; i++)
        {
            if (time <= points[i].Time)
            {
                GainPoint a = points[i - 1], b = points[i];
                double span = b.Time - a.Time;
                return span <= 0 ? b.OffsetDb : a.OffsetDb + ((b.OffsetDb - a.OffsetDb) * (time - a.Time) / span);
            }
        }

        return 0;
    }
}
