using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Rodaje.TestMedia;
using Rodaje.Engine.Tests.Timeline;

namespace Rodaje.Engine.Tests.Support;

/// <summary>
/// Arnés de las pruebas de estrés (<c>Categoria=Estres</c>): varios hilos que se pisan con una semilla fija, un tope de
/// tiempo para la CI y la semilla en cada mensaje de fallo, para repetir la corrida exacta.
/// </summary>
/// <remarks>
/// Sin <c>RODAJE_PRUEBAS_LARGAS</c> cada prueba hace pocas iteraciones (la CI); con ella, <see cref="LongFactor"/> veces
/// más. Las iteraciones paran también al vencer <see cref="Budget"/>, sin fallar: en la CI de Windows, con todos los
/// proyectos de prueba a la vez y esperas de 1 ms que allí duran 15,6 (la resolución del reloj), una corrida que en Linux
/// tarda medio segundo pasaba de 60 s y se daba por interbloqueo. Interbloqueo es ahora un hilo vivo que no avanza
/// (no saca ningún número de su <see cref="Random"/>) en <see cref="Stall"/>, o la corrida entera pasada de
/// <see cref="Deadline"/>. Cortar por presupuesto no puede dejar la prueba vacía: una corrida en la que ningún hilo sacó
/// un número (cero vueltas) falla, en vez de pasar en verde sin haber probado nada. Y el hilo que recorre sus vueltas con
/// <see cref="Steps"/> hace siempre al menos <see cref="MinimumSteps"/>: el presupuesto no corta antes, y si el hilo sale
/// antes por su cuenta la corrida falla. Al dar con un interbloqueo, el mensaje trae las pilas de todos los hilos
/// (<see cref="ThreadDump"/>), con los trabajadores por su nombre.
/// </remarks>
internal static class Stress
{
    /// <summary>Cuántas veces más iteraciones con <c>RODAJE_PRUEBAS_LARGAS=1</c>.</summary>
    public const int LongFactor = 20;

    public static bool Long => Environment.GetEnvironmentVariable(Skip.LongVariable) == "1";

    /// <summary>Tiempo para iterar: al vencer se enciende la señal de parada y los hilos terminan su vuelta, sin fallar.</summary>
    public static TimeSpan Budget => TimeSpan.FromSeconds(Long ? 300 : 20);

    /// <summary>Un hilo vivo que no saca ningún número de su <see cref="Random"/> en este tiempo está trabado.</summary>
    public static TimeSpan Stall => TimeSpan.FromSeconds(Long ? 120 : 60);

    /// <summary>Tope de cada corrida: lo que tarde de más es un interbloqueo (o un hilo colgado).</summary>
    public static TimeSpan Deadline => TimeSpan.FromSeconds(Long ? 900 : 240);

    /// <summary>Las iteraciones de la CI, multiplicadas por <see cref="LongFactor"/> con las pruebas largas.</summary>
    public static int Iterations(int ci) => Long ? ci * LongFactor : ci;

    /// <summary>
    /// Las vueltas que <see cref="Steps"/> hace siempre de las <paramref name="count"/> pedidas: la cuarta parte de las de la
    /// CI (también con las pruebas largas), y al menos una. El presupuesto solo corta las que pasan de ahí; antes podía
    /// cortar en la primera y la prueba pasaba en verde casi sin haber corrido.
    /// </summary>
    public static int MinimumSteps(int count) => Math.Max(1, (Long ? count / LongFactor : count) / 4);

    /// <summary>
    /// Las vueltas <c>0 … count - 1</c> de un hilo de <see cref="Run(int, ValueTuple{string, Action{Random, CancellationToken}}[])"/>,
    /// en lugar de <c>for (i = 0; i &lt; count &amp;&amp; !stop.IsCancellationRequested; i++)</c>. Se cortan en cuanto otro hilo
    /// falla, pero el presupuesto no corta la corrida hasta que el hilo hizo <see cref="MinimumSteps"/>; la corrida falla si
    /// el hilo sale antes (por su cuenta, sin que nadie fallara).
    /// </summary>
    public static IEnumerable<int> Steps(int count, CancellationToken stop)
    {
        RunState run = t_run ?? throw new InvalidOperationException("Stress.Steps solo vale dentro de un hilo de Stress.Run.");
        RunSteps steps = run.Register(count);
        // Una parada antes del mínimo solo puede venir de un fallo; si es del presupuesto (este hilo empezó tarde), se sigue.
        for (int i = 0; i < count && !(stop.IsCancellationRequested && (run.Failed || i >= steps.Minimum)); i++)
        {
            yield return i;
            steps.Done();
        }
    }

    [ThreadStatic]
    private static RunState? t_run;

    /// <summary>
    /// Corre <paramref name="workers"/> en hilos propios hasta que todos vuelven, juntando sus excepciones. Cada uno
    /// recibe su propio <see cref="Random"/> derivado de la semilla (que cuenta cuántos números saca: su avance) y una
    /// señal de parada que se enciende si otro falla o al vencer <see cref="Budget"/>.
    /// </summary>
    public static void Run(int seed, params (string Name, Action<Random, CancellationToken> Body)[] workers) =>
        Run(seed, Stall, workers);

    /// <summary>
    /// Como <see cref="Run(int, ValueTuple{string, Action{Random, CancellationToken}}[])"/>, con otro tope de «sin avance»:
    /// las pruebas del propio arnés lo bajan para ver fallar el detector en poco tiempo.
    /// </summary>
    public static void Run(int seed, TimeSpan stall, params (string Name, Action<Random, CancellationToken> Body)[] workers)
    {
        var watch = Stopwatch.StartNew();
        Run(seed, stall, () => watch.Elapsed, workers);
    }

    /// <summary>
    /// Como <see cref="Run(int, TimeSpan, ValueTuple{string, Action{Random, CancellationToken}}[])"/>, con el reloj que
    /// deciden los topes de «sin avance» y de <see cref="Deadline"/>: se lee una vez por vuelta de vigilancia (tras esperar
    /// hasta 100 ms al primer hilo vivo). Las pruebas del propio arnés lo avanzan a mano para no depender de cuánto CPU
    /// les dé la máquina; <see cref="Budget"/> sigue siendo de tiempo real.
    /// </summary>
    public static void Run(int seed, TimeSpan stall, Func<TimeSpan> clock, params (string Name, Action<Random, CancellationToken> Body)[] workers) =>
        Run(seed, stall, clock, Budget, workers);

    /// <summary>
    /// Como <see cref="Run(int, TimeSpan, Func{TimeSpan}, ValueTuple{string, Action{Random, CancellationToken}}[])"/>, con otro
    /// presupuesto: las pruebas del propio arnés lo dejan en cero para ver que no corta antes de <see cref="MinimumSteps"/>.
    /// </summary>
    public static void Run(int seed, TimeSpan stall, Func<TimeSpan> clock, TimeSpan budget, params (string Name, Action<Random, CancellationToken> Body)[] workers)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var run = new RunState();
        var errors = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();
        var threads = new List<(string Name, Thread Thread, ProgressRandom Random)>();
        for (int i = 0; i < workers.Length; i++)
        {
            (string name, Action<Random, CancellationToken> body) = workers[i];
            var random = new ProgressRandom(WorkerSeed(seed, i));
            var thread = new Thread(() =>
            {
                t_run = run.For(name);
                try
                {
                    body(random, stop.Token);
                }
#pragma warning disable CA1031 // Se junta cualquier excepción para fallar con la semilla.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    errors.Enqueue($"[{name}] {ex}");
                    run.Fail();
                    stop.Cancel();
                }
            })
            { IsBackground = true, Name = "Estrés " + name };
            threads.Add((name, thread, random));
        }

        threads.ForEach(t => t.Thread.Start());
        var budgetWatch = Stopwatch.StartNew();
        long[] seen = new long[threads.Count];
        TimeSpan[] moved = new TimeSpan[threads.Count];
        string? hung = null;
        TimeSpan looked = TimeSpan.Zero;
        TimeSpan widestGap = TimeSpan.Zero;
        // Un hilo puede terminar entre la condición y la espera: se toma el primero vivo una sola vez, y si ya no queda
        // ninguno se sale (con First, la corrida lanzaba «Sequence contains no matching element»).
        while (threads.FirstOrDefault(t => t.Thread.IsAlive).Thread is { } alive)
        {
            alive.Join(TimeSpan.FromMilliseconds(100));
            // El presupuesto (tiempo real) enciende la parada solo cuando cada hilo con Steps ya hizo su mínimo.
            if (!stop.IsCancellationRequested && budgetWatch.Elapsed >= budget && run.MinimumsReached)
            {
                stop.Cancel();
            }

            TimeSpan now = clock();
            widestGap = now - looked > widestGap ? now - looked : widestGap;
            looked = now;
            var stalled = new List<string>();
            for (int i = 0; i < threads.Count; i++)
            {
                long progress = threads[i].Random.Draws;
                if (progress != seen[i])
                {
                    seen[i] = progress;
                    moved[i] = now;
                }
                else if (threads[i].Thread.IsAlive && now - moved[i] > stall)
                {
                    stalled.Add(threads[i].Name);
                }
            }

            if (stalled.Count > 0)
            {
                hung = $"Posible interbloqueo: {string.Join(", ", stalled)} no avanzó en {stall.TotalSeconds:0.#} s.";
                break;
            }

            if (now > Deadline)
            {
                hung = $"Posible interbloqueo: {string.Join(", ", threads.Where(t => t.Thread.IsAlive).Select(t => t.Name))} no terminó en {Deadline.TotalSeconds:0} s.";
                break;
            }
        }

        if (hung is not null)
        {
            // Las pilas antes de encender la parada: así se ve dónde estaba cada hilo, no cómo sale de ahí.
            // Con el estado de cada uno y lo que tardó el propio vigilante entre dos miradas: si también él estuvo parado
            // mucho tiempo, el proceso entero se quedó sin CPU; si miró cada 100 ms, el hilo de verdad no avanzaba.
            var names = threads.ToDictionary(t => t.Thread.ManagedThreadId, t => "[" + t.Name + "]");
            string stacks = ThreadDump.Capture(names);
            string state = string.Join("; ", threads.Select((t, i) =>
                $"{t.Name}: {(t.Thread.IsAlive ? "vivo" : "terminado")}, {seen[i]} números, el último hace {(looked - moved[i]).TotalSeconds:0.#} s"));
            string requested = stop.IsCancellationRequested ? "ya pedida" : "sin pedir";
            run.Fail();
            stop.Cancel();
            errors.Enqueue($"{hung}{Environment.NewLine}Estado: {state}. A los {looked.TotalSeconds:0.#} s; la parada estaba {requested}; "
                + $"el vigilante tardó como mucho {widestGap.TotalSeconds:0.##} s entre dos miradas.{Environment.NewLine}"
                + $"Pilas de los hilos al saltar el detector:{Environment.NewLine}{stacks}");
        }
        else if (errors.IsEmpty && threads.All(t => t.Random.Draws == 0))
        {
            // Ningún hilo dio una vuelta (por ejemplo, el presupuesto venció antes de empezar): no se probó nada.
            errors.Enqueue($"La corrida no avanzó: ninguno de {string.Join(", ", threads.Select(t => t.Name))} sacó un número de su Random.");
        }
        else if (errors.IsEmpty)
        {
            // Nadie falló, así que nada cortó las vueltas antes del mínimo: un hilo que se quedó corto salió por su cuenta.
            foreach (string shortRun in run.Short())
            {
                errors.Enqueue(shortRun);
            }
        }

        Assert.True(errors.IsEmpty, $"Semilla {seed}: {string.Join(Environment.NewLine, errors)}");
    }

    /// <summary>
    /// Semilla del <see cref="Random"/> del hilo <paramref name="worker"/> de <see cref="Run(int, ValueTuple{string, Action{Random, CancellationToken}}[])"/>:
    /// depende solo de la semilla y del índice (SplitMix64), así que la «Semilla N» de un fallo repite la corrida en otro
    /// proceso. Con <see cref="HashCode.Combine{T1, T2}"/>, que cambia de un proceso a otro, no la repetía.
    /// </summary>
    public static int WorkerSeed(int seed, int worker) => StressSeeds.Seed(seed, worker);

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
        if (!thread.Join(limit))
        {
            string stacks = ThreadDump.Capture(new Dictionary<int, string> { [thread.ManagedThreadId] = "[" + what + "]" });
            Assert.Fail($"Semilla {seed}: {what} no terminó en {limit.TotalSeconds:0.#} s (posible interbloqueo).{Environment.NewLine}Pilas de los hilos:{Environment.NewLine}{stacks}");
        }

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

/// <summary>Las vueltas de <see cref="Stress.Steps"/> de cada hilo de una corrida, para no cortar antes de su mínimo.</summary>
internal sealed class RunState
{
    private readonly ConcurrentQueue<RunSteps> _steps = new();
    private readonly StrongBox<bool> _failed = new();
    private readonly string _name = "";

    /// <summary>Todos los hilos que recorren vueltas con <see cref="Stress.Steps"/> hicieron ya su mínimo.</summary>
    public bool MinimumsReached => _steps.All(s => s.Count >= s.Minimum);

    /// <summary>Algún hilo falló o la corrida se dio por trabada: las vueltas paren ya, sin mínimo.</summary>
    public bool Failed => Volatile.Read(ref _failed.Value);

    public void Fail() => Volatile.Write(ref _failed.Value, true);

    /// <summary>El estado que ve el hilo <paramref name="name"/>.</summary>
    public RunState For(string name) => new(this, name);

    private RunState(RunState shared, string name)
    {
        _steps = shared._steps;
        _failed = shared._failed;
        _name = name;
    }

    public RunState()
    {
    }

    public RunSteps Register(int count)
    {
        var steps = new RunSteps(_name, count, Stress.MinimumSteps(count));
        _steps.Enqueue(steps);
        return steps;
    }

    /// <summary>Los hilos que salieron de sus vueltas antes del mínimo.</summary>
    public IEnumerable<string> Short() => _steps
        .Where(s => s.Count < Math.Min(s.Minimum, s.Total))
        .Select(s => $"{s.Name} hizo {s.Count} de {s.Total} vueltas; el mínimo es {s.Minimum}.");
}

/// <summary>Cuántas vueltas de <see cref="Stress.Steps"/> lleva un hilo.</summary>
internal sealed class RunSteps(string name, int total, int minimum)
{
    private int _count;

    public string Name => name;

    public int Total => total;

    public int Minimum => Math.Min(minimum, total);

    public int Count => Volatile.Read(ref _count);

    public void Done() => Interlocked.Increment(ref _count);
}

/// <summary>Un <see cref="Random"/> que cuenta los números que saca: el avance de un hilo de <see cref="Stress.Run"/>.</summary>
internal sealed class ProgressRandom(int seed) : Random(seed)
{
    private long _draws;

    public long Draws => Interlocked.Read(ref _draws);

    public override int Next()
    {
        Interlocked.Increment(ref _draws);
        return base.Next();
    }

    public override int Next(int maxValue)
    {
        Interlocked.Increment(ref _draws);
        return base.Next(maxValue);
    }

    public override int Next(int minValue, int maxValue)
    {
        Interlocked.Increment(ref _draws);
        return base.Next(minValue, maxValue);
    }

    public override long NextInt64()
    {
        Interlocked.Increment(ref _draws);
        return base.NextInt64();
    }

    public override long NextInt64(long maxValue)
    {
        Interlocked.Increment(ref _draws);
        return base.NextInt64(maxValue);
    }

    public override long NextInt64(long minValue, long maxValue)
    {
        Interlocked.Increment(ref _draws);
        return base.NextInt64(minValue, maxValue);
    }

    public override double NextDouble()
    {
        Interlocked.Increment(ref _draws);
        return base.NextDouble();
    }

    public override float NextSingle()
    {
        Interlocked.Increment(ref _draws);
        return base.NextSingle();
    }

    public override void NextBytes(byte[] buffer)
    {
        Interlocked.Increment(ref _draws);
        base.NextBytes(buffer);
    }

    public override void NextBytes(Span<byte> buffer)
    {
        Interlocked.Increment(ref _draws);
        base.NextBytes(buffer);
    }
}
