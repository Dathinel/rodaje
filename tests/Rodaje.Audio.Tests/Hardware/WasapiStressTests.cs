using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Timeline;
using Rodaje.Playback;
using Rodaje.TestMedia;

namespace Rodaje.Audio.Tests.Hardware;

/// <summary>
/// La salida real bajo presión: transporte, volumen y lecturas del reloj desde varios hilos mientras otro la cierra en un
/// momento al azar. Después del cierre solo puede salir <see cref="ObjectDisposedException"/>, y nada se cuelga.
/// </summary>
/// <remarks>
/// Regresión: una orden de transporte que pasaba la comprobación de cerrado justo antes de <see cref="WasapiOutput.Dispose"/>
/// señalaba el evento del hilo de audio después de que el cierre lo liberara (un identificador cerrado o, peor, reutilizado).
/// Solo corre con un dispositivo de salida; reproduce silencio.
/// </remarks>
[Collection(nameof(DispositivoDeAudioReal))]
[Trait("Categoria", "Estres")]
public sealed class WasapiStressTests
{
    [AudioDeviceTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Transport_from_several_threads_while_closing_only_reports_disposed(int seed)
    {
        var random = new Random(seed);
        string env = Environment.GetEnvironmentVariable(Skip.LongVariable) ?? string.Empty;
        int rounds = env == "1" ? 20 : 3;
        for (int round = 0; round < rounds; round++)
        {
            var output = WasapiOutput.Open(new ToneSource(48_000, 440, amplitude: 0f));
            IAudioClock clock = output.Clock;
            int closed = 0;
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var threads = new List<Thread>();
            for (int t = 0; t < 3; t++)
            {
                var local = new Random(StressSeeds.Seed(seed, round, t));
                threads.Add(new Thread(() =>
                {
                    for (int i = 0; i < 2000 && Volatile.Read(ref closed) < 2; i++)
                    {
                        try
                        {
                            switch (local.Next(6))
                            {
                                case 0:
                                    clock.Start();
                                    break;
                                case 1:
                                    clock.Pause();
                                    break;
                                case 2:
                                    clock.Seek(Time.FromSeconds(local.NextDouble() * 10));
                                    break;
                                case 3:
                                    output.Volume = (float)local.NextDouble();
                                    break;
                                default:
                                    _ = clock.Position;
                                    _ = output.Stats;
                                    break;
                            }
                        }
                        catch (ObjectDisposedException) when (Volatile.Read(ref closed) > 0)
                        {
                        }
#pragma warning disable CA1031 // Se junta para fallar con la semilla.
                        catch (Exception ex)
#pragma warning restore CA1031
                        {
                            errors.Enqueue(ex.ToString());
                        }
                    }
                })
                { IsBackground = true });
            }

            threads.ForEach(t => t.Start());
            Thread.Sleep(random.Next(0, 80));
            Volatile.Write(ref closed, 1);
            var dispose = new Thread(output.Dispose) { IsBackground = true };
            dispose.Start();
            Assert.True(dispose.Join(TimeSpan.FromSeconds(10)), $"Semilla {seed}, vuelta {round}: Dispose no volvió.");
            Volatile.Write(ref closed, 2);
            foreach (Thread thread in threads)
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(20)), $"Semilla {seed}, vuelta {round}: un hilo de transporte quedó colgado.");
            }

            Assert.True(errors.IsEmpty, $"Semilla {seed}, vuelta {round}: {string.Join(Environment.NewLine, errors)}");
        }
    }
}
