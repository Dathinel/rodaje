using System.Collections.Concurrent;
using System.Diagnostics;

namespace Rodaje.Playback.Tests.Support;

/// <summary>
/// Arnés de las pruebas de estrés (<c>Categoria=Estres</c>): varios hilos que se pisan con una semilla fija, un tope de
/// tiempo para la CI y la semilla en cada mensaje de fallo, para repetir la corrida exacta.
/// </summary>
/// <remarks>
/// Sin <c>RODAJE_PRUEBAS_LARGAS</c> cada prueba hace pocas iteraciones (la CI); con ella, <see cref="LongFactor"/> veces
/// más. Un hilo que no termina antes del tope cuenta como interbloqueo y la prueba falla con la semilla y el hilo.
/// </remarks>
internal static class Stress
{
    /// <summary>Cuántas veces más iteraciones con <c>RODAJE_PRUEBAS_LARGAS=1</c>.</summary>
    public const int LongFactor = 20;

    public static bool Long => Environment.GetEnvironmentVariable("RODAJE_PRUEBAS_LARGAS") == "1";

    /// <summary>Tope de cada corrida: lo que tarde de más es un interbloqueo (o un hilo colgado).</summary>
    public static TimeSpan Deadline => TimeSpan.FromSeconds(Long ? 600 : 60);

    /// <summary>Las iteraciones de la CI, multiplicadas por <see cref="LongFactor"/> con las pruebas largas.</summary>
    public static int Iterations(int ci) => Long ? ci * LongFactor : ci;

    /// <summary>
    /// Corre <paramref name="workers"/> en hilos propios hasta que todos vuelven, juntando sus excepciones. Cada uno
    /// recibe su propio <see cref="Random"/> derivado de la semilla y una señal de parada que se enciende si otro falla.
    /// </summary>
    public static void Run(int seed, params (string Name, Action<Random, CancellationToken> Body)[] workers)
    {
        var errors = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();
        var threads = new List<(string Name, Thread Thread)>();
        for (int i = 0; i < workers.Length; i++)
        {
            (string name, Action<Random, CancellationToken> body) = workers[i];
            var random = new Random(HashCode.Combine(seed, i));
            var thread = new Thread(() =>
            {
                try
                {
                    body(random, stop.Token);
                }
#pragma warning disable CA1031 // Se junta cualquier excepción para fallar con la semilla.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    errors.Enqueue($"[{name}] {ex}");
                    stop.Cancel();
                }
            })
            { IsBackground = true, Name = "Estrés " + name };
            threads.Add((name, thread));
        }

        threads.ForEach(t => t.Thread.Start());
        var watch = Stopwatch.StartNew();
        var hung = new List<string>();
        foreach ((string name, Thread thread) in threads)
        {
            TimeSpan left = Deadline - watch.Elapsed;
            if (!thread.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero))
            {
                hung.Add(name);
            }
        }

        if (hung.Count > 0)
        {
            stop.Cancel();
            errors.Enqueue($"Posible interbloqueo: {string.Join(", ", hung)} no terminó en {Deadline.TotalSeconds:0} s.");
        }

        Assert.True(errors.IsEmpty, $"Semilla {seed}: {string.Join(Environment.NewLine, errors)}");
    }

    /// <summary>Corre <paramref name="action"/> en otro hilo y falla con la semilla si no vuelve antes de <paramref name="limit"/>.</summary>
    public static void Within(int seed, string what, TimeSpan limit, Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
#pragma warning disable CA1031 // Se relanza en el hilo de la prueba.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                error = ex;
            }
        })
        { IsBackground = true, Name = "Estrés " + what };
        thread.Start();
        Assert.True(thread.Join(limit), $"Semilla {seed}: {what} no terminó en {limit.TotalSeconds:0.#} s (posible interbloqueo).");
        Assert.True(error is null, $"Semilla {seed}: {what} lanzó {error}");
    }

    /// <summary>Espera a que se cumpla la condición; falla con la semilla si no llega antes de <paramref name="limit"/>.</summary>
    public static void WaitUntil(int seed, string what, Func<bool> condition, TimeSpan? limit = null)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan max = limit ?? TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(watch.Elapsed < max, $"Semilla {seed}: {what} no ocurrió en {max.TotalSeconds:0.#} s.");
            Thread.Sleep(1);
        }
    }
}
