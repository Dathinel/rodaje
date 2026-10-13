using System.Collections.Concurrent;
using System.Diagnostics;
using Rodaje.TestMedia;

namespace Rodaje.Engine.Tests.Support;

/// <summary>
/// Arnés de las pruebas de estrés (<c>Categoria=Estres</c>): varios hilos que se pisan con una semilla fija, un
/// presupuesto de tiempo que corta las iteraciones y la semilla en cada mensaje de fallo, para repetir la corrida exacta.
/// </summary>
/// <remarks>
/// <para>
/// Sin <c>RODAJE_PRUEBAS_LARGAS</c> cada prueba hace pocas iteraciones (la CI); con ella, <see cref="LongFactor"/> veces
/// más. Al cumplirse <see cref="Budget"/> se enciende la señal de parada y los hilos dejan de iterar: un runner cargado
/// hace menos vueltas, no falla (en la CI de Windows, con todos los proyectos a la vez, 1 500 pasos de la línea de tiempo
/// llegaron a pasar de 60 s; en Linux tardan menos de uno).
/// </para>
/// <para>
/// Lo que falla es la falta de progreso: cada hilo avanza cada vez que saca un número de su <see cref="Random"/>, y uno
/// que pasa <see cref="Stall"/> sin sacar ninguno cuenta como interbloqueo (o hilo colgado) y la prueba falla con la
/// semilla y el hilo. Por eso cada vuelta de un hilo saca al menos un número.
/// </para>
/// </remarks>
internal static class Stress
{
    /// <summary>Cuántas veces más iteraciones con <c>RODAJE_PRUEBAS_LARGAS=1</c>.</summary>
    public const int LongFactor = 20;

    public static bool Long => Environment.GetEnvironmentVariable(Skip.LongVariable) == "1";

    /// <summary>Tiempo de cada corrida: al cumplirse se pide parar y las iteraciones que falten no se hacen (no es un fallo).</summary>
    public static TimeSpan Budget => TimeSpan.FromSeconds(Long ? 600 : 15);

    /// <summary>
    /// Lo que puede pasar un hilo sin avanzar antes de darlo por colgado. Queda por encima de los topes de
    /// <see cref="Within"/> de las pruebas (20 s), para que un cierre colgado falle con su propio mensaje.
    /// </summary>
    public static TimeSpan Stall => TimeSpan.FromSeconds(Long ? 300 : 60);

    /// <summary>Las iteraciones de la CI, multiplicadas por <see cref="LongFactor"/> con las pruebas largas.</summary>
    public static int Iterations(int ci) => Long ? ci * LongFactor : ci;

    /// <summary>
    /// Corre <paramref name="workers"/> en hilos propios hasta que todos vuelven, juntando sus excepciones. Cada uno
    /// recibe su propio <see cref="Random"/> derivado de la semilla y una señal de parada que se enciende si otro falla o
    /// si se acaba <see cref="Budget"/>.
    /// </summary>
    public static void Run(int seed, params (string Name, Action<Random, CancellationToken> Body)[] workers)
    {
        var errors = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();
        var threads = new List<(string Name, Thread Thread, BeatingRandom Random)>();
        for (int i = 0; i < workers.Length; i++)
        {
            (string name, Action<Random, CancellationToken> body) = workers[i];
            var random = new BeatingRandom(HashCode.Combine(seed, i));
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
            threads.Add((name, thread, random));
        }

        threads.ForEach(t => t.Thread.Start());
        var watch = Stopwatch.StartNew();
        long[] beats = new long[threads.Count];
        var lastBeat = new TimeSpan[threads.Count];
        bool cut = false;
        var hung = new List<string>();
        while (true)
        {
            int alive = threads.FindIndex(t => t.Thread.IsAlive);
            if (alive < 0)
            {
                break;
            }

            threads[alive].Thread.Join(TimeSpan.FromMilliseconds(100));
            TimeSpan now = watch.Elapsed;
            if (!cut && now >= Budget)
            {
                cut = true;
                stop.Cancel();
            }

            for (int i = 0; i < threads.Count; i++)
            {
                long beat = threads[i].Random.Beats;
                if (beat != beats[i] || !threads[i].Thread.IsAlive)
                {
                    beats[i] = beat;
                    lastBeat[i] = now;
                }
                else if (now - lastBeat[i] > Stall)
                {
                    hung.Add($"{threads[i].Name} (sin avanzar desde los {lastBeat[i].TotalSeconds:0} s, tras {beat} números)");
                }
            }

            if (hung.Count > 0)
            {
                stop.Cancel();
                errors.Enqueue($"Posible interbloqueo: {string.Join(", ", hung)}; {Stall.TotalSeconds:0} s sin progreso.");
                break;
            }
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

    /// <summary>Un <see cref="Random"/> que cuenta los números que saca: el pulso con el que <see cref="Run"/> ve que un hilo avanza.</summary>
    private sealed class BeatingRandom(int seed) : Random(seed)
    {
        private long _beats;

        public long Beats => Volatile.Read(ref _beats);

        public override int Next()
        {
            Beat();
            return base.Next();
        }

        public override int Next(int maxValue)
        {
            Beat();
            return base.Next(maxValue);
        }

        public override int Next(int minValue, int maxValue)
        {
            Beat();
            return base.Next(minValue, maxValue);
        }

        public override long NextInt64()
        {
            Beat();
            return base.NextInt64();
        }

        public override long NextInt64(long maxValue)
        {
            Beat();
            return base.NextInt64(maxValue);
        }

        public override long NextInt64(long minValue, long maxValue)
        {
            Beat();
            return base.NextInt64(minValue, maxValue);
        }

        public override double NextDouble()
        {
            Beat();
            return base.NextDouble();
        }

        public override float NextSingle()
        {
            Beat();
            return base.NextSingle();
        }

        public override void NextBytes(byte[] buffer)
        {
            Beat();
            base.NextBytes(buffer);
        }

        public override void NextBytes(Span<byte> buffer)
        {
            Beat();
            base.NextBytes(buffer);
        }

        private void Beat() => Volatile.Write(ref _beats, _beats + 1);
    }
}
