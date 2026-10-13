using System.Diagnostics;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using static Rodaje.Engine.Tests.Support.Seconds;
using C = Rodaje.Core.Model.BuiltInEffects.CompressorParameters;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// La vista previa bajo presión, con hilos de verdad: búsquedas al azar mientras reproduce, ediciones, cambios del
/// mezclador en vivo, el hilo de audio leyendo y buscando, medidores leyendo desde otro hilo y el cierre en mitad de
/// todo. No debe haber excepciones, interbloqueos, cuadros sin soltar ni decodificadores sin cerrar.
/// </summary>
[Trait("Categoria", "Estres")]
public sealed class TimelineStressTests
{
    private static readonly Rational Fps30 = Rational.Fps30;
    private static readonly string[] MediaIds = ["m0", "m1", "m2", "m3"];

    [Theory]
    [InlineData(101, false)]
    [InlineData(202, true)]
    [InlineData(303, false)]
    [InlineData(404, true)]
    public void Seeks_edits_and_live_mixer_changes_while_playing_never_race(int seed, bool asyncDecode)
    {
        var backend = new FakeTimelineBackend
        {
            RealThreads = true,
            AsyncDecode = asyncDecode,
            DecodeDelay = index => TimeSpan.FromMilliseconds(Math.Abs(HashCode.Combine(seed, index)) % 3),
            DisposeDelay = TimeSpan.FromMilliseconds(1),
        };
        var setup = new Random(seed);
        TimelineBuilder b = Build(backend, setup);
        var options = new TimelineSessionOptions
        {
            DecoderIdleTimeout = TimeSpan.FromMilliseconds(setup.Next(5, 80)),
            PartialFrameTimeout = TimeSpan.FromMilliseconds(setup.Next(0, 50)),
            MaxDecodersPerMedia = setup.Next(1, 4),
            AudioReadAhead = TimeSpan.FromMilliseconds(setup.Next(100, 400)),
        };
        TimelineSession session = TimelineSessionTests.Open(b, backend, options);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        session.DecodeFailed += (_, e) => failures.Enqueue($"{e.MediaId} cuadro {e.FrameIndex}: {e.Error.GetType().Name}: {e.Error.Message}");
        int steps = Stress.Iterations(1500);
        int closed = 0;

        Stress.Run(
            seed,
            ("ventana", (random, stop) =>
            {
                try
                {
                    for (int i = 0; i < steps && !stop.IsCancellationRequested; i++)
                    {
                        WindowStep(session, b, backend, random);
                    }
                }
                finally
                {
                    Volatile.Write(ref closed, 1);
                    Stress.Within(seed, "Dispose de la sesión", TimeSpan.FromSeconds(20), session.Dispose);
                }
            }),
            ("audio", (random, stop) => AudioThread(backend, random, () => Volatile.Read(ref closed) != 0 || stop.IsCancellationRequested)),
            ("medidores", (random, stop) => MeterThread(session, random, () => Volatile.Read(ref closed) != 0 || stop.IsCancellationRequested)));

        AssertReleased(seed, backend);
        Assert.True(failures.IsEmpty, $"Semilla {seed}: fallaron cuadros sin que ningún decodificador fallara: {string.Join("; ", failures.Take(5))}");
    }

    /// <summary>
    /// Cerrar la ventana durante cada operación larga: con cada decodificador ocupado en un cuadro que tarda, el cierre
    /// en un momento al azar (tras abrir, buscar, reproducir o editar) vuelve enseguida y no deja nada abierto.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Closing_during_any_long_operation_returns_promptly_and_releases_everything(int seed)
    {
        var random = new Random(seed);
        int rounds = Stress.Iterations(6);
        for (int round = 0; round < rounds; round++)
        {
            var backend = new FakeTimelineBackend
            {
                RealThreads = true,
                AsyncDecode = random.Next(2) == 0,
                DecodeDelay = _ => TimeSpan.FromMilliseconds(random.Next(2) == 0 ? 1 : 400),
            };
            TimelineBuilder b = Build(backend, new Random(HashCode.Combine(seed, round)));
            TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { DecoderIdleTimeout = TimeSpan.FromMilliseconds(20) });
            int operation = random.Next(4);
            switch (operation)
            {
                case 0:
                    break;
                case 1:
                    session.Seek(S(random.NextDouble() * 9));
                    break;
                case 2:
                    session.Play();
                    for (int i = 0; i < 5; i++)
                    {
                        backend.Clock.Advance(Time.FromFrames(1, Fps30));
                        session.Tick().Dispose();
                    }

                    break;
                default:
                    Relayout(b, random);
                    session.Reload(b.Project);
                    break;
            }

            Thread.Sleep(random.Next(0, 30));
            var watch = Stopwatch.StartNew();
            Stress.Within(seed, $"Dispose tras la operación {operation} (vuelta {round})", TimeSpan.FromSeconds(20), session.Dispose);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3),
                $"Semilla {seed}, vuelta {round}: cerrar tras la operación {operation} tardó {watch.Elapsed.TotalMilliseconds:0} ms.");
            AssertReleased(seed, backend);
        }
    }

    /// <summary>
    /// Regresión: el cierre no cancelaba el cuadro que cada decodificador estaba decodificando, así que esperaba a que
    /// terminara (en un 4K con seek, cientos de milisegundos por decodificador, uno tras otro).
    /// </summary>
    [Fact]
    public void Dispose_cancels_the_frames_being_decoded()
    {
        using var decoding = new CountdownEvent(1);
        var backend = new FakeTimelineBackend
        {
            RealThreads = true,
            DecodeDelay = _ =>
            {
                decoding.Signal();
                return TimeSpan.FromSeconds(30);
            },
        };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m0", Fps30, 3000, audio: false, backend: backend);
        b.AddVideo(0, "m0", Time.Zero, Time.Zero, S(10));
        TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false });
        session.Tick().Dispose();
        Assert.True(decoding.Wait(TimeSpan.FromSeconds(10)), "El decodificador no empezó.");

        var watch = Stopwatch.StartNew();
        Stress.Within(0, "Dispose", TimeSpan.FromSeconds(40), session.Dispose);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Cerrar esperó al cuadro en curso: {watch.Elapsed.TotalMilliseconds:0} ms.");
        Assert.Empty(backend.OpenVideos);
    }

    private static TimelineBuilder Build(FakeTimelineBackend backend, Random random)
    {
        var b = new TimelineBuilder(3, 2);
        foreach (string id in MediaIds)
        {
            b.AddMedia(id, Fps30, 900, backend: backend);
        }

        for (int t = 0; t < 3; t++)
        {
            Relayout(b, random, t);
        }

        b.AddAudio(0, "m0", Time.Zero, Time.Zero, S(5));
        b.AddAudio(0, "m1", S(5), S(1), S(5));
        b.AddAudio(1, "m2", S(1), Time.Zero, S(8));
        return b;
    }

    /// <summary>Rehace una pista de video: de uno a cuatro clips seguidos, de medios y puntos de entrada al azar.</summary>
    private static void Relayout(TimelineBuilder b, Random random, int? track = null)
    {
        int t = track ?? random.Next(b.Sequence.VideoTracks.Count);
        b.Sequence.VideoTracks[t].Clips.Clear();
        Time at = S(random.NextDouble() * 0.5);
        int clips = random.Next(1, 5);
        for (int i = 0; i < clips; i++)
        {
            Time duration = Time.FromFrames(random.Next(15, 120), Fps30);
            Time sourceIn = Time.FromFrames(random.Next(0, 600), Fps30);
            b.AddVideo(t, MediaIds[random.Next(MediaIds.Length)], at, sourceIn, duration);
            at += duration;
        }
    }

    private static void WindowStep(TimelineSession session, TimelineBuilder b, FakeTimelineBackend backend, Random random)
    {
        int roll = random.Next(100);
        if (roll < 45)
        {
            if (session.IsPlaying)
            {
                backend.Clock.Advance(Time.FromTicks(Time.TicksPerSecond * random.Next(5, 40) / 1000));
            }

            session.Tick().Dispose();
            if (random.Next(3) == 0)
            {
                // Como el refresco de la pantalla: deja avanzar a los decodificadores y a la mezcla entre medias.
                Thread.Sleep(random.Next(0, 3));
            }
        }
        else if (roll < 60)
        {
            session.Seek(S(random.NextDouble() * 11) - S(0.5));
        }
        else if (roll < 66)
        {
            session.TogglePlayPause();
        }
        else if (roll < 70)
        {
            session.StepFrame(random.Next(-3, 4));
        }
        else if (roll < 78)
        {
            Relayout(b, random);
            session.Reload(b.Project);
        }
        else if (roll < 86)
        {
            EditAudio(b, random);
            session.Reload(b.Project);
        }
        else if (roll < 92)
        {
            session.MonitorDynamics(random.Next(4) switch
            {
                0 => null,
                1 => new DynamicsMonitor(DynamicsMonitor.ForMix()),
                2 => new DynamicsMonitor(DynamicsMonitor.ForTrack("a0"), Listen: random.Next(2) == 0),
                _ => new DynamicsMonitor(DynamicsMonitor.ForTrack("a1"), Bypass: random.Next(2) == 0),
            });
        }
        else if (roll < 97)
        {
            var ids = b.Sequence.AudioTracks.SelectMany(t => t.Effects).Concat(b.Sequence.MasterEffects).Select(e => e.Id)
                .Where(_ => random.Next(2) == 0).ToHashSet();
            session.BypassInserts(random.Next(3) == 0 ? null : ids);
        }
        else
        {
            _ = session.Stats;
            session.ResetStats();
        }
    }

    /// <summary>Cambia los efectos del mezclador: el fader, un compresor o un ecualizador de una pista, o el compresor de la mezcla.</summary>
    private static void EditAudio(TimelineBuilder b, Random random)
    {
        List<EffectInstance> effects = random.Next(3) == 0 ? b.Sequence.MasterEffects : b.Sequence.AudioTracks[random.Next(2)].Effects;
        if (effects.Count > 0 && random.Next(3) == 0)
        {
            effects.RemoveAt(random.Next(effects.Count));
            return;
        }

        string id = $"e{random.Next(1_000_000)}";
        effects.Add(random.Next(3) switch
        {
            0 => new EffectInstance
            {
                Id = id,
                Type = BuiltInEffects.Audio,
                Parameters = { [BuiltInEffects.AudioParameters.GainDb] = new Parameter { Value = random.Next(-24, 6) } },
            },
            1 => new EffectInstance
            {
                Id = id,
                Type = BuiltInEffects.Compressor,
                Parameters = { [C.Threshold] = new Parameter { Value = -random.Next(5, 40) }, [C.Ratio] = new Parameter { Value = random.Next(2, 8) } },
            },
            _ => new EffectInstance
            {
                Id = id,
                Type = BuiltInEffects.Equalizer,
                Parameters = { [BuiltInEffects.EqualizerParameters.PresenceGain] = new Parameter { Value = random.Next(-6, 7) } },
            },
        });
    }

    /// <summary>Lo que hace el hilo del dispositivo: lee la mezcla a su ritmo y, a veces, la lleva a donde está el reloj.</summary>
    private static void AudioThread(FakeTimelineBackend backend, Random random, Func<bool> done)
    {
        Rodaje.Audio.IAudioSampleSource source = backend.Output!.Source;
        float[] buffer = new float[960 * 2];
        while (!done())
        {
            if (random.Next(50) == 0)
            {
                source.Seek(backend.Clock.Position);
            }

            int frames = random.Next(64, 960);
            int read = source.Read(buffer.AsSpan(0, frames * 2));
            Assert.InRange(read, 0, frames);
            for (int i = 0; i < read * 2; i++)
            {
                Assert.True(float.IsFinite(buffer[i]), $"La mezcla dio {buffer[i]}.");
            }

            Thread.Sleep(random.Next(0, 3));
        }
    }

    /// <summary>Los medidores del mezclador: se leen desde otro hilo, incluso mientras se desecha la sesión.</summary>
    private static void MeterThread(TimelineSession session, Random random, Func<bool> done)
    {
        while (!done())
        {
            Time to = S(random.NextDouble() * 10);
            Time from = to - S(0.05);
            _ = session.MeasureAudio(from, to, TimeSpan.FromMilliseconds(300));
            _ = session.MeasureLoudnessPower(to, TimeSpan.FromMilliseconds(400));
            _ = session.MeasureStrip(DynamicsMonitor.ForTrack("a" + random.Next(2)), from, to, TimeSpan.FromMilliseconds(300));
            _ = session.MeasureDynamics(from, to);
            Thread.Sleep(random.Next(0, 2));
        }
    }

    private static void AssertReleased(int seed, FakeTimelineBackend backend)
    {
        Stress.WaitUntil(seed, "soltar todos los cuadros", () => backend.Live.Value == 0, TimeSpan.FromSeconds(5));
        Assert.True(!backend.OpenVideos.Any(), $"Semilla {seed}: quedaron decodificadores abiertos: {string.Join(", ", backend.OpenVideos.Select(v => v.Path))}.");
    }
}
