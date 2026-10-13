using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Tests.Timeline;
using Rodaje.Media.Export;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Offline;

/// <summary>
/// La exportación bajo presión: se edita el proyecto mientras se exporta una copia, y cada exportación se cancela en un
/// momento al azar (esperando un cuadro, decodificando o midiendo la sonoridad). Solo puede salir
/// <see cref="OperationCanceledException"/>, y al desecharla no queda ningún cuadro ni decodificador abierto.
/// </summary>
[Trait("Categoria", "Estres")]
public sealed class OfflineStressTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly Rational Fps30 = Rational.Fps30;
    private static readonly string[] MediaIds = ["m0", "m1", "m2"];

    [Theory]
    [InlineData(5)]
    [InlineData(55)]
    [InlineData(555)]
    public void Editing_while_exporting_and_canceling_at_random_moments_never_races(int seed)
    {
        var backend = new FakeTimelineBackend
        {
            RealThreads = true,
            DecodeDelay = index => SlowDecode(seed, index),
        };
        var b = new TimelineBuilder(3, 2);
        foreach (string id in MediaIds)
        {
            b.AddMedia(id, Fps30, 900, backend: backend);
        }

        var setup = new Random(seed);
        for (int t = 0; t < 3; t++)
        {
            Relayout(b, setup, t);
        }

        b.AddAudio(0, "m0", Time.Zero, Time.Zero, S(4));
        b.AddAudio(1, "m1", S(1), S(2), S(3));
        Project snapshot = b.Project.Clone();
        int exports = Stress.Iterations(12);
        int finished = 0;
        int canceled = 0;
        var cancel = new CancellationTokenSource();

        Stress.Run(
            seed,
            ("edición", (random, stop) =>
            {
                while (Volatile.Read(ref finished) == 0 && !stop.IsCancellationRequested)
                {
                    // El proyecto que se edita es otro objeto: la exportación recibe una copia, como en la app.
                    Relayout(b, random);
                    if (random.Next(3) == 0)
                    {
                        b.Sequence.AudioTracks[random.Next(2)].Muted ^= true;
                    }

                    Volatile.Write(ref snapshot, b.Project.Clone());
                    Thread.Sleep(random.Next(0, 3));
                }
            }),
            ("exportación", (random, stop) =>
            {
                try
                {
                    foreach (int e in Stress.Steps(exports, stop))
                    {
                        CancellationToken token = Volatile.Read(ref cancel).Token;
                        Project project = Volatile.Read(ref snapshot);
                        try
                        {
                            Export(project, backend, random, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref canceled);
                        }

                        Assert.True(backend.Live.Value == 0, $"Exportación {e}: quedaron {backend.Live.Value} cuadros sin soltar.");
                        Assert.False(backend.OpenVideos.Any(), $"Exportación {e}: quedaron decodificadores abiertos.");
                        if (token.IsCancellationRequested)
                        {
                            Volatile.Write(ref cancel, new CancellationTokenSource());
                        }
                    }
                }
                finally
                {
                    Volatile.Write(ref finished, 1);
                }
            }),
            ("cancelar", (random, stop) =>
            {
                while (Volatile.Read(ref finished) == 0 && !stop.IsCancellationRequested)
                {
                    Thread.Sleep(random.Next(1, 120));
                    if (random.Next(3) == 0)
                    {
                        Volatile.Read(ref cancel).Cancel();
                    }
                }
            }));

        output.WriteLine($"Semilla {seed}: {exports} exportaciones, {canceled} canceladas.");
    }

    /// <summary>
    /// La «Semilla N» que imprime esta prueba repite la corrida en otro proceso: los cuadros lentos del decodificador y el
    /// <see cref="Random"/> de cada hilo de <see cref="Stress.Run(int, ValueTuple{string, Action{Random, CancellationToken}}[])"/>
    /// dependen solo de la semilla. Regresión: los dos salían de <see cref="HashCode.Combine{T1, T2}"/>, cuya semilla es
    /// aleatoria por proceso, y estos valores fijados cambiaban en cada ejecución.
    /// </summary>
    [Fact]
    public void The_printed_seed_reproduces_the_run_in_another_process()
    {
        string slow = string.Concat(Enumerable.Range(0, 24).Select(i => SlowDecode(5, i) > TimeSpan.Zero ? 'L' : 'r'));
        Assert.Equal("rrrrLrrrrrrLrrrrrrLrrrLr", slow);

        Assert.Equal(1_545_029_486, Stress.WorkerSeed(5, 0));
        Assert.Equal(1_325_234_912, Stress.WorkerSeed(5, 1));

        // Cada hilo de la corrida recibe el Random de su semilla fijada, no otro.
        int[] first = new int[2];
        Stress.Run(
            5,
            ("primero", (random, _) => first[0] = random.Next()),
            ("segundo", (random, _) => first[1] = random.Next()));
        Assert.Equal([new Random(1_545_029_486).Next(), new Random(1_325_234_912).Next()], first);
    }

    /// <summary>Uno de cada siete cuadros, más o menos, tarda 4 ms en decodificarse; cuáles, según la semilla.</summary>
    private static TimeSpan SlowDecode(int seed, long index) => TimeSpan.FromMilliseconds(StressSeeds.Mix(seed, index) % 7 == 0 ? 4 : 0);

    /// <summary>Como una exportación: cuadro tras cuadro con alguna vuelta atrás, y a veces la sonoridad antes.</summary>
    private static void Export(Project project, FakeTimelineBackend backend, Random random, CancellationToken token)
    {
        var options = new OfflineTimelineOptions { ProjectDirectory = @"C:\proyecto", LookaheadFrames = random.Next(1, 10), FrameTimeout = TimeSpan.FromSeconds(20) };
        if (random.Next(3) == 0)
        {
            using var audio = new OfflineAudio(project, "sec", @"C:\proyecto", backend);
            _ = audio.MeasureLoudness(Time.Zero, Time.Min(audio.Duration, S(2)), null, token);
        }

        using var timeline = new OfflineTimeline(project, "sec", null, options, backend);
        long frame = random.Next(0, 30);
        for (int i = 0; i < 60 && frame < timeline.FrameCount; i++)
        {
            using (timeline.Render(frame, token))
            {
            }

            frame = random.Next(25) == 0 ? Math.Max(0, frame - random.Next(1, 60)) : frame + 1;
        }
    }

    private static void Relayout(TimelineBuilder b, Random random, int? track = null)
    {
        int t = track ?? random.Next(b.Sequence.VideoTracks.Count);
        b.Sequence.VideoTracks[t].Clips.Clear();
        Time at = Time.Zero;
        for (int i = random.Next(1, 4); i > 0; i--)
        {
            Time duration = Time.FromFrames(random.Next(20, 90), Fps30);
            b.AddVideo(t, MediaIds[random.Next(MediaIds.Length)], at, Time.FromFrames(random.Next(0, 600), Fps30), duration);
            at += duration;
        }
    }
}
