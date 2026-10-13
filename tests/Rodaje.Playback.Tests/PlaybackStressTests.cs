using System.Collections.Concurrent;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Timeline;
using Rodaje.Playback.Tests.Support;

namespace Rodaje.Playback.Tests;

/// <summary>
/// El programador de un clip bajo presión, con hilos de verdad: el refresco elige cuadros mientras otro hilo busca,
/// reproduce y pausa al azar, el decodificador termina en su hilo o en otro (a veces tarde, a veces con error) y el
/// cierre llega con decodificaciones en curso. Ningún cuadro se libera dos veces ni queda sin liberar.
/// </summary>
[Trait("Categoria", "Estres")]
public sealed class PlaybackStressTests
{
    private static readonly ClipTiming Clip = new(Time.FromSeconds(1), Time.FromSeconds(3), Time.FromSeconds(23), Rational.Fps30);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Random_seeks_while_playing_from_another_thread_never_race(int seed)
    {
        var payloads = new PayloadTracker();
        var source = new ThreadedSource(seed, Clip.FrameRate, payloads);
        var clock = new ManualClock(Clip.Start);
        var cache = new FrameCache(new Random(seed).Next(4, 40));
        var executor = new DecodeThread("Estrés decodificación");
        var scheduler = new PlaybackScheduler(Clip, clock, source, cache, executor, new PlaybackOptions { PrefetchFrames = new Random(seed).Next(1, 9) });
        var failures = new ConcurrentQueue<string>();
        scheduler.DecodeFailed += (_, e) =>
        {
            if (!ThreadedSource.Fails(e.FrameIndex))
            {
                failures.Enqueue($"cuadro {e.FrameIndex}: {e.Error}");
            }
        };
        int ready = 0;
        scheduler.ExactFrameReady += (_, _) => Interlocked.Increment(ref ready);
        int refreshes = Stress.Iterations(3000);
        int done = 0;

        try
        {
            Stress.Run(
                seed,
                ("refresco", (random, stop) =>
                {
                    try
                    {
                        foreach (int i in Stress.Steps(refreshes, stop))
                        {
                            clock.Advance(Time.FromTicks(Time.TicksPerSecond * random.Next(0, 40) / 1000));
                            FramePresentation shown = scheduler.Tick();
                            if (shown.Frame is { } frame)
                            {
                                // Vale hasta el siguiente Tick aunque la caché lo haya expulsado.
                                Assert.False(frame.IsReleased, $"Refresco {i}: el cuadro {frame.Index} que se muestra ya estaba liberado.");
                                Assert.InRange(frame.Index, Clip.FirstFrame, Clip.LastFrame);
                            }

                            if (random.Next(4) == 0)
                            {
                                Thread.Sleep(random.Next(0, 2));
                            }
                        }
                    }
                    finally
                    {
                        Volatile.Write(ref done, 1);
                    }
                }),
                ("transporte", (random, stop) =>
                {
                    while (Volatile.Read(ref done) == 0 && !stop.IsCancellationRequested)
                    {
                        switch (random.Next(10))
                        {
                            case < 5:
                                scheduler.Seek(Clip.Start + Time.FromSeconds((random.NextDouble() * 22) - 1));
                                break;
                            case < 7:
                                scheduler.SeekToFrame(random.Next((int)Clip.FirstFrame, (int)Clip.LastFrame + 1));
                                break;
                            case 7:
                                scheduler.Play();
                                break;
                            case 8:
                                scheduler.Pause();
                                break;
                            default:
                                _ = scheduler.Stats;
                                _ = scheduler.IsSeekPending;
                                break;
                        }

                        Thread.Sleep(random.Next(0, 4));
                    }
                }));
        }
        finally
        {
            // El cierre, con decodificaciones todavía en curso: primero el programador, luego su hilo y la caché.
            Stress.Within(seed, "cerrar", TimeSpan.FromSeconds(20), () =>
            {
                scheduler.Dispose();
                executor.Dispose();
                cache.Dispose();
            });
        }

        source.Drain();
        Stress.WaitUntil(seed, "liberar los cuadros que llegaron tras el cierre", () => payloads.Live == 0, TimeSpan.FromSeconds(5));
        Assert.True(failures.IsEmpty, $"Semilla {seed}: fallos que el decodificador no produjo: {string.Join("; ", failures.Take(3))}");
        Assert.True(payloads.Live == 0, $"Semilla {seed}: quedaron {payloads.Live} cuadros sin liberar de {payloads.Created}.");
        Assert.True(source.Overlaps == 0, $"Semilla {seed}: {source.Overlaps} pedidos se solaparon en el mismo decodificador.");
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public void Closing_while_decoding_releases_every_frame(int seed)
    {
        var random = new Random(seed);
        int rounds = Stress.Iterations(40);
        for (int round = 0; round < rounds; round++)
        {
            var payloads = new PayloadTracker();
            var source = new ThreadedSource(StressSeeds.Seed(seed, round), Clip.FrameRate, payloads);
            var clock = new ManualClock(Clip.Start + Time.FromSeconds(random.NextDouble() * 19));
            var cache = new FrameCache(16);
            var executor = new DecodeThread("Estrés cierre");
            var scheduler = new PlaybackScheduler(Clip, clock, source, cache, executor, new PlaybackOptions { PrefetchFrames = 6 });
            if (random.Next(2) == 0)
            {
                scheduler.Play();
            }

            for (int i = random.Next(0, 6); i > 0; i--)
            {
                clock.Advance(Time.FromSeconds(1.0 / 60));
                scheduler.Tick();
            }

            if (random.Next(2) == 0)
            {
                scheduler.Seek(Clip.Start + Time.FromSeconds(random.NextDouble() * 19));
            }

            Thread.Sleep(random.Next(0, 5));
            Stress.Within(seed, $"cerrar en la vuelta {round}", TimeSpan.FromSeconds(20), () =>
            {
                scheduler.Dispose();
                executor.Dispose();
                cache.Dispose();
            });
            source.Drain();
            Stress.WaitUntil(seed, "liberar los cuadros que llegaron tras el cierre", () => payloads.Live == 0, TimeSpan.FromSeconds(5));
            Assert.True(payloads.Live == 0, $"Semilla {seed}, vuelta {round}: quedaron {payloads.Live} cuadros sin liberar.");
            Assert.True(source.Overlaps == 0, $"Semilla {seed}, vuelta {round}: se solaparon pedidos en el mismo decodificador.");
        }
    }

    /// <summary>
    /// Decodificador falso en hilos de verdad: unos cuadros salen en el acto, otros terminan en un hilo propio tras
    /// una espera que respeta la cancelación, y algunos fallan siempre (<see cref="Fails"/>) o no existen.
    /// </summary>
    private sealed class ThreadedSource(int seed, Rational rate, PayloadTracker payloads) : IVideoFrameSource
    {
        private readonly ConcurrentDictionary<Task, bool> _pending = new();
        private int _busy;
        private int _calls;
        private int _overlaps;

        public int Overlaps => Volatile.Read(ref _overlaps);

        /// <summary>Cuadros dañados: el decodificador falla siempre con ellos.</summary>
        public static bool Fails(long index) => index % 97 == 13;

        public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken)
        {
            // El programador nunca pide dos cuadros a la vez al mismo decodificador.
            if (Interlocked.Exchange(ref _busy, 1) != 0)
            {
                Interlocked.Increment(ref _overlaps);
            }

            int call = Interlocked.Increment(ref _calls);
            int kind = (int)(StressSeeds.Mix(seed, frameIndex, call) % 10);
            if (Fails(frameIndex))
            {
                Volatile.Write(ref _busy, 0);
                return ValueTask.FromException<DecodedFrame?>(new InvalidDataException($"Cuadro {frameIndex} dañado."));
            }

            if (kind < 5)
            {
                Volatile.Write(ref _busy, 0);
                return ValueTask.FromResult<DecodedFrame?>(Make(frameIndex));
            }

            // En un hilo propio y no en el grupo de hilos: con la CI cargada el grupo puede tardar más de 10 s en darle un
            // hilo, y Drain lo tomaría por un cuadro colgado.
            Task<DecodedFrame?> task = Task.Factory.StartNew<DecodedFrame?>(
                () =>
                {
                    try
                    {
                        cancellationToken.WaitHandle.WaitOne(kind == 9 ? 30 : kind - 4);
                        cancellationToken.ThrowIfCancellationRequested();
                        return Make(frameIndex);
                    }
                    finally
                    {
                        Volatile.Write(ref _busy, 0);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _pending.TryAdd(task, true);
            return new ValueTask<DecodedFrame?>(task);
        }

        /// <summary>
        /// Espera a que terminen las decodificaciones que corrían en sus hilos; falla si alguna no termina (cada
        /// espera dura como mucho 30 ms o hasta que la cancelan, así que una que no vuelve es un cuadro colgado).
        /// </summary>
        public void Drain()
        {
            int hung = 0;
            foreach (Task task in _pending.Keys)
            {
                try
                {
                    if (!task.Wait(TimeSpan.FromSeconds(10)))
                    {
                        hung++;
                    }
                }
                catch (AggregateException)
                {
                    // Cancelada o fallida: terminó igual.
                }
            }

            Assert.True(hung == 0, $"Semilla {seed}: {hung} decodificaciones no terminaron en 10 s.");
        }

        private DecodedFrame Make(long index)
        {
            Time pts = Time.FromFrames(index, rate);
            return new DecodedFrame(index, pts, Time.FromFrames(index + 1, rate) - pts, payloads.Create(), 1);
        }
    }
}
