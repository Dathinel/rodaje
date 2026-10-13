using System.Diagnostics;
using System.Globalization;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// Como <see cref="Support.Growth"/>, pero sin contar las pausas del recolector, y contando además los bytes que reserva el
/// hilo. «Subclips repetidos» dio ×174 y ×230 en la CI de Windows (2,7 ms y 473,5 ms) con una carga lineal: la corrida de
/// 4 000 cabe en la reserva joven del recolector y casi nunca lo despierta, y la de 32 000 reserva unos 12 MB y lo despierta
/// siempre. Lo que cuesta esa recolección depende del proceso entero (el montón de las 1 700 pruebas de
/// <c>Rodaje.App.Tests</c>, los hilos que hay que detener con el runner cargado), no del tamaño de la entrada: en Linux, la
/// misma corrida de 4 000 pasa de 11 ms a 130 ms cuando le toca recolectar. Medido así, cualquier carga que reserve memoria
/// parecía cuadrática en cuanto la grande pasaba del umbral.
/// <para>
/// Por eso se miden dos cosas que no dependen del recolector: los bytes que reserva el hilo
/// (<see cref="GC.GetAllocatedBytesForCurrentThread"/>, una cuenta exacta) y el reloj menos las pausas del recolector
/// (<see cref="GC.GetTotalPauseDuration"/>), con el mejor de cinco.
/// </para>
/// </summary>
internal static class WorkGrowth
{
    /// <summary>
    /// Falla si multiplicar el tamaño por <c>large / small</c> multiplica los bytes reservados o el tiempo sin pausas de
    /// <paramref name="run"/> por más del cuádruple de eso: lineal da el factor; cuadrático, el factor al cuadrado.
    /// <paramref name="prepare"/> arma la entrada fuera de la medida.
    /// </summary>
    public static void AssertLinear<T>(Func<int, T> prepare, Action<T> run, int small, int large, string what)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(run);
        T smallInput = prepare(small);
        T largeInput = prepare(large);

        // Una vuelta de cada tamaño antes de medir: la primera corre con el código sin optimizar (compilación por niveles).
        run(smallInput);
        run(largeInput);
        Work bestSmall = Work.Worst, bestLarge = Work.Worst;
        for (int i = 0; i < 5; i++)
        {
            bestSmall = Work.Best(bestSmall, Measure(run, smallInput));
            bestLarge = Work.Best(bestLarge, Measure(run, largeInput));
        }

        double factor = large / (double)small;
        double bytes = bestLarge.Bytes / (double)Math.Max(1, bestSmall.Bytes);
        double time = bestLarge.Ticks / (double)Math.Max(1, bestSmall.Ticks);
        Assert.True(bytes < 4 * factor && time < 4 * factor, string.Create(CultureInfo.InvariantCulture,
            $"{what}: de {small} a {large} (lineal: {factor:0.#}) los bytes reservados se multiplicaron por {bytes:0.0} " +
            $"({bestSmall.Bytes} y {bestLarge.Bytes}) y el tiempo sin pausas del recolector por {time:0.0} " +
            $"({Milliseconds(bestSmall.Ticks):0.0} ms y {Milliseconds(bestLarge.Ticks):0.0} ms)."));
    }

    /// <summary>Bytes que reservó el hilo y tiempo, sin las pausas del recolector, de una corrida de <paramref name="run"/>.</summary>
    public static (long Bytes, TimeSpan Time) Measure<T>(Action<T> run, T input, out TimeSpan pauses)
    {
        ArgumentNullException.ThrowIfNull(run);
        Work work = MeasureWork(run, input, out long pauseTicks);
        pauses = Stopwatch.GetElapsedTime(0, pauseTicks);
        return (work.Bytes, Stopwatch.GetElapsedTime(0, work.Ticks));
    }

    private static Work Measure<T>(Action<T> run, T input) => MeasureWork(run, input, out _);

    private static Work MeasureWork<T>(Action<T> run, T input, out long pauseTicks)
    {
        // Sin recolecciones forzadas antes de medir: detendrían a las demás pruebas del proceso, y las pausas ya se restan.
        TimeSpan pausedBefore = GC.GetTotalPauseDuration();
        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        run(input);
        long elapsed = Stopwatch.GetTimestamp() - start;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        pauseTicks = (long)((GC.GetTotalPauseDuration() - pausedBefore).TotalSeconds * Stopwatch.Frequency);
        return new Work(bytes, Math.Max(0, elapsed - pauseTicks));
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private readonly record struct Work(long Bytes, long Ticks)
    {
        public static Work Worst => new(long.MaxValue, long.MaxValue);

        // Los bytes son una cuenta exacta (salvo lo que reserve el entorno de vez en cuando): también el mínimo.
        public static Work Best(Work a, Work b) => new(Math.Min(a.Bytes, b.Bytes), Math.Min(a.Ticks, b.Ticks));
    }
}
