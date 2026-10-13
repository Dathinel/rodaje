using System.Diagnostics;
using System.Globalization;

namespace Rodaje.App.Tests.Support;

/// <summary>
/// Cuánto crece el tiempo de una operación al multiplicar su tamaño, sin depender de la carga de la máquina: se alternan
/// las corridas pequeña y grande y se toma el mejor tiempo de cada una (una pausa del runner solo puede alargar una
/// corrida, nunca acortarla). Para las pruebas de la CI que antes miraban un tope de reloj: en Windows, con todos los
/// proyectos de prueba a la vez, una carga de 0,3 s llegó a tardar 33,7 s, pero lo lineal sigue creciendo como lo lineal.
/// Lo que se mide no toca el disco: en el runner de Windows, escribir y volver a leer el archivo de la biblioteca dio
/// ×178 de 1 000 a 8 000 carpetas (en Linux, ×10), mientras la misma carga en memoria (ids sueltos) crecía como lo lineal;
/// lo más probable es el antivirus del runner al leer un archivo recién escrito.
/// </summary>
internal static class Growth
{
    /// <summary>
    /// Falla si multiplicar el tamaño por <c>large / small</c> (8 en las pruebas) multiplica el tiempo de
    /// <paramref name="run"/> por más del cuádruple de eso (32): lineal da el factor, o algo más por la caché y el
    /// recolector (hasta 2,5 veces el factor en Linux); cuadrático, el factor al cuadrado (64), y los defectos que vigilan
    /// estas pruebas eran cuadráticos o cúbicos. <paramref name="prepare"/> arma la entrada fuera de la medida.
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
        long bestSmall = long.MaxValue, bestLarge = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            bestSmall = Math.Min(bestSmall, Time(run, smallInput));
            bestLarge = Math.Min(bestLarge, Time(run, largeInput));
        }

        double factor = large / (double)small;
        double ratio = bestLarge / (double)Math.Max(1, bestSmall);
        Assert.True(ratio < 4 * factor, string.Create(CultureInfo.InvariantCulture,
            $"{what}: de {small} a {large} el tiempo se multiplicó por {ratio:0.0} (lineal: {factor:0.#}; " +
            $"{Milliseconds(bestSmall):0.0} ms y {Milliseconds(bestLarge):0.0} ms)."));
    }

    /// <summary>Como la otra, cuando no hay nada que preparar.</summary>
    public static void AssertLinear(Action<int> run, int small, int large, string what) =>
        AssertLinear(static size => size, run, small, large, what);

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static long Time<T>(Action<T> run, T input)
    {
        long start = Stopwatch.GetTimestamp();
        run(input);
        return Stopwatch.GetTimestamp() - start;
    }
}
