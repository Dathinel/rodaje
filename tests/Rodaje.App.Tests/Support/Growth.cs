using System.Diagnostics;
using System.Globalization;

namespace Rodaje.App.Tests.Support;

/// <summary>
/// Cuánto crece el tiempo de una operación al multiplicar su tamaño, sin depender de la carga de la máquina: se alternan
/// las corridas pequeña y grande y se toma el mejor tiempo de cada una (una pausa del runner solo puede alargar una
/// corrida, nunca acortarla). Para las pruebas de la CI que antes miraban un tope de reloj: en Windows, con todos los
/// proyectos de prueba a la vez, una carga de 0,3 s llegó a tardar 33,7 s, pero lo lineal sigue creciendo como lo lineal.
/// </summary>
internal static class Growth
{
    /// <summary>
    /// Proporción entre el mejor tiempo de <paramref name="run"/> con <paramref name="large"/> y con
    /// <paramref name="small"/>. Lineal da cerca de <c>large / small</c>; cuadrático, su cuadrado.
    /// </summary>
    public static double Ratio(Action<int> run, int small, int large, int rounds = 5)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Una vuelta de cada tamaño antes de medir: la primera corre con el código sin optimizar (compilación por niveles).
        run(small);
        run(large);
        long bestSmall = long.MaxValue, bestLarge = long.MaxValue;
        for (int i = 0; i < rounds; i++)
        {
            bestSmall = Math.Min(bestSmall, Time(run, small));
            bestLarge = Math.Min(bestLarge, Time(run, large));
        }

        return bestLarge / (double)Math.Max(1, bestSmall);
    }

    /// <summary>
    /// Falla si multiplicar el tamaño por <c>large / small</c> (8 en las pruebas) multiplica el tiempo por más del cuádruple
    /// de eso (32): lineal da el factor, o algo más por la caché y el recolector (hasta 2,5 veces el factor en Linux);
    /// cuadrático, el factor al cuadrado (64), y los defectos que vigilan estas pruebas eran cuadráticos o cúbicos.
    /// </summary>
    public static void AssertLinear(Action<int> run, int small, int large, string what)
    {
        double factor = large / (double)small;
        double ratio = Ratio(run, small, large);
        Assert.True(ratio < 4 * factor, string.Create(CultureInfo.InvariantCulture,
            $"{what}: de {small} a {large} el tiempo se multiplicó por {ratio:0.0} (lineal: {factor:0.#})."));
    }

    private static long Time(Action<int> run, int size)
    {
        long start = Stopwatch.GetTimestamp();
        run(size);
        return Stopwatch.GetTimestamp() - start;
    }
}
