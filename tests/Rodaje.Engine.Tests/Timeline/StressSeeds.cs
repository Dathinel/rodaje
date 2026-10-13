namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Números de las pruebas de estrés que dependen solo de la semilla: con la misma semilla salen los mismos en cada
/// corrida, así que el «Semilla N» de un mensaje de fallo reproduce la corrida. <see cref="Random.Shared"/> no se repite
/// nunca y <see cref="HashCode.Combine{T1, T2}"/> cambia de una ejecución del proceso a otra (su semilla es aleatoria).
/// </summary>
internal static class StressSeeds
{
    /// <summary>Mezcla los valores con SplitMix64, en orden: el mismo resultado en cualquier proceso y plataforma.</summary>
    public static ulong Mix(params ReadOnlySpan<long> values)
    {
        ulong state = 0x9E3779B97F4A7C15;
        foreach (long value in values)
        {
            state = Step(state ^ unchecked((ulong)value));
        }

        return state;
    }

    /// <summary>Semilla de un <see cref="Random"/> derivada de los valores.</summary>
    public static int Seed(params ReadOnlySpan<long> values) => (int)(Mix(values) & int.MaxValue);

    /// <summary>
    /// Cuadro rápido (1 ms) o lento (2 minutos), mitad y mitad, según la semilla, la vuelta y el índice del cuadro: el
    /// mismo cuadro tarda siempre lo mismo con la misma semilla.
    /// </summary>
    public static TimeSpan SlowOrFast(int seed, int round, long index) =>
        (Mix(seed, round, index) & 1) == 0 ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromMinutes(2);

    /// <summary>De 0 a 2 ms según la semilla y el índice del cuadro.</summary>
    public static TimeSpan ShortDelay(int seed, long index) => TimeSpan.FromMilliseconds((double)(Mix(seed, index) % 3));

    private static ulong Step(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EB;
            return x ^ (x >> 31);
        }
    }
}
