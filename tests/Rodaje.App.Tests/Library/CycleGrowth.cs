using System.Globalization;
using System.Runtime.InteropServices;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// Como <see cref="Support.Growth"/>, pero contando los ciclos de procesador que gasta el hilo de la prueba
/// (<c>QueryThreadCycleTime</c>) en lugar del reloj de pared: lo que el hilo pasa sin correr (esperando su turno con el
/// runner cargado) no cuenta. En la CI de Windows, con cuatro procesos de prueba a la vez, «Subclips repetidos» dio ×230
/// de 4 000 a 32 000 por reloj (5,5 ms y 1 255,7 ms) con una carga lineal: la corrida pequeña cabe en un turno del
/// planificador y la grande no (Windows Server reparte turnos largos), así que se mide la espera, no el trabajo. Los ciclos
/// del hilo crecen como el trabajo; incluyen la recolección que corre en el mismo hilo.
/// </summary>
internal static partial class CycleGrowth
{
    /// <summary>
    /// Falla si multiplicar el tamaño por <c>large / small</c> multiplica los ciclos de <paramref name="run"/> por más del
    /// cuádruple de eso: lineal da el factor; cuadrático, el factor al cuadrado. <paramref name="prepare"/> arma la
    /// entrada fuera de la cuenta.
    /// </summary>
    public static void AssertLinear<T>(Func<int, T> prepare, Action<T> run, int small, int large, string what)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(run);
        T smallInput = prepare(small);
        T largeInput = prepare(large);

        // Una vuelta de cada tamaño antes de contar: la primera corre con el código sin optimizar.
        run(smallInput);
        run(largeInput);
        ulong bestSmall = ulong.MaxValue, bestLarge = ulong.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            bestSmall = Math.Min(bestSmall, Cycles(run, smallInput));
            bestLarge = Math.Min(bestLarge, Cycles(run, largeInput));
        }

        double factor = large / (double)small;
        double ratio = bestLarge / (double)Math.Max(1UL, bestSmall);
        Assert.True(ratio < 4 * factor, string.Create(CultureInfo.InvariantCulture,
            $"{what}: de {small} a {large} los ciclos del hilo se multiplicaron por {ratio:0.0} (lineal: {factor:0.#}; " +
            $"{bestSmall} y {bestLarge} ciclos)."));
    }

    /// <summary>Ciclos de procesador que gastó el hilo actual en <paramref name="run"/>.</summary>
    public static ulong Cycles<T>(Action<T> run, T input)
    {
        ArgumentNullException.ThrowIfNull(run);
        ulong start = ThreadCycles();
        run(input);
        return ThreadCycles() - start;
    }

    private static ulong ThreadCycles()
    {
        if (!QueryThreadCycleTime(GetCurrentThread(), out ulong cycles))
        {
            throw new InvalidOperationException("QueryThreadCycleTime falló: " + Marshal.GetLastPInvokeError());
        }

        return cycles;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryThreadCycleTime(nint thread, out ulong cycles);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetCurrentThread();
}
