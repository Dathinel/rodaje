using System.Diagnostics;
using System.Globalization;
using Rodaje.Tests.Fuzz;

namespace Rodaje.App.Tests.Support;

/// <summary>
/// Vigilancia contra cuelgues por falta de avance, no por reloj de pared (tarea 321). En la CI de Windows el fuzz de
/// proyectos extremos se cortó a los 15 s con «abrir» en 9,2 s y 9,3 s de pausa del GC del proceso: la máquina estaba
/// parada, no la prueba. Cuenta el tiempo desde el último avance de <c>progress</c> descontando las pausas del GC
/// (<see cref="GC.GetTotalPauseDuration"/>), que paran todos los hilos; un interbloqueo no avanza ni pausa el GC, así que
/// se sigue cortando en el mismo plazo. El reloj y las pausas se inyectan para probarlo sin depender del tiempo.
/// </summary>
internal sealed class StallWatch(TimeSpan limit, Func<TimeSpan> clock, Func<TimeSpan> gcPause)
{
    private long _seen = long.MinValue;
    private TimeSpan _since;
    private TimeSpan _gcSince;

    public TimeSpan Limit => limit;

    public static StallWatch Real(TimeSpan limit)
    {
        long start = Stopwatch.GetTimestamp();
        return new StallWatch(limit, () => Stopwatch.GetElapsedTime(start), GC.GetTotalPauseDuration);
    }

    /// <summary>Tiempo sin avance, sin las pausas del GC, desde que <paramref name="progress"/> cambió por última vez.</summary>
    public TimeSpan Idle(long progress)
    {
        TimeSpan now = clock();
        TimeSpan gc = gcPause();
        if (progress != _seen)
        {
            _seen = progress;
            _since = now;
            _gcSince = gc;
            return TimeSpan.Zero;
        }

        return now - _since - (gc - _gcSince);
    }

    public bool Stalled(long progress) => Idle(progress) > limit;

    /// <summary>
    /// Como <see cref="FuzzRunner.Run"/> (mismas semillas, casos, presupuesto y <c>RODAJE_FUZZ_SEMILLA</c>), pero un caso
    /// se da por colgado cuando <paramref name="progress"/> no avanza en <see cref="Limit"/> sin contar el GC, no cuando
    /// tarda más de <see cref="FuzzRunner.CaseTimeout"/>. El mensaje del cuelgue añade <paramref name="describe"/>.
    /// </summary>
    public static int RunCases(string name, int baseSeed, int cases, TimeSpan budget, int minimumCases, Func<long> progress, Func<string> describe, StallWatch watch, Action<int, Random> body)
    {
        int total = FuzzRunner.Long ? cases * 20 : cases;
        TimeSpan stopAfter = FuzzRunner.Long ? budget * 20 : (budget < FuzzRunner.NormalBudget ? budget : FuzzRunner.NormalBudget);
        int minimum = FuzzRunner.Long ? minimumCases : Math.Max(1, minimumCases / 4);
        int? only = FuzzRunner.OnlySeed;
        int done = 0;
        int current = baseSeed;
        Exception? failure = null;
        bool stop = false;
        var clock = Stopwatch.StartNew();
        var thread = new Thread(() =>
        {
            for (int i = 0; i < total && !Volatile.Read(ref stop); i++)
            {
                int seed = baseSeed + i;
                if (only is { } wanted && seed != wanted)
                {
                    continue;
                }

                Volatile.Write(ref current, seed);
                try
                {
                    body(seed, new Random(seed));
                }
                catch (Exception ex)
                {
                    failure = new Xunit.Sdk.XunitException(Invariant(
                        $"{name}: falló el caso de la semilla {seed} (repetir solo ese caso con RODAJE_FUZZ_SEMILLA={seed}).\n{ex.GetType().Name}: {ex.Message}\n{FuzzRunner.Truncate(ex.StackTrace ?? "", 2000)}"));
                    return;
                }

                Interlocked.Increment(ref done);
                if (clock.Elapsed > stopAfter && done >= minimum)
                {
                    return;
                }
            }
        }, maxStackSize: 16 * 1024 * 1024)
        {
            IsBackground = true,
            Name = "fuzz " + name,
        };

        thread.Start();
        while (!thread.Join(50))
        {
            if (watch.Stalled(progress()))
            {
                Volatile.Write(ref stop, true);
                int seed = Volatile.Read(ref current);
                throw new Xunit.Sdk.XunitException(Invariant(
                    $"{name}: el caso de la semilla {seed} no avanzó en {watch.Limit.TotalSeconds:F0} s sin contar el GC (cuelgue; repetir con RODAJE_FUZZ_SEMILLA={seed}). {describe()}"));
            }
        }

        if (failure is not null)
        {
            throw failure;
        }

        if (only is null)
        {
            Assert.True(done >= Math.Min(minimum, total), Invariant($"{name}: solo {done} casos en {clock.Elapsed.TotalSeconds:F1} s (mínimo {minimum})."));
        }

        return done;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
