using System.Globalization;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// Cuánto crece el trabajo de una operación al multiplicar su tamaño, sin reloj: se cuentan las operaciones que la
/// costura de prueba suma (por ejemplo <c>Subclip.KeyReads</c>: una búsqueda lineal lee la clave de cada elemento) y los
/// bytes que reserva el hilo (<see cref="GC.GetAllocatedBytesForCurrentThread"/>). Las dos son cuentas exactas que no
/// dependen de la máquina.
/// <para>
/// Antes también se medía el tiempo sin las pausas del recolector, y «Subclips repetidos» siguió fallando en la CI de
/// Windows: ×143 de 4 000 a 32 000 (4,0 ms y 571,1 ms) con los bytes en ×7,6, es decir, con una carga lineal. Restar las
/// pausas no basta: no cuentan las esperas del hilo que reserva mientras corre una recolección en segundo plano (la
/// corrida grande reserva en el montón de objetos grandes, la pequeña no) ni el tiempo que el hilo pasa sin CPU con el
/// runner lleno (la corrida pequeña cabe en un turno del planificador; la grande espera a los demás hilos en cada turno).
/// En Linux, con cuatro hilos reservando bloques de 100 kB a la vez, la misma carga dio ×8 a ×13 con 0 ms de pausas; con
/// 32 hilos girando, hasta ×16.
/// </para>
/// </summary>
internal static class WorkGrowth
{
    /// <summary>
    /// Falla si multiplicar el tamaño por <c>large / small</c> multiplica las operaciones de <paramref name="run"/> (lo que
    /// avanza <paramref name="operations"/>, un contador del hilo) por más del doble de eso, o sus bytes reservados por más
    /// del cuádruple: lineal da el factor; cuadrático, el factor al cuadrado. <paramref name="prepare"/> arma la entrada
    /// fuera de la medida. También falla si la corrida pequeña no cuenta ninguna operación (la costura no está conectada).
    /// </summary>
    public static void AssertLinear<T>(Func<int, T> prepare, Action<T> run, Func<long> operations, int small, int large, string what)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(operations);
        T smallInput = prepare(small);
        T largeInput = prepare(large);

        // Una vuelta de cada tamaño antes de medir: la primera reserva lo que el entorno prepara una sola vez.
        run(smallInput);
        run(largeInput);
        Work bestSmall = Work.Worst, bestLarge = Work.Worst;
        for (int i = 0; i < 3; i++)
        {
            bestSmall = Work.Best(bestSmall, MeasureWork(run, smallInput, operations));
            bestLarge = Work.Best(bestLarge, MeasureWork(run, largeInput, operations));
        }

        Assert.True(bestSmall.Operations > 0, $"{what}: la corrida de {small} no contó ninguna operación; la costura de prueba no está conectada.");
        double factor = large / (double)small;
        double ops = bestLarge.Operations / (double)bestSmall.Operations;
        double bytes = bestLarge.Bytes / (double)Math.Max(1, bestSmall.Bytes);
        Assert.True(ops < 2 * factor && bytes < 4 * factor, string.Create(CultureInfo.InvariantCulture,
            $"{what}: de {small} a {large} (lineal: {factor:0.#}) las operaciones se multiplicaron por {ops:0.0} " +
            $"({bestSmall.Operations} y {bestLarge.Operations}) y los bytes reservados por {bytes:0.0} " +
            $"({bestSmall.Bytes} y {bestLarge.Bytes})."));
    }

    /// <summary>Operaciones contadas y bytes que reservó el hilo en una corrida de <paramref name="run"/>.</summary>
    public static (long Operations, long Bytes) Measure<T>(Action<T> run, T input, Func<long> operations)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(operations);
        Work work = MeasureWork(run, input, operations);
        return (work.Operations, work.Bytes);
    }

    private static Work MeasureWork<T>(Action<T> run, T input, Func<long> operations)
    {
        long operationsBefore = operations();
        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        run(input);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        return new Work(operations() - operationsBefore, bytes);
    }

    private readonly record struct Work(long Operations, long Bytes)
    {
        public static Work Worst => new(long.MaxValue, long.MaxValue);

        // Las dos son cuentas exactas (salvo lo que reserve el entorno de vez en cuando): el mínimo de cada una.
        public static Work Best(Work a, Work b) => new(Math.Min(a.Operations, b.Operations), Math.Min(a.Bytes, b.Bytes));
    }
}
