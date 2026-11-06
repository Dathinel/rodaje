using Xunit.Abstractions;

namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// La medida de los Event con identificadores de verdad (tarea 308): diez tramos de diez «aperturas» con hilos que se
/// suman en cada tramo, como el grupo de hilos. Sin retener nada no hay fuga; reteniendo un <c>ManualResetEvent</c>,
/// el <c>WaitHandle</c> de un <c>CancellationTokenSource</c> o el <c>AvailableWaitHandle</c> de un <c>SemaphoreSlim</c>
/// por apertura, sí. Solo en Windows los eventos son identificadores.
/// </summary>
[Collection(AppLeakGroup.Name)]
public sealed class EventLeakMeasureTests(ITestOutputHelper output)
{
    private const int Samples = 10;
    private const int ThreadsPerSample = 3;

    [Fact]
    public void An_event_kept_per_opening_is_seen_and_threads_with_their_events_are_not()
    {
        double perThread = ProcessHandles.MeasureEventsPerThread();
        (int[] cleanEvents, int[] cleanThreads) = Run(keepEvents: false);
        (int[] leakEvents, int[] leakThreads) = Run(keepEvents: true);
        int[] clean = HandleLeakCriterion.UnthreadedEvents(cleanEvents, cleanThreads, perThread);
        int[] leaking = HandleLeakCriterion.UnthreadedEvents(leakEvents, leakThreads, perThread);
        output.WriteLine($"{perThread:F2} Event por hilo. Sin retener: Event {string.Join(" ", cleanEvents)}, Thread {string.Join(" ", cleanThreads)} → {string.Join(" ", clean)}.");
        output.WriteLine($"Reteniendo uno por apertura: Event {string.Join(" ", leakEvents)}, Thread {string.Join(" ", leakThreads)} → {string.Join(" ", leaking)}.");

        Assert.Null(HandleLeakCriterion.Find(clean, "Los Event sin los de los hilos"));
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(HandleLeakCriterion.Find(leaking));
            // El grupo de hilos puede soltar algún hilo ocioso de pruebas anteriores durante la corrida (en la CI:
            // 65 67 70 73 76 79 82 85 88 91, donde caducó uno en el primer tramo y los demás subieron de 3 en 3).
            Assert.True(HandleLeakCriterion.MedianFloorStep(cleanThreads) >= ThreadsPerSample, $"Los hilos no se contaron: Thread {string.Join(" ", cleanThreads)}.");
            if (perThread >= 1)
            {
                // Lo que hacía falta restar: sin eso, los hilos que se suman parecerían una fuga de eventos.
                Assert.NotNull(HandleLeakCriterion.Find(cleanEvents));
            }
        }
    }

    /// <summary>Las cuentas de Event y Thread cada <see cref="HandleLeakCriterion.SampleCycles"/> aperturas.</summary>
    private static (int[] Events, int[] Threads) Run(bool keepEvents)
    {
        var kept = new List<IDisposable>();
        var threads = new List<Thread>();
        using var release = new ManualResetEventSlim();
        var events = new int[Samples];
        var threadHandles = new int[Samples];
        try
        {
            for (int sample = 0; sample < Samples; sample++)
            {
                for (int opening = 0; opening < HandleLeakCriterion.SampleCycles; opening++)
                {
                    if (keepEvents)
                    {
                        kept.Add(KeepEvent(opening % 3));
                    }
                }

                for (int i = 0; i < ThreadsPerSample; i++)
                {
                    var thread = new Thread(() => release.Wait()) { IsBackground = true };
                    thread.Start();
                    threads.Add(thread);
                }

                ProcessHandles.Snapshot snapshot = ProcessHandles.Take();
                events[sample] = snapshot.Of(ProcessHandles.EventType);
                threadHandles[sample] = snapshot.Of(ProcessHandles.ThreadType);
            }
        }
        finally
        {
            release.Set();
            threads.ForEach(t => t.Join());
            kept.ForEach(k => k.Dispose());
        }

        return (events, threadHandles);
    }

    /// <summary>Un objeto que retiene un identificador Event mientras viva.</summary>
    private static IDisposable KeepEvent(int kind)
    {
        switch (kind)
        {
            case 0:
                return new ManualResetEvent(initialState: false);
            case 1:
                var source = new CancellationTokenSource();
                _ = source.Token.WaitHandle;
                return source;
            default:
                var semaphore = new SemaphoreSlim(1);
                _ = semaphore.AvailableWaitHandle;
                return semaphore;
        }
    }
}
