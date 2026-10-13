namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Las semillas de las pruebas de estrés reproducen: los retardos de cada cuadro dependen solo de la semilla. Antes,
/// <c>Closing_during_any_long_operation_returns_promptly_and_releases_everything</c> sorteaba los cuadros lentos con
/// <see cref="Random.Shared"/> y su mensaje de fallo citaba una semilla que no repetía la corrida.
/// </summary>
public sealed class StressSeedsTests
{
    /// <summary>La misma semilla da los mismos retardos, cuadro por cuadro, y otra semilla da otros.</summary>
    [Fact]
    public void The_same_seed_gives_the_same_delays()
    {
        TimeSpan[] first = [.. Enumerable.Range(0, 500).Select(i => StressSeeds.SlowOrFast(7, 3, i))];
        TimeSpan[] again = [.. Enumerable.Range(0, 500).Select(i => StressSeeds.SlowOrFast(7, 3, i))];
        TimeSpan[] other = [.. Enumerable.Range(0, 500).Select(i => StressSeeds.SlowOrFast(8, 3, i))];
        TimeSpan[] nextRound = [.. Enumerable.Range(0, 500).Select(i => StressSeeds.SlowOrFast(7, 4, i))];

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.NotEqual(first, nextRound);

        // Mitad y mitad, más o menos: la prueba sigue mezclando cuadros lentos y rápidos.
        int slow = first.Count(d => d > TimeSpan.FromSeconds(1));
        Assert.InRange(slow, 200, 300);
    }

    /// <summary>
    /// Valores fijados: no cambian de una ejecución del proceso a otra (con <see cref="HashCode.Combine{T1, T2}"/> sí
    /// cambiaban, porque su semilla es aleatoria por proceso), así que una semilla citada en la CI se repite en otra máquina.
    /// </summary>
    [Fact]
    public void The_delays_are_the_same_in_every_process()
    {
        string pattern = string.Concat(Enumerable.Range(0, 16).Select(i => StressSeeds.SlowOrFast(7, 0, i) > TimeSpan.FromSeconds(1) ? 'L' : 'r'));
        Assert.Equal("LLLrrrrrrLrLLLrr", pattern);

        int[] quick = [.. Enumerable.Range(0, 12).Select(i => (int)StressSeeds.ShortDelay(101, i).TotalMilliseconds)];
        Assert.Equal([1, 0, 1, 0, 1, 1, 0, 2, 1, 0, 0, 0], quick);

        Assert.Equal(470_900_616, StressSeeds.Seed(7, 0));
    }
}
