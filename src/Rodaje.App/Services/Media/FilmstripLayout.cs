using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Media;

/// <summary>
/// Dónde caen las miniaturas de una tira y de qué alto se hacen. Los instantes se ajustan a una rejilla de potencias de
/// dos (1/32 s, 1/16 s…, según la separación), así al hacer zoom o desplazar la línea de tiempo la mayoría de las
/// miniaturas ya están en la caché: la rejilla de un zoom contiene a la del zoom más lejano.
/// </summary>
public static class FilmstripLayout
{
    /// <summary>Paso mínimo de la rejilla: 1/32 s (alrededor de un cuadro a 30 fps).</summary>
    public static readonly Time BaseStep = Time.FromTicks(Time.TicksPerSecond / 32);

    /// <summary>Altos en que se guardan las miniaturas; un pedido se redondea hacia arriba al siguiente.</summary>
    public static IReadOnlyList<int> HeightClasses { get; } = [32, 48, 64, 96, 128, 192, 256];

    public const int MaxCount = 512;

    public static int HeightClass(int height)
    {
        foreach (int h in HeightClasses)
        {
            if (h >= height)
            {
                return h;
            }
        }

        return HeightClasses[^1];
    }

    /// <summary>Paso de la rejilla para una separación entre miniaturas: la mayor potencia de dos del paso base que no la supera.</summary>
    public static Time GridStep(Time spacing)
    {
        long step = BaseStep.Ticks;
        while (step * 2 <= spacing.Ticks && step < long.MaxValue / 4)
        {
            step *= 2;
        }

        return Time.FromTicks(step);
    }

    /// <summary>Lleva un instante al punto de la rejilla más cercano.</summary>
    public static Time Snap(Time time, Time step)
    {
        long s = Math.Max(1, step.Ticks);
        long q = Math.DivRem(time.Ticks, s, out long r);
        if (r < 0)
        {
            q--;
            r += s;
        }

        return Time.FromTicks((r * 2 >= s ? q + 1 : q) * s);
    }

    /// <summary>
    /// Instantes de una tira de <paramref name="count"/> miniaturas sobre <c>[start, end)</c>: el centro de cada hueco,
    /// ajustado a la rejilla y sin salir del tramo.
    /// </summary>
    public static Time[] Times(Time start, Time end, int count)
    {
        count = Math.Clamp(count, 0, MaxCount);
        if (count == 0 || end <= start)
        {
            return [];
        }

        long length = end.Ticks - start.Ticks;
        Time step = GridStep(Time.FromTicks(length / count));
        var times = new Time[count];
        for (int i = 0; i < count; i++)
        {
            long center = start.Ticks + (long)(((Int128)length * ((2 * i) + 1)) / (2 * count));
            Time snapped = Snap(Time.FromTicks(center), step);
            times[i] = Time.FromTicks(Math.Clamp(snapped.Ticks, start.Ticks, end.Ticks - 1));
        }

        return times;
    }
}
