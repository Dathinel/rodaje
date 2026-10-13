using System.Diagnostics;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Leaks;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LeakGroup
{
    /// <summary>Las pruebas de fugas miden hilos y memoria del proceso: corren solas.</summary>
    public const string Name = "Fugas del motor";
}

/// <summary>
/// Abrir y cerrar la sesión de la línea de tiempo muchas veces, y reproducir y buscar sin parar: al terminar no queda
/// ningún decodificador, lector de audio ni cuadro vivo, y ni los hilos ni la memoria administrada crecen con las vueltas.
/// Con <c>RODAJE_PRUEBAS_LARGAS=1</c> se dan diez veces más vueltas.
/// </summary>
[Collection(LeakGroup.Name)]
public sealed class TimelineSessionLeakTests(ITestOutputHelper output)
{
    private const string ProjectDirectory = @"C:\proyecto";
    private static readonly Time Refresh60 = Time.FromFrames(1, Rational.Fps60);
    private static readonly int Scale = Rodaje.TestMedia.Skip.LongRuns ? 10 : 1;

    [Fact]
    public void Opening_and_closing_a_session_a_hundred_times_leaves_nothing_alive()
    {
        int cycles = 100 * Scale;
        var watch = Stopwatch.StartNew();
        RunCycle(0);
        long memoryBefore = ManagedMemory();
        int threadsBefore = Threads();
        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            RunCycle(cycle);
        }

        long grown = ManagedMemory() - memoryBefore;
        int newThreads = Threads() - threadsBefore;
        output.WriteLine($"{cycles} vueltas en {watch.Elapsed.TotalSeconds:F1} s: memoria {grown / 1024.0:F0} KB, hilos {newThreads:+0;-0;0}.");

        // Una sesión que no se suelta retiene su plan, los búferes de la mezcla y un hilo de audio: 100 son decenas de MB.
        Assert.True(grown < 8L << 20, $"La memoria administrada creció {grown / 1024.0:F0} KB en {cycles} vueltas.");
        Assert.True(newThreads < 16, $"Quedaron {newThreads} hilos más tras {cycles} vueltas.");
    }

    [Fact]
    public void Playing_and_seeking_a_thousand_times_keeps_frames_and_decoders_bounded()
    {
        int seeks = 1000 * Scale;
        var backend = new FakeTimelineBackend { RealThreads = true };
        TimelineBuilder b = Project(backend, 0);
        var random = new Random(96);
        long memoryBefore = 0;
        int worstFrames = 0;
        int worstDecoders = 0;
        using (TimelineSession session = Open(b, backend))
        {
            session.Play();
            for (int i = 0; i < seeks; i++)
            {
                if (i == seeks / 10)
                {
                    memoryBefore = ManagedMemory();
                }

                if (i % 3 == 0)
                {
                    session.Seek(S(random.NextDouble() * 9));
                }

                for (int refresh = 0; refresh < 2; refresh++)
                {
                    backend.Clock.Advance(Refresh60);
                    session.Tick().Dispose();
                }

                worstFrames = Math.Max(worstFrames, backend.Live.Value);
                worstDecoders = Math.Max(worstDecoders, session.Stats.DecodersOpen);
            }

            long grown = ManagedMemory() - memoryBefore;
            output.WriteLine($"{seeks} vueltas: memoria {grown / 1024.0:F0} KB, hasta {worstFrames} cuadros vivos y {worstDecoders} decodificadores.");
            Assert.True(grown < 8L << 20, $"La memoria administrada creció {grown / 1024.0:F0} KB al reproducir y buscar.");
        }

        // Tres orígenes de video, cada uno con su tope de cuadros (24 por omisión) y lo que retienen las capas.
        Assert.InRange(worstFrames, 0, 3 * 24 * 2);
        Assert.InRange(worstDecoders, 0, 3 * 4);
        AssertNothingAlive(backend);
    }

    private static void RunCycle(int cycle)
    {
        var backend = new FakeTimelineBackend { RealThreads = true };
        TimelineBuilder b = Project(backend, cycle);
        using (TimelineSession session = Open(b, backend))
        {
            session.Play();
            for (int refresh = 0; refresh < 6; refresh++)
            {
                backend.Clock.Advance(Refresh60);
                session.Tick().Dispose();
            }

            session.Seek(S(4.5));
            backend.Clock.Advance(Refresh60);
            session.Tick().Dispose();
        }

        AssertNothingAlive(backend);
    }

    private static void AssertNothingAlive(FakeTimelineBackend backend)
    {
        Assert.Empty(backend.OpenVideos);
        Assert.All(backend.Pcms, p => Assert.True(p.Disposed, $"Lector de audio de {p.Path} sin cerrar."));
        Assert.Equal(0, backend.Live.Value);
        Assert.Contains("output", backend.Log.Entries);
    }

    /// <summary>Video con cortes, un título, una máscara, audio con compresor y un bus: lo que toca cada parte del motor.</summary>
    private static TimelineBuilder Project(FakeTimelineBackend backend, int cycle)
    {
        var b = new TimelineBuilder(2, 2);
        string[] media = [$"m{cycle % 3}", $"n{cycle % 3}", $"o{cycle % 3}"];
        foreach (string id in media)
        {
            MediaRef m = b.AddMedia(id, Rational.Fps30, 600, backend: backend);

            // En Windows es la misma ruta; en otros sistemas, la ruta tal como la resuelve el plan.
            backend.Register(m.ResolvePath(ProjectDirectory), Samples.Info(Rational.Fps30, 600, true) with { Path = m.ResolvePath(ProjectDirectory) });
        }

        Clip first = b.AddVideo(0, media[0], Time.Zero, Time.Zero, S(3));
        b.AddVideo(0, media[1], S(3), S(1), S(3));
        b.AddVideo(0, media[2], S(6), S(2), S(4));
        b.AddTitle(1, S(1), S(5), $"Vuelta {cycle}");
        first.Effects.Add(new EffectInstance { Id = "mascara", Type = BuiltInEffects.Mask });
        Clip voice = b.AddAudio(0, media[0], Time.Zero, Time.Zero, S(6));
        voice.Effects.Add(new EffectInstance { Id = "comp", Type = BuiltInEffects.Compressor });
        b.AddAudio(1, media[1], S(2), Time.Zero, S(6));
        b.Sequence.AudioBuses.Add(new AudioBus { Id = "voces", Name = "Voces" });
        b.Sequence.AudioTracks[0].Output = "voces";
        return b;
    }

    private static TimelineSession Open(TimelineBuilder b, FakeTimelineBackend backend) =>
        new(b.Project, b.Sequence.Id, null, new TimelineSessionOptions { ProjectDirectory = ProjectDirectory }, backend);

    private static long ManagedMemory()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static int Threads()
    {
        using var process = Process.GetCurrentProcess();
        return process.Threads.Count;
    }
}
